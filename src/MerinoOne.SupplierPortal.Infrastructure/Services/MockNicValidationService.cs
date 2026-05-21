using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

public class MockNicValidationService : INicValidationService
{
    public async Task<NicValidationOutcome> VerifyAsync(VerificationType type, string number, CancellationToken ct = default)
    {
        await Task.Delay(150, ct);
        // Deterministic results — keyed on the last char of the number for predictable seed-data outcomes
        if (string.IsNullOrEmpty(number))
            return new NicValidationOutcome(VerificationResult.Error, "Mock", "{\"error\":\"empty\"}", "No number supplied.");

        var lastChar = number[^1];
        var result = (lastChar % 10) switch
        {
            0 or 9 => VerificationResult.Error,
            7 or 3 => VerificationResult.Fail,
            _ => VerificationResult.Pass
        };

        var payload = JsonSerializer.Serialize(new
        {
            type = type.ToString(),
            number,
            result = result.ToString(),
            provider = "Mock",
            timestamp = DateTime.UtcNow
        });

        var notes = result switch
        {
            VerificationResult.Pass => $"{type} verified successfully (mock).",
            VerificationResult.Fail => $"{type} number not found in registry (mock).",
            _ => "Mock provider returned an error."
        };

        return new NicValidationOutcome(result, "Mock", payload, notes);
    }
}
