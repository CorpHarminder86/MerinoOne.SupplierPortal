using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Auth;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const int MfaOtpValidMinutes = 5;
    private const int MfaMaxAttempts = 5;

    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IOtpCodeGenerator _otp;
    private readonly IEmailService _email;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        IConfiguration cfg,
        IOtpCodeGenerator otp,
        IEmailService email,
        IPasswordHasher hasher,
        ILogger<AuthController> logger)
    {
        _db = db;
        _cfg = cfg;
        _otp = otp;
        _email = email;
        _hasher = hasher;
        _logger = logger;
    }

    [HttpPost("login")]
    public async Task<ActionResult<Result<LoginResponse>>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var identifier = (req.Email ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(identifier))
            return Unauthorized(Result<LoginResponse>.Fail("Invalid credentials."));

        // Backwards-compat: the same field accepts email (contains '@') or a bare userCode.
        Domain.Entities.Admin.AppUser? user;
        if (identifier.Contains('@'))
        {
            var lowered = identifier.ToLowerInvariant();
            user = await _db.AppUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email.ToLower() == lowered && u.IsActive, ct);
        }
        else
        {
            user = await _db.AppUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.UserCode == identifier && u.IsActive, ct);
        }

        if (user == null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            return Unauthorized(Result<LoginResponse>.Fail("Invalid credentials."));

        // MFA gate — issue an OTP, return MfaToken, and DEFER JWT issuance to /mfa/verify.
        if (user.IsMfaEnabled)
        {
            var now = DateTime.UtcNow;
            var code = _otp.Generate();
            var mfaToken = Guid.NewGuid().ToString("N");

            _db.LoginOtps.Add(new LoginOtp
            {
                Id = Guid.NewGuid(),
                AppUserId = user.Id,
                CodeHash = _hasher.DeterministicHash(code),
                IssuedAt = now,
                ExpiresAt = now.AddMinutes(MfaOtpValidMinutes),
                Attempts = 0,
                MfaToken = mfaToken,
                ConsumedAt = null,
                CreatedBy = "login-mfa",
                CreatedOn = now,
            });
            await _db.SaveChangesAsync(ct);

            try
            {
                await _email.SendLoginOtpAsync(user.Email, user.FullName, code, MfaOtpValidMinutes, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MFA OTP email send failed for {Email} (user {UserId}). MfaToken issued; user can request retry.",
                    user.Email, user.Id);
            }

            return Ok(Result<LoginResponse>.Ok(
                new LoginResponse(
                    Token: string.Empty,
                    ExpiresAt: default,
                    UserCode: user.UserCode,
                    FullName: user.FullName,
                    Roles: Array.Empty<string>(),
                    Permissions: Array.Empty<string>(),
                    MustChangePassword: false,
                    RequiresMfa: true,
                    MfaToken: mfaToken),
                HttpContext.TraceIdentifier));
        }

        // Non-MFA path — issue JWT immediately.
        var (jwtStr, expiry, roles, perms) = await BuildSessionAsync(user, ct);
        return Ok(Result<LoginResponse>.Ok(
            new LoginResponse(
                jwtStr,
                expiry,
                user.UserCode,
                user.FullName,
                roles,
                perms,
                user.MustChangePassword,
                RequiresMfa: false,
                MfaToken: null),
            HttpContext.TraceIdentifier));
    }

    [HttpPost("mfa/verify")]
    public async Task<ActionResult<Result<LoginResponse>>> MfaVerify([FromBody] MfaVerifyRequest req, CancellationToken ct)
    {
        var token = (req?.MfaToken ?? string.Empty).Trim();
        var code = (req?.Code ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(code))
            return Unauthorized(Result<LoginResponse>.Fail("Invalid verification request."));

        var now = DateTime.UtcNow;

        var otp = await _db.LoginOtps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.MfaToken == token, ct);
        if (otp == null)
            return Unauthorized(Result<LoginResponse>.Fail("Verification token not recognised."));

        if (otp.ConsumedAt.HasValue)
            return Unauthorized(Result<LoginResponse>.Fail("This verification token has already been used."));
        if (otp.ExpiresAt < now)
            return Unauthorized(Result<LoginResponse>.Fail("Verification code has expired. Please sign in again."));

        var expected = _hasher.DeterministicHash(code);
        if (!string.Equals(expected, otp.CodeHash, StringComparison.Ordinal))
        {
            otp.Attempts++;
            otp.UpdatedBy = "mfa-verify";
            otp.UpdatedOn = now;
            if (otp.Attempts >= MfaMaxAttempts)
            {
                otp.ConsumedAt = now; // lock out — user must sign in again
            }
            await _db.SaveChangesAsync(ct);

            var remaining = Math.Max(0, MfaMaxAttempts - otp.Attempts);
            return Unauthorized(Result<LoginResponse>.Fail(
                remaining > 0
                    ? $"Invalid verification code. {remaining} attempt(s) remaining."
                    : "Verification code locked out. Please sign in again."));
        }

        // Success — consume the OTP and mint the real JWT.
        otp.ConsumedAt = now;
        otp.UpdatedBy = "mfa-verify";
        otp.UpdatedOn = now;

        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == otp.AppUserId && u.IsActive, ct);
        if (user == null)
        {
            await _db.SaveChangesAsync(ct);
            return Unauthorized(Result<LoginResponse>.Fail("User no longer active."));
        }

        var (jwtStr, expiry, roles, perms) = await BuildSessionAsync(user, ct);

        return Ok(Result<LoginResponse>.Ok(
            new LoginResponse(
                jwtStr,
                expiry,
                user.UserCode,
                user.FullName,
                roles,
                perms,
                user.MustChangePassword,
                RequiresMfa: false,
                MfaToken: null),
            HttpContext.TraceIdentifier));
    }

    /// <summary>
    /// Shared session bootstrap: enumerate roles + permissions, mint JWT, stamp
    /// <c>LastLoginAt</c>, and SaveChanges. Called from both the non-MFA login path
    /// and the post-OTP verify path so the two stay in lock-step.
    /// </summary>
    private async Task<(string jwt, DateTime expiry, string[] roles, string[] perms)> BuildSessionAsync(
        Domain.Entities.Admin.AppUser user, CancellationToken ct)
    {
        // IgnoreQueryFilters bypasses BOTH seccode AND soft-delete; re-apply !IsDeleted manually
        // or soft-deleted role assignments will leak back into the JWT (e.g. user keeps Admin
        // claims after admin removes the Admin role).
        var roles = await (from ur in _db.UserRoles.IgnoreQueryFilters()
                           join r in _db.Roles.IgnoreQueryFilters() on ur.RoleId equals r.Id
                           where ur.AppUserId == user.Id && !ur.IsDeleted && !r.IsDeleted
                           select r.Name).ToListAsync(ct);

        var perms = await (from ur in _db.UserRoles.IgnoreQueryFilters()
                           join rp in _db.RolePermissions.IgnoreQueryFilters() on ur.RoleId equals rp.RoleId
                           join p in _db.Permissions.IgnoreQueryFilters() on rp.PermissionId equals p.Id
                           where ur.AppUserId == user.Id && !ur.IsDeleted && !rp.IsDeleted && !p.IsDeleted
                           select p.Code).Distinct().ToListAsync(ct);

        var jwt = _cfg.GetSection("Jwt");
        var keyBytes = Encoding.UTF8.GetBytes(jwt["SigningKey"]!);
        var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("userCode", user.UserCode),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(perms.Select(p => new Claim("permission", p)));

        var expiry = DateTime.UtcNow.AddMinutes(int.Parse(jwt["ExpiryMinutes"] ?? "60"));
        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: expiry,
            signingCredentials: creds);

        var jwtStr = new JwtSecurityTokenHandler().WriteToken(token);

        // Stamp lastLoginAt AFTER signing the JWT so a serialization failure won't pollute the audit.
        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedBy = "login";
        user.UpdatedOn = user.LastLoginAt.Value;
        await _db.SaveChangesAsync(ct);

        return (jwtStr, expiry, roles.ToArray(), perms.ToArray());
    }

    /// <summary>
    /// Diagnostic: returns the principal as the seccode filter sees it + every G-seccode the user can read.
    /// Use to verify whether sup-* users have over-broad SecRights or wrong roles.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize]
    [HttpGet("whoami")]
    public async Task<ActionResult<object>> WhoAmI(CancellationToken ct)
    {
        var userCode = User.FindFirst("userCode")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? string.Empty;
        var roles = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToArray();
        var perms = User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray();

        var secRights = await (from sr in _db.SecRights.IgnoreQueryFilters()
                               join sc in _db.Seccodes.IgnoreQueryFilters() on sr.SeccodeId equals sc.Id
                               where sr.UserCode == userCode
                               select new
                               {
                                   secCodeId = sc.Id,
                                   secCodeType = sc.SeccodeType.ToString(),
                                   secCodeName = sc.Name,
                                   supplierId = sc.SupplierId,
                                   canRead = sr.CanRead,
                                   canWrite = sr.CanWrite
                               }).ToListAsync(ct);

        var mappedSuppliers = await (from m in _db.SupplierUserMaps.IgnoreQueryFilters()
                                     join u in _db.AppUsers.IgnoreQueryFilters() on m.AppUserId equals u.Id
                                     join s in _db.Suppliers.IgnoreQueryFilters() on m.SupplierId equals s.Id
                                     where u.UserCode == userCode
                                     select new { s.SupplierCode, s.LegalName }).ToListAsync(ct);

        var isAdmin = roles.Contains("SuperAdmin") || roles.Contains("Admin");
        var isManager = roles.Contains("Buyer") || roles.Contains("Finance");
        var willSeeAll = isAdmin || isManager || string.IsNullOrEmpty(userCode);

        return Ok(new
        {
            userCode,
            roles,
            permissions = perms,
            isAdmin,
            isManager,
            willSeeAllData = willSeeAll,
            willSeeAllReason = isAdmin ? "isAdmin (SuperAdmin/Admin)"
                              : isManager ? "isManager (Buyer/Finance)"
                              : string.IsNullOrEmpty(userCode) ? "empty userCode → fail-open"
                              : "no — filtered by SecRights",
            secRights,
            mappedSuppliers
        });
    }

    /// <summary>
    /// Rehydrate the full user session from the current JWT. Used by the Blazor client after
    /// page reload — ProtectedLocalStorage only persists the raw token, so we re-emit the same
    /// shape as <see cref="Login"/> (roles, permissions, full name, expiry, mustChangePassword)
    /// without minting a new token.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<Result<LoginResponse>>> Me(CancellationToken ct)
    {
        var userCode = User.FindFirst("userCode")?.Value
                       ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? string.Empty;

        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.UserCode == userCode && u.IsActive, ct);

        if (user == null)
            return Unauthorized(Result<LoginResponse>.Fail("Session no longer valid."));

        // IgnoreQueryFilters bypasses BOTH seccode AND soft-delete; re-apply !IsDeleted manually
        // or soft-deleted role assignments will leak back into the JWT (e.g. user keeps Admin
        // claims after admin removes the Admin role).
        var roles = await (from ur in _db.UserRoles.IgnoreQueryFilters()
                           join r in _db.Roles.IgnoreQueryFilters() on ur.RoleId equals r.Id
                           where ur.AppUserId == user.Id && !ur.IsDeleted && !r.IsDeleted
                           select r.Name).ToListAsync(ct);

        var perms = await (from ur in _db.UserRoles.IgnoreQueryFilters()
                           join rp in _db.RolePermissions.IgnoreQueryFilters() on ur.RoleId equals rp.RoleId
                           join p in _db.Permissions.IgnoreQueryFilters() on rp.PermissionId equals p.Id
                           where ur.AppUserId == user.Id && !ur.IsDeleted && !rp.IsDeleted && !p.IsDeleted
                           select p.Code).Distinct().ToListAsync(ct);

        // Echo the incoming token back so the client can re-store/refresh it. If extraction fails
        // for any reason the client still holds the original — return empty string and let it cope.
        var authHeader = HttpContext.Request.Headers.Authorization.ToString();
        var token = !string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring("Bearer ".Length).Trim()
            : string.Empty;

        var expClaim = User.FindFirst("exp")?.Value;
        var expiresAt = long.TryParse(expClaim, out var expUnix)
            ? DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime
            : DateTime.UtcNow.AddMinutes(60);

        return Ok(Result<LoginResponse>.Ok(
            new LoginResponse(
                token,
                expiresAt,
                user.UserCode,
                user.FullName,
                roles.ToArray(),
                perms.ToArray(),
                user.MustChangePassword),
            HttpContext.TraceIdentifier));
    }
}
