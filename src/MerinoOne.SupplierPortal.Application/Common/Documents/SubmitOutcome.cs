namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3, Component 5. A two-state submit result that lets a submit handler model
/// the attachment-Warning "confirm to proceed" path WITHOUT throwing (a Warning is not an error). It is either:
/// <list type="bullet">
///   <item><b>Completed</b> — the transition happened; <see cref="Data"/> carries the detail DTO.</item>
///   <item><b>RequiresConfirmation</b> — one or more Warning-level attachments are missing and were not
///         acknowledged; <see cref="MissingWarning"/> names them and <see cref="ConfirmationMessage"/> is the
///         prompt. The transition did NOT happen. The caller re-submits with the acknowledge flag set to proceed.</item>
/// </list>
/// The controller maps this to <c>Result&lt;T&gt;</c>: Completed → 200 with Data; RequiresConfirmation → 200 with
/// <c>ConfirmationRequired = true</c> + the warning list (a flag, NOT an error — the two-step is a normal flow).
/// Mandatory failures never produce this type — they throw <c>ValidationException</c> (→ 400).
/// </summary>
public sealed record SubmitOutcome<T>
{
    public bool IsCompleted { get; init; }
    public T? Data { get; init; }

    public bool RequiresConfirmation => !IsCompleted;
    public string? ConfirmationMessage { get; init; }
    public IReadOnlyList<string> MissingWarning { get; init; } = Array.Empty<string>();

    /// <summary>The standard confirm prompt (UC-ATT-03), shared so every entity reads identically.</summary>
    public const string DefaultConfirmationMessage =
        "One or more required attachments were not uploaded, do you wish to proceed?";

    public static SubmitOutcome<T> Completed(T data) => new() { IsCompleted = true, Data = data };

    public static SubmitOutcome<T> Confirm(IReadOnlyList<string> missingWarning) => new()
    {
        IsCompleted = false,
        ConfirmationMessage = DefaultConfirmationMessage,
        MissingWarning = missingWarning,
    };
}
