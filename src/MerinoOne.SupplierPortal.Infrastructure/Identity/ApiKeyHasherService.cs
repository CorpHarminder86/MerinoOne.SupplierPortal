using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>
/// DI adapter over the static <see cref="ApiKeyHasher"/> so Application-layer handlers can hash/verify
/// inbound API keys without referencing Infrastructure.
/// </summary>
public class ApiKeyHasherService : IApiKeyHasher
{
    public string Hash(string plaintextKey) => ApiKeyHasher.Hash(plaintextKey);
    public bool Verify(string presentedKey, string storedHashHex) => ApiKeyHasher.Verify(presentedKey, storedHashHex);
}
