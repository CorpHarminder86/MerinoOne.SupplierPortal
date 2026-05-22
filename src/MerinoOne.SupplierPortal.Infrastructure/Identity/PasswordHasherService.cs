using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

public class PasswordHasherService : IPasswordHasher
{
    public string Hash(string password) => PasswordHasher.Hash(password);
    public bool Verify(string password, string stored) => PasswordHasher.Verify(password, stored);
}
