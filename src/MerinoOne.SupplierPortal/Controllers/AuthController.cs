using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Auth;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    public AuthController(AppDbContext db, IConfiguration cfg) { _db = db; _cfg = cfg; }

    [HttpPost("login")]
    public async Task<ActionResult<Result<LoginResponse>>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.UserCode == req.UserCode && u.IsActive, ct);
        if (user == null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            return Unauthorized(Result<LoginResponse>.Fail("Invalid credentials."));

        var roles = await (from ur in _db.UserRoles.IgnoreQueryFilters()
                           join r in _db.Roles.IgnoreQueryFilters() on ur.RoleId equals r.Id
                           where ur.AppUserId == user.Id
                           select r.Name).ToListAsync(ct);

        var perms = await (from ur in _db.UserRoles.IgnoreQueryFilters()
                           join rp in _db.RolePermissions.IgnoreQueryFilters() on ur.RoleId equals rp.RoleId
                           join p in _db.Permissions.IgnoreQueryFilters() on rp.PermissionId equals p.Id
                           where ur.AppUserId == user.Id
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
        return Ok(Result<LoginResponse>.Ok(new LoginResponse(jwtStr, expiry, user.UserCode, user.FullName, roles.ToArray(), perms.ToArray()), HttpContext.TraceIdentifier));
    }
}
