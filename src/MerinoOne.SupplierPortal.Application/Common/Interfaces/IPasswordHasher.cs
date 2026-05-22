namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
}
