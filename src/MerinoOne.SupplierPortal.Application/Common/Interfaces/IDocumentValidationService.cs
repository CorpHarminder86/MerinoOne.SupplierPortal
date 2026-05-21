using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface IDocumentValidationService
{
    Task<DocumentValidationOutcome> ValidateAsync(Guid documentUploadId, CancellationToken ct = default);
}

public record DocumentValidationOutcome(
    AiValidationStatus Status,
    decimal? Confidence,
    string? Payload);
