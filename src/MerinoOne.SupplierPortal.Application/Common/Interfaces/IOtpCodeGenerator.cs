namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Cryptographically-strong numeric OTP generator. The default of 6 digits matches
/// the invite + MFA flows; longer codes can be requested by callers that need them.
/// </summary>
public interface IOtpCodeGenerator
{
    /// <summary>Returns a zero-padded numeric code of <paramref name="digits"/> length.</summary>
    string Generate(int digits = 6);
}
