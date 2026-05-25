namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
    /// <summary>
    /// Stable, salt-less-equivalent hash. Same input always yields the same output.
    /// Used by OTP storage so equality compare suffices for verification, and by
    /// seeders so re-runs produce identical password hashes.
    /// </summary>
    string DeterministicHash(string input);
}
