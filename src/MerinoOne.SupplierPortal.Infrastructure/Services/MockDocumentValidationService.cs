using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

public class MockDocumentValidationService : IDocumentValidationService
{
    public async Task<DocumentValidationOutcome> ValidateAsync(Guid documentUploadId, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);
        var payload = JsonSerializer.Serialize(new
        {
            documentId = documentUploadId,
            extractedFields = new { name = "Acme Corp", gst = "29ABCDE1234F1Z5" },
            issues = Array.Empty<string>(),
            confidence = 92.5m
        });
        return new DocumentValidationOutcome(AiValidationStatus.Valid, 92.5m, payload);
    }
}
