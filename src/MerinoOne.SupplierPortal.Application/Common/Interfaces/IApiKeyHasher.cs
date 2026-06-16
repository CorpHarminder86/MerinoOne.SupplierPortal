namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// One-way hashing for inbound X-APIKey credentials. Implemented in Infrastructure over the static
/// ApiKeyHasher (Application has no Infrastructure reference). API keys are high-entropy random secrets,
/// so a plain SHA-256 hex digest suffices (no per-key salt / slow KDF).
/// </summary>
public interface IApiKeyHasher
{
    /// <summary>SHA-256 hex digest (lowercase, 64 chars) of the full plaintext key.</summary>
    string Hash(string plaintextKey);

    /// <summary>Constant-time comparison of a presented key against a stored hex digest.</summary>
    bool Verify(string presentedKey, string storedHashHex);
}
