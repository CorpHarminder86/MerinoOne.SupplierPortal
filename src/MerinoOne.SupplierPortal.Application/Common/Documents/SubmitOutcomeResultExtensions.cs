using MerinoOne.SupplierPortal.Application.Common.Models;

namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3, Component 5. Maps a <see cref="SubmitOutcome{T}"/> from a submit
/// handler to the API <see cref="Result{T}"/> envelope so the controllers stay one-liners:
/// <list type="bullet">
///   <item>Completed → <c>Result&lt;T&gt;.Ok(data)</c> (200 with the detail DTO).</item>
///   <item>RequiresConfirmation → <c>Result&lt;T&gt;.NeedsConfirmation(...)</c> (200 with the prompt + the missing
///         Warning type names; NOT an error).</item>
/// </list>
/// </summary>
public static class SubmitOutcomeResultExtensions
{
    public static Result<T> ToResult<T>(this SubmitOutcome<T> outcome, string? traceId = null)
        => outcome.IsCompleted
            ? Result<T>.Ok(outcome.Data!, traceId)
            : Result<T>.NeedsConfirmation(
                outcome.ConfirmationMessage ?? SubmitOutcome<T>.DefaultConfirmationMessage,
                outcome.MissingWarning, traceId);
}
