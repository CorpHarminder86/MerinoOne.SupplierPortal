using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R5 (TSD R5 Addendum §11 / Component 7) — the PURE, DB-less decision for the inbound-PO status derivation
/// rule (§11.3). The ERP→portal status MAPPING (which raw <c>erpStatus</c> resolves to which portal
/// <see cref="PoStatus"/>) lives in the configurable <c>PoStatusMapping</c> master (resolved by
/// <see cref="IPoStatusMap"/>); the material-change judgement lives in <see cref="PoMaterialChange"/>. This
/// resolver COMPOSES those two inputs into the final "what should PoStatus become" answer — it owns ONLY the
/// §11.3 first-receipt / re-receipt / terminal / no-clobber logic and is therefore trivially unit-testable.
///
/// <para><b>Rules (§11.3):</b></para>
/// <list type="bullet">
///   <item><b>Unmapped</b> (<paramref name="mappedCandidate"/> is null) → leave PoStatus unchanged, flag
///         <see cref="PoStatusResolution.Unmapped"/> so the caller writes a Sync Log Failed row.</item>
///   <item><b>First receipt</b> (<paramref name="currentStatus"/> is null) → take the candidate (typically
///         Draft or Released).</item>
///   <item><b>Re-receipt, terminal candidate</b> (Cancelled / Closed) → take it UNCONDITIONALLY.</item>
///   <item><b>Re-receipt, Released candidate</b> → re-arm to Released ONLY when the change is material
///         (R4 §6.4); otherwise leave unchanged (no-clobber — a routine re-sync must not reset supplier
///         progress like Accepted).</item>
///   <item><b>Re-receipt, Draft candidate</b> → leave unchanged (Draft is a first-receipt-only state).</item>
/// </list>
///
/// <para>This reproduces the R4 §6.3 hardcoded <c>Modify → Released</c> behaviour when the seeded default
/// maps <c>modified → Released</c> — the reset still composes with the material-change gate.</para>
/// </summary>
public static class PoStatusResolver
{
    /// <summary>
    /// Applies the §11.3 rule. <paramref name="currentStatus"/> is the PO's current portal status (null on a
    /// first receipt — the PO does not yet exist). <paramref name="mappedCandidate"/> is the portal status the
    /// incoming <c>erpStatus</c> resolved to via the map (null = UNMAPPED). <paramref name="isMaterialChange"/>
    /// is the result of <see cref="PoMaterialChange.IsMaterial"/> for this re-push (ignored on a first receipt
    /// and for terminal candidates). Returns the new status to APPLY (null = leave PoStatus untouched) plus the
    /// Unmapped flag.
    /// </summary>
    public static PoStatusResolution Resolve(PoStatus? currentStatus, PoStatus? mappedCandidate, bool isMaterialChange)
    {
        // 1. UNMAPPED — the erpStatus has no mapping row. Change nothing; signal the caller to Sync-Log Fail.
        if (mappedCandidate is null)
            return new PoStatusResolution(NewStatus: null, Unmapped: true);

        var candidate = mappedCandidate.Value;

        // 2. FIRST RECEIPT — the PO is being created. Take the mapped candidate (typically Draft or Released).
        if (currentStatus is null)
            return new PoStatusResolution(NewStatus: candidate, Unmapped: false);

        // 3. RE-RECEIPT of an existing PO.
        // 3a. Terminal ERP states (Cancelled / Closed) apply UNCONDITIONALLY — they win over any local progress.
        if (candidate is PoStatus.Cancelled or PoStatus.Closed)
            return new PoStatusResolution(NewStatus: candidate, Unmapped: false);

        // 3b. Released is a reset / re-arm — apply it ONLY when the change is genuinely material (R4 §6.4); a
        //     routine non-material re-sync must NOT clobber supplier progress (e.g. an Accepted PO stays Accepted).
        if (candidate is PoStatus.Released)
            return isMaterialChange
                ? new PoStatusResolution(NewStatus: PoStatus.Released, Unmapped: false)
                : new PoStatusResolution(NewStatus: null, Unmapped: false);

        // 3c. Draft (or any other non-terminal, non-Released candidate) is a first-receipt-only state — ignore on
        //     a re-receipt (leave the current status). Delivered is handled here too: a mapped Delivered on a
        //     re-receipt is taken (it is not terminal-Cancelled/Closed and not Released) → fall through to "take it".
        //     §11.2: Delivered is ERP-driven and advances a FullyShipped PO. It is NOT a no-clobber Released reset,
        //     so apply it directly.
        if (candidate is PoStatus.Delivered)
            return new PoStatusResolution(NewStatus: PoStatus.Delivered, Unmapped: false);

        // Draft (the only remaining ERP-driven target) → leave unchanged on a re-receipt.
        return new PoStatusResolution(NewStatus: null, Unmapped: false);
    }
}

/// <summary>
/// The outcome of <see cref="PoStatusResolver.Resolve"/>. <see cref="NewStatus"/> null = leave the PO's
/// PoStatus unchanged. <see cref="Unmapped"/> true = the erpStatus had no mapping (the caller writes a Sync
/// Log Failed row and leaves PoStatus alone).
/// </summary>
public readonly record struct PoStatusResolution(PoStatus? NewStatus, bool Unmapped);
