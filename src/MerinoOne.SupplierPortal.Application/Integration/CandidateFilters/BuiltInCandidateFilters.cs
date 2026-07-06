using System.Linq.Expressions;
using System.Text.Json;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierChangeRequestEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierChangeRequest;

namespace MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;

/// <summary>
/// R9 (TSD R9 §2.5a) — the seed candidate-filter registry: named EF predicates the reconciliation sweep
/// and backfill entity-scan compile into indexed SQL WHERE clauses. One correctness rule: each filter
/// must be a SUPERSET of its gate-eligible rows (over-inclusive is fine — the gate rejects extras;
/// under-inclusive means rows the sweep never sees).
///
/// ── PHASE B ENTRY GATE (TSD §2.5a marker) ─────────────────────────────────────────────────────────
/// These predicates are wired to NOTHING in Phase A (registry validation + config dropdown only).
/// Before ANY endpoint flips Dynamic in Phase B, reconfirm every predicate against the live enums and
/// prove the superset guarantee via LnCandidateFilterSupersetTests (one gate-passing sample per filter
/// must match its filter). Field names verified against the entities on 2026-07-06.
/// ──────────────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class BuiltInCandidateFilters
{
    /// <summary>
    /// InvoicePost. Includes <c>Matched</c> alongside <c>Submitted</c> — the GRN auto-post claim accepts
    /// BOTH (<c>UpsertGoodsReceiptStatusCommand</c> guard (c): status IN (Submitted, Matched)), so a
    /// Submitted-only filter would be UNDER-inclusive (TSD V2.1 §2.5a correction #7). Invoice is the one
    /// entity with a cheap posted-marker (<c>ErpPostedAt</c>) — used.
    /// </summary>
    [CandidateFilter(LnPortalEntity.Invoice, "InvoiceSubmittedUnposted")]
    public static Expression<Func<Invoice, bool>> InvoiceSubmittedUnposted() =>
        i => (i.InvoiceStatus == InvoiceStatus.Submitted || i.InvoiceStatus == InvoiceStatus.Matched)
             && i.ErpPostedAt == null && !i.IsDeleted;

    /// <summary>AsnPost. Over-includes already-posted ASNs — the scanner's deterministic-key exclusion handles it.</summary>
    [CandidateFilter(LnPortalEntity.Asn, "AsnSubmitted")]
    public static Expression<Func<Asn, bool>> AsnSubmitted() =>
        a => a.AsnStatus == AsnStatus.Submitted && !a.IsDeleted;

    /// <summary>
    /// Parameterized built-in for the three PO-response configs: params <c>{"statuses":["Acknowledged"]}</c>
    /// (PoAcknowledge), <c>["Accepted"]</c> (PoAccept), <c>["Rejected"]</c> (PoReject) — one filter, three configs.
    /// </summary>
    [CandidateFilter(LnPortalEntity.PurchaseOrder, "StatusIn")]
    public static Expression<Func<PurchaseOrder, bool>> PurchaseOrderStatusIn(JsonElement paramsJson)
    {
        var statuses = ParseStatuses<PoStatus>(paramsJson);
        return p => statuses.Contains(p.PoStatus) && !p.IsDeleted;
    }

    /// <summary>SupplierChange. Approved-only is deliberately over-inclusive vs Pushed/PartiallyPushed rows — key exclusion dedupes.</summary>
    [CandidateFilter(LnPortalEntity.SupplierChange, "SupplierChangeApproved")]
    public static Expression<Func<SupplierChangeRequestEntity, bool>> SupplierChangeApproved() =>
        c => c.ChangeStatus == ChangeRequestStatus.Approved && !c.IsDeleted;

    /// <summary>
    /// SupplierSync (onboarding push). Field names corrected from the TSD's <c>[Guessing]</c> seed:
    /// the onboarding state lives on <c>Supplier.RegistrationStatus</c> (no OnboardingStatus exists) and
    /// the ERP handle is <c>Supplier.ErpCode</c>.
    /// </summary>
    [CandidateFilter(LnPortalEntity.Supplier, "SupplierRegistrationApprovedNoErpCode")]
    public static Expression<Func<SupplierEntity, bool>> SupplierRegistrationApprovedNoErpCode() =>
        s => s.RegistrationStatus == RegistrationStatus.Approved && s.ErpCode == null && !s.IsDeleted;

    /// <summary>PoNegotiationApprove. Over-includes already-pushed rounds — key exclusion handles it (§2.5a note).</summary>
    [CandidateFilter(LnPortalEntity.PoNegotiation, "PoNegotiationApproved")]
    public static Expression<Func<PurchaseOrderNegotiation, bool>> PoNegotiationApproved() =>
        n => n.NegotiationStatus == PoNegotiationStatus.Approved && !n.IsDeleted;

    private static List<TEnum> ParseStatuses<TEnum>(JsonElement paramsJson) where TEnum : struct, Enum
    {
        if (paramsJson.ValueKind != JsonValueKind.Object || !paramsJson.TryGetProperty("statuses", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("StatusIn params must be {\"statuses\":[\"...\"]}.");
        var statuses = new List<TEnum>();
        foreach (var el in arr.EnumerateArray())
        {
            var raw = el.GetString();
            if (!Enum.TryParse<TEnum>(raw, ignoreCase: false, out var parsed))
                throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} value '{raw}' in StatusIn params.");
            statuses.Add(parsed);
        }
        if (statuses.Count == 0) throw new InvalidOperationException("StatusIn params must name at least one status.");
        return statuses;
    }
}
