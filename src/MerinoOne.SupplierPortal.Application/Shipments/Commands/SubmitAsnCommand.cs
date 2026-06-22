using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 3, the core ASN orchestration. On submit (Draft -> Submitted) this, in ONE
/// transaction:
/// <list type="number">
///   <item>asserts Draft (else 409); validates over-ship, lot/serial (per Item flags), and the mixed-currency
///         guard R16 (all covered POs share one currency);</item>
///   <item>flips to Submitted, stamps submittedAt/by; from here Update + attachment-mutation are LOCKED;</item>
///   <item>stamps the ASN's <c>ErpSyncId</c> = the deterministic outbox key (the ERP correlation id);</item>
///   <item>creates EXACTLY ONE draft <see cref="Domain.Entities.Proc.Invoice"/> spanning all the ASN's POs via
///         <see cref="DraftInvoiceFromAsnFactory"/> (UQ_Invoice_asnId upsert-or-skip);</item>
///   <item>enqueues the ASN->ERP post on the Increment-0 outbox (post-commit dispatch; the draft invoice is
///         portal-internal and NOT gated on the LN result).</item>
/// </list>
/// ASN status + junction (unchanged) + draft Invoice + Outbox row all commit in ONE <c>SaveChangesAsync</c>.
/// </summary>
public record SubmitAsnCommand(Guid Id) : IRequest<AsnDetailDto>;

public class SubmitAsnCommandValidator : AbstractValidator<SubmitAsnCommand>
{
    public SubmitAsnCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class SubmitAsnCommandHandler : IRequestHandler<SubmitAsnCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IOutboxDispatcher _outbox;
    private readonly DraftInvoiceFromAsnFactory _invoiceFactory;

    public SubmitAsnCommandHandler(
        IAppDbContext db, ICurrentUser user, IOutboxDispatcher outbox, DraftInvoiceFromAsnFactory invoiceFactory)
    {
        _db = db; _user = user; _outbox = outbox; _invoiceFactory = invoiceFactory;
    }

    public async Task<AsnDetailDto> Handle(SubmitAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        if (asn.AsnStatus != AsnStatus.Draft)
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; only a Draft ASN can be submitted.");

        var now = DateTime.UtcNow;

        // ---- Validation: lines + over-ship + lot/serial -------------------------------------------------
        var lineCtx = await (from al in _db.AsnLines
                             join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                             where al.AsnId == asn.Id
                             select new
                             {
                                 al.Id,
                                 al.ShippedQty,
                                 al.BatchNumber,
                                 al.PositionNo,
                                 pol.OrderQty,
                                 pol.ItemCode,
                                 pol.ItemId,
                             }).ToListAsync(ct);

        if (lineCtx.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "Cannot submit an ASN with no lines." }
            });

        // Item control flags (Addendum A3) for lot/serial validation.
        var itemIds = lineCtx.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).Distinct().ToList();
        var itemFlags = await _db.Items
            .Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.IsLotControlled, i.IsSerialized })
            .ToDictionaryAsync(i => i.Id, ct);

        var errors = new Dictionary<string, List<string>>();
        void AddErr(string key, string msg)
        {
            if (!errors.TryGetValue(key, out var list)) { list = new(); errors[key] = list; }
            list.Add(msg);
        }

        foreach (var l in lineCtx)
        {
            // Over-ship: block submit when shipped exceeds ordered (the draft invoice would otherwise be capped,
            // but the supplier should reconcile the ASN first — plan default treats this as a blocking error).
            if (l.OrderQty > 0 && l.ShippedQty > l.OrderQty)
                AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) ships {l.ShippedQty} > ordered {l.OrderQty} (over-ship).");

            if (l.ItemId.HasValue && itemFlags.TryGetValue(l.ItemId.Value, out var flags))
            {
                if (flags.IsLotControlled && string.IsNullOrWhiteSpace(l.BatchNumber))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is lot-controlled; BatchNumber is required.");

                // Q-SERIAL: serialized items require serial numbers, but there is NO serial child table on AsnLine
                // today. Per the plan's Phase-1 note we validate PRESENCE only (the BatchNumber field is reused to
                // carry a serial marker for serialized items) and FLAG Q-Serial for solution-architect to decide
                // whether a dedicated AsnLineSerial child table is needed. We do NOT invent the table here.
                if (flags.IsSerialized && string.IsNullOrWhiteSpace(l.BatchNumber))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is serialized; serial number(s) are required (Q-Serial: no serial child table yet — supply in BatchNumber for Phase 1).");
            }
        }

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()));

        // ---- Flip to Submitted + stamp + ERP correlation key --------------------------------------------
        // deterministic — reused across retries; tenant-qualified (review B2).
        var key = OutboxKey.For(OutboxEntity.Asn, asn.TenantId, asn.AsnNumber, "submit");
        asn.AsnStatus = AsnStatus.Submitted;
        asn.SubmittedAt = now;
        asn.SubmittedBy = _user.UserCode;
        asn.ErpSyncId = key;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // ---- Single draft invoice spanning all the ASN's POs (R16 currency guard inside; upsert-or-skip) -
        await _invoiceFactory.EnsureDraftAsync(asn, now, ct);

        // ---- Outbox: ASN -> ERP post, dispatched POST-COMMIT (never an LN HTTP call inside this txn) ------
        await _outbox.EnqueueAsync(OutboxTransactionType.AsnPost, OutboxEntity.Asn, asn.Id, key, null, ct);

        // ONE transaction: ASN status + draft Invoice (+ lines) + Outbox row.
        await _db.SaveChangesAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct);
    }
}
