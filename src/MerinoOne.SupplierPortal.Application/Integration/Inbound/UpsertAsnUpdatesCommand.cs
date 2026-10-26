using System.Globalization;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

// R17 (2026-10-26) — inbound ASN receipt update (POST /inbound/asn-update). Infor LN reports back on an ASN the portal
// already POSTED: its own shipment number, the shipment status, and per line the quantity it received and the date.
// Runs through the executor's TRANSACTIONAL overload (literal resolved company, no share-group normalization, endpoint
// gate, canonical-hash idempotency, SyncLog/IntegrationError, session telemetry) — the same shape as the goods-receipt
// handlers. Every read uses IgnoreQueryFilters (the service principal has no seccode/company context) and is bounded by
// tenant + the resolved company explicitly.
//
// Semantics that matter (F2 AC2–AC7):
//   • ATOMIC PER ASN. Everything for one ASN record is resolved first (supplier, ASN, from-state, shipment, every line);
//     only if ALL of it resolves is anything written. One bad line fails the whole ASN record and writes nothing for it.
//     Other ASN records in the same batch are independent (poison-row isolation, as the executor already provides).
//   • A business failure is a Failed ROW (200 + RowOutcome.Failed + a clear Error), never a 4xx — LN reads the per-row
//     result, and a 4xx would make it retry the whole batch because of one bad ASN.
//   • The shipment status is stored VERBATIM (Asn.ErpShipmentStatus). Only InTransit / Delivered / Received also move the
//     portal AsnStatus (backward moves allowed — ERP is the truth); any other status leaves AsnStatus alone.
//   • receivedQty / actualDeliveryDate are ABSOLUTE replacements; an omitted (null) value leaves the stored one alone.
//   • The shipment stamps Asn.ErpCode when blank (it is the ASN's IDM eligibility gate — R11.2). It is never overwritten:
//     a non-blank ErpCode that differs from the pushed shipment is a Failed row. Because a blank → value stamp can only
//     happen on an ASN that has no synced IDM document yet (the gate is that very field), no IDM owner-update enqueue is
//     needed here, unlike a changed value on the erp-ack path.
public record UpsertAsnUpdatesCommand(PushAsnUpdatesRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey)
    : IRequest<UpsertResultDto>;

public class UpsertAsnUpdatesCommandValidator : AbstractValidator<UpsertAsnUpdatesCommand>
{
    // Column widths: Asn.AsnNumber / Asn.ErpCode / Supplier.ErpCode are nvarchar(50); Asn.ErpShipmentStatus is nvarchar(30);
    // AsnLine.ReceivedQty is decimal(18,4) → 14 integer digits.
    private const int StatusMax = 30;
    private const int Max50 = 50;
    private const int PoNumberMax = 60;
    private const decimal QtyMax = 99_999_999_999_999.9999m;

    public UpsertAsnUpdatesCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Asn)
            .NotEmpty().WithMessage("At least one ASN record is required.")
            .Must(a => a == null || a.Count <= 1000).WithMessage("A batch may contain at most 1000 ASN records.")
            .Must(a => a == null || a.All(r => r is not null)).WithMessage("ASN records must not be null.");

        RuleForEach(x => x.Body.Asn).ChildRules(a =>
        {
            a.RuleFor(r => r.ErpSupplierCode).NotEmpty().Must(s => s.Trim().Length <= Max50)
                .WithMessage("erpSupplierCode must be at most 50 characters.");
            a.RuleFor(r => r.SupplierAsn).NotEmpty().Must(s => s.Trim().Length <= Max50)
                .WithMessage("supplierASN must be at most 50 characters.");
            // shipment is stamped onto Asn.ErpCode (nvarchar(50)) — reject over-long values here, not as a DB truncation.
            a.RuleFor(r => r.Shipment).NotEmpty().Must(s => s.Trim().Length <= Max50)
                .WithMessage("shipment must be at most 50 characters.");
            a.RuleFor(r => r.Status).NotEmpty().Must(s => s.Trim().Length <= StatusMax)
                .WithMessage($"status must be at most {StatusMax} characters.");

            // Lines are optional (a status-only update), but when present every line is validated.
            a.RuleFor(r => r.Lines)
                .Must(l => l == null || l.All(x => x is not null)).WithMessage("Lines must not contain null entries.");
            a.RuleForEach(r => r.Lines).ChildRules(l =>
            {
                l.RuleFor(x => x.PositionNo).GreaterThan(0).WithMessage("positionNo must be greater than 0.");
                l.RuleFor(x => x.ReceivedQty)
                    .GreaterThanOrEqualTo(0m).WithMessage("receivedQty must be >= 0.")
                    .LessThanOrEqualTo(QtyMax).WithMessage("receivedQty is too large.")
                    .When(x => x.ReceivedQty.HasValue);
                l.RuleFor(x => x.PoNumber).MaximumLength(PoNumberMax);
            });
            a.RuleFor(r => r.Lines)
                .Must(NoDuplicateLineKeys)
                .When(r => r.Lines is { Count: > 0 } && r.Lines.All(x => x is not null))
                .WithMessage("Duplicate (positionNo, poNumber) lines are not allowed within one ASN record.");
        });
    }

    /// <summary>Line key = (positionNo, poNumber case-insensitive); a missing/blank poNumber is its own key.</summary>
    private static bool NoDuplicateLineKeys(IReadOnlyList<AsnUpdateLineRecord>? lines)
    {
        if (lines is null) return true;
        var seen = new HashSet<(int, string)>();
        foreach (var l in lines)
            if (!seen.Add((l.PositionNo, (UpsertAsnUpdatesCommandHandler.NormalizePo(l.PoNumber) ?? string.Empty).ToUpperInvariant())))
                return false;
        return true;
    }
}

public class UpsertAsnUpdatesCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertAsnUpdatesCommand, UpsertResultDto>
{
    private const string Actor = "infor:inbound";

    /// <summary>States an ERP update may act on: the ASN has been posted to LN (Submitted onwards). Received is allowed so a correction can follow.</summary>
    private static readonly HashSet<AsnStatus> UpdatableStates = new()
    {
        AsnStatus.Submitted, AsnStatus.InTransit, AsnStatus.Delivered, AsnStatus.Received,
    };

    public Task<UpsertResultDto> Handle(UpsertAsnUpdatesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Asn;
        var companyCode = request.Body.CompanyCode.Trim();

        // Canonical digest for the no-header idempotency fallback: every field the handler acts on, so a follow-up push that
        // changes a status, quantity or date hashes differently and is NOT short-circuited as a duplicate. Line order is
        // irrelevant to the outcome, so the line segments are sorted.
        var canonical = recs.Select(r =>
            $"{r.ErpSupplierCode.Trim().ToUpperInvariant()}|{r.SupplierAsn.Trim().ToUpperInvariant()}|{r.Shipment.Trim().ToUpperInvariant()}|{r.Status.Trim()}|" +
            string.Join(";", (r.Lines ?? Array.Empty<AsnUpdateLineRecord>())
                .Select(l => string.Create(CultureInfo.InvariantCulture,
                    $"{l.PositionNo}/{(NormalizePo(l.PoNumber) ?? string.Empty).ToUpperInvariant()}/{l.ReceivedQty}/{l.ActualDeliveryDate:yyyy-MM-dd}"))
                .OrderBy(s => s, StringComparer.Ordinal)));
        var codes = recs.Select(r => r.SupplierAsn.Trim());

        return exec.ExecuteAsync(TransactionalInboundEntity.AsnUpdate, request.Body.CompanyCode, request.BoundCompanyIds,
            request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;

            // ---- Suppliers: Supplier.ErpCode within the key's tenant (the same resolution the PO inbound uses). ----
            // A list per code: ErpCode has no unique index, so more than one supplier could carry it; the ASN decides which.
            var erpCodes = recs.Select(r => r.ErpSupplierCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var supplierRows = await db.Suppliers.IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && s.TenantId == tenantId && s.ErpCode != null && erpCodes.Contains(s.ErpCode))
                .Select(s => new { s.Id, s.ErpCode })
                .ToListAsync(token);
            var supplierIdsByErp = supplierRows
                .GroupBy(s => s.ErpCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToHashSet(), StringComparer.OrdinalIgnoreCase);

            // ---- ASNs (+ their live lines), bounded by tenant AND the resolved company; soft-deleted rows excluded. ----
            var asnNumbers = recs.Select(r => r.SupplierAsn.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var asns = await db.Asns.IgnoreQueryFilters()
                .Include(a => a.Lines.Where(l => !l.IsDeleted))
                .Where(a => !a.IsDeleted && a.TenantId == tenantId && a.TenantEntityId == sourceId && asnNumbers.Contains(a.AsnNumber))
                .ToListAsync(token);
            var asnByNumber = asns
                .GroupBy(a => a.AsnNumber, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // ---- PO number per PO line, to disambiguate a position that repeats across the POs of one ASN. ----
            var poLineIds = asns.SelectMany(a => a.Lines).Select(l => l.PurchaseOrderLineId).Distinct().ToList();
            var poNumberByLine = poLineIds.Count == 0
                ? new Dictionary<Guid, string>()
                : (await db.PurchaseOrderLines.IgnoreQueryFilters()
                        .Where(l => poLineIds.Contains(l.Id))
                        .Select(l => new { l.Id, PoNumber = l.PurchaseOrder!.PoNumber })
                        .ToListAsync(token))
                    .ToDictionary(x => x.Id, x => x.PoNumber);

            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
                results.Add(Apply(rec));
            return results;

            RowResult Apply(AsnUpdateRecord rec)
            {
                var code = rec.SupplierAsn.Trim();
                var erpSupplier = rec.ErpSupplierCode.Trim();
                var shipment = rec.Shipment.Trim();
                var status = rec.Status.Trim();

                RowResult Fail(string error) => new(code, RowOutcome.Failed, error);

                // (a) supplier
                if (!supplierIdsByErp.TryGetValue(erpSupplier, out var supplierIds))
                    return Fail($"Unknown erpSupplierCode '{erpSupplier}'.");

                // (b) ASN — by number, owned by that supplier, in the resolved company
                if (!asnByNumber.TryGetValue(code, out var asn) || !supplierIds.Contains(asn.SupplierId))
                    return Fail($"ASN '{code}' not found for supplier '{erpSupplier}' in company '{companyCode}'.");

                // (c) from-state — only a posted ASN is something LN can know about
                if (!UpdatableStates.Contains(asn.AsnStatus))
                    return Fail($"ASN '{code}' is '{asn.AsnStatus}'; only posted ASNs can be updated by ERP.");

                // (d) shipment — LN's own number; stamped when the ASN has none yet, never overwritten
                var heldShipment = asn.ErpCode?.Trim();
                var stampErpCode = string.IsNullOrEmpty(heldShipment);
                if (!stampErpCode && !string.Equals(heldShipment, shipment, StringComparison.OrdinalIgnoreCase))
                    return Fail($"Shipment mismatch: ASN '{code}' carries '{heldShipment}', ERP sent '{shipment}'.");

                // (e) lines — resolve EVERY payload line before touching anything
                var plan = new List<(AsnLine Line, decimal? Qty, DateTime? Date)>();
                var claimed = new HashSet<Guid>();
                var liveLines = asn.Lines.Where(l => !l.IsDeleted).ToList();
                foreach (var pl in rec.Lines ?? Array.Empty<AsnUpdateLineRecord>())
                {
                    var po = NormalizePo(pl.PoNumber);
                    var onPo = po is null ? string.Empty : $" on PO {po}";
                    var candidates = liveLines
                        .Where(l => l.PositionNo == pl.PositionNo
                                    && (po is null
                                        || (poNumberByLine.TryGetValue(l.PurchaseOrderLineId, out var lpo)
                                            && string.Equals(lpo, po, StringComparison.OrdinalIgnoreCase))))
                        .ToList();
                    if (candidates.Count == 0)
                        return Fail($"No ASN line at position {pl.PositionNo}{onPo}.");
                    if (candidates.Count > 1)
                        return Fail($"Position {pl.PositionNo} matches {candidates.Count} ASN lines{onPo}; send poNumber to disambiguate.");

                    var target = candidates[0];
                    if (!claimed.Add(target.Id))
                        return Fail($"More than one line resolves to the ASN line at position {pl.PositionNo}{onPo}.");

                    decimal? qty = pl.ReceivedQty is { } rq ? decimal.Round(rq, 4, MidpointRounding.AwayFromZero) : null;
                    plan.Add((target, qty, pl.ActualDeliveryDate?.Date));
                }

                // (f)/(g) every check passed — write. Nothing above mutated anything.
                var headerChanged = false;
                var anyLineChanged = false;

                if (!string.Equals(asn.ErpShipmentStatus, status, StringComparison.Ordinal))
                {
                    asn.ErpShipmentStatus = status;
                    headerChanged = true;
                }
                if (MapToAsnStatus(status) is { } mapped && asn.AsnStatus != mapped)
                {
                    asn.AsnStatus = mapped;
                    headerChanged = true;
                }
                if (stampErpCode)
                {
                    asn.ErpCode = shipment;
                    headerChanged = true;
                }
                if (headerChanged)
                {
                    asn.UpdatedBy = Actor;
                    asn.UpdatedOn = now;
                }

                foreach (var (line, qty, date) in plan)
                {
                    var lineChanged = false;
                    if (qty is { } q && line.ReceivedQty != q)
                    {
                        line.ReceivedQty = q;
                        lineChanged = true;
                    }
                    if (date is { } d && line.ActualDeliveryDate?.Date != d)
                    {
                        line.ActualDeliveryDate = d;
                        lineChanged = true;
                    }
                    if (!lineChanged) continue;
                    line.UpdatedBy = Actor;
                    line.UpdatedOn = now;
                    anyLineChanged = true;
                }

                // (h) outcome
                return new RowResult(code, headerChanged || anyLineChanged ? RowOutcome.Updated : RowOutcome.Skipped, null);
            }
        }
    }

    /// <summary>A blank poNumber is treated as "not supplied" (null) everywhere — validation, matching and the digest.</summary>
    internal static string? NormalizePo(string? poNumber)
        => string.IsNullOrWhiteSpace(poNumber) ? null : poNumber.Trim();

    /// <summary>
    /// LN's shipment status → the portal lifecycle state. Spaces, hyphens and underscores are ignored and the comparison is
    /// case-insensitive ("In Transit", "in-transit", "IN_TRANSIT" all map). Anything else is not a portal state: null, and
    /// the ASN's AsnStatus is left alone (the verbatim text still lands on Asn.ErpShipmentStatus).
    /// </summary>
    private static AsnStatus? MapToAsnStatus(string status)
    {
        var key = new string(status.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray());
        if (key.Equals("InTransit", StringComparison.OrdinalIgnoreCase)) return AsnStatus.InTransit;
        if (key.Equals("Delivered", StringComparison.OrdinalIgnoreCase)) return AsnStatus.Delivered;
        if (key.Equals("Received", StringComparison.OrdinalIgnoreCase)) return AsnStatus.Received;
        return null;
    }
}
