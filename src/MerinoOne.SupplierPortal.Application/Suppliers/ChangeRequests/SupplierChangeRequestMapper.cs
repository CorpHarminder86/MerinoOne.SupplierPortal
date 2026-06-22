using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Domain.Enums;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests;

/// <summary>
/// Entity → DTO projection for the change-request detail + lines, shared by the commands (which return the
/// freshly-mutated aggregate) and the by-id query. Lines are projected from the parent's <c>Lines</c> collection
/// (there is intentionally no root DbSet for lines). Soft-deleted lines are excluded.
/// </summary>
public static class SupplierChangeRequestMapper
{
    public static SupplierChangeRequestDto ToDto(SupplierChangeRequest r, string supplierCode, string supplierLegalName)
        => new(
            r.Id,
            r.Seq,
            r.SupplierId,
            supplierCode,
            supplierLegalName,
            r.ChangeStatus.ToString(),
            r.Summary,
            r.RequestedBy,
            r.RequestedAt,
            r.ReviewedBy,
            r.ReviewedAt,
            r.RejectionReason,
            r.CreatedOn,
            r.Lines
                .Where(l => !l.IsDeleted)
                .OrderBy(l => l.CreatedOn)
                .Select(ToLineDto)
                .ToList());

    public static SupplierChangeRequestLineDto ToLineDto(SupplierChangeRequestLine l)
        => new(
            l.Id,
            l.TargetEntity.ToString(),
            l.TargetEntityId,
            l.Operation.ToString(),
            l.FieldName,
            l.OldValue,
            l.NewValue,
            l.PayloadJson,
            l.PushStatus.ToString(),
            l.PushedAt,
            l.ErpRef);

    /// <summary>
    /// Builds a delta line from a request input (shared by create + update). For Edit we carry the field/new-value;
    /// for Add the verbatim payloadJson (no existing row to diff). oldValue is captured later for the diff view; the
    /// authoritative before/after is also covered by the audit interceptor on apply. Assumes the input has already
    /// passed <see cref="SupplierChangeLineRules"/> in the validator.
    /// </summary>
    public static SupplierChangeRequestLine BuildLine(SupplierChangeLineInput input, string actor, DateTime now)
    {
        if (!Enum.TryParse<ChangeTargetEntity>(input.TargetEntity, true, out var target))
            throw new ValidationException(Err("targetEntity must be Supplier, Address, Contact, Bank or License."));
        if (!Enum.TryParse<ChangeOperation>(input.Operation, true, out var op))
            throw new ValidationException(Err("operation must be Add, Edit or Delete."));

        return new SupplierChangeRequestLine
        {
            Id = Guid.NewGuid(),
            TargetEntity = target,
            Operation = op,
            TargetEntityId = input.TargetEntityId,
            FieldName = op == ChangeOperation.Edit ? input.FieldName?.Trim() : null,
            OldValue = null,
            NewValue = op == ChangeOperation.Edit ? Trunc(input.NewValue, 1000) : null,
            PayloadJson = op == ChangeOperation.Add ? input.PayloadJson : null,
            PushStatus = LinePushStatus.Pending,
            CreatedBy = actor,
            CreatedOn = now,
        };
    }

    private static string? Trunc(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
    private static IReadOnlyDictionary<string, string[]> Err(string m) => new Dictionary<string, string[]> { ["lines"] = new[] { m } };
}
