using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Cryptographically-strong numeric OTP generator backed by
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>. Zero-pads the
/// random integer so the returned code is exactly <c>digits</c> characters long.
/// </summary>
internal sealed class OtpCodeGenerator : IOtpCodeGenerator
{
    public string Generate(int digits = 6)
    {
        var max = (int)Math.Pow(10, digits);
        var n = System.Security.Cryptography.RandomNumberGenerator.GetInt32(max);
        return n.ToString(new string('0', digits));
    }
}
