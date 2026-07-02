namespace MerinoOne.Web.Components.Shared;

/// <summary>
/// R7 (TSD R7 §2.2) — fulfilment summary the <see cref="AttachmentPanel"/> raises after every load/refresh so a
/// host (via <c>DocumentRail</c> / <c>DocumentRailPill</c>) can show the live count ("Attachments 3/4") and derive
/// the rail's §2.3 default open/closed state WITHOUT re-querying the panel read-model. Web-side only — this is a
/// render-state signal, not a contract DTO, and it carries no enforcement semantics (the Mandatory/Warning gate
/// stays at the server AttachmentSubmitGuard).
/// </summary>
/// <param name="SatisfiedSlots">Slots with at least one uploaded file.</param>
/// <param name="TotalSlots">All policy slots the panel rendered (0 ⇒ no policy ⇒ panel renders nothing).</param>
/// <param name="TotalFiles">Total uploaded files across all slots.</param>
/// <param name="AnyMandatoryEmpty">Any Mandatory slot with zero files — drives the §2.3 editable-host default open.</param>
/// <param name="AnyRequiredEmpty">Any non-Optional (Mandatory or Warning) slot with zero files.</param>
public sealed record AttachmentPanelSummary(
    int SatisfiedSlots,
    int TotalSlots,
    int TotalFiles,
    bool AnyMandatoryEmpty,
    bool AnyRequiredEmpty);
