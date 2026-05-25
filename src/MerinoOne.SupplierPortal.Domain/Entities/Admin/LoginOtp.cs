using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// OTP issued during the MFA login step. After the first password leg, the server returns
/// MfaToken to the client; the client posts MfaToken + OTP back to the verify endpoint
/// so the password does not need to be re-presented. Single-use: ConsumedAt is set on verify.
/// </summary>
public class LoginOtp : AuditableEntity
{
    public Guid AppUserId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string MfaToken { get; set; } = string.Empty;

    public AppUser User { get; set; } = null!;
}
