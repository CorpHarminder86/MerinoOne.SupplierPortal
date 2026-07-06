using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Application.Integration.Ln;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration;

/// <summary>
/// R9 (TSD R9 §2.11, D-R9-16) — the R8 <see cref="IEligibilityGate"/> re-implemented on the shared LN
/// JSONata engine: ONE gate language across IDM and LN. The expression must evaluate to the strict JSON
/// <c>true</c> against the serialized snapshot; false / undefined / non-boolean / parse-or-eval error /
/// blank expression all fail CLOSED — exactly the posture of the JsonPath gate it replaces (a malformed
/// gate never dispatches). DI-swapped for <c>JsonPathEligibilityGate</c> in the B8 retrofit slice, after
/// migration 0049 converts the stored dot-path arrays to JSONata conjunctions.
/// </summary>
public sealed class JsonataEligibilityGate : IEligibilityGate
{
    private readonly ILnMappingService _mapping;
    public JsonataEligibilityGate(ILnMappingService mapping) => _mapping = mapping;

    public bool IsSatisfied(string eligibilityGateJson, object snapshot)
    {
        if (string.IsNullOrWhiteSpace(eligibilityGateJson)) return false;
        try
        {
            var inputJson = JsonSerializer.Serialize(snapshot);
            var result = _mapping.Evaluate(eligibilityGateJson, inputJson);
            return result.Ok && result.OutputJson == "true";
        }
        catch
        {
            return false; // fail closed — identical to the malformed-gate behaviour of JsonPathEligibilityGate
        }
    }
}
