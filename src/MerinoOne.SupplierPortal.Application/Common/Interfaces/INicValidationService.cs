using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface INicValidationService
{
    Task<NicValidationOutcome> VerifyAsync(VerificationType type, string number, CancellationToken ct = default);
}

public record NicValidationOutcome(
    VerificationResult Result,
    string Provider,
    string RawPayload,
    string? Notes);
