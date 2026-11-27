using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Quality.Core.Abstractions;
using Quality.Core.Entities;
using Quality.Core.Services;
using Quality.Data;

namespace Quality.Api.Infrastructure;

public sealed class JwtSettings
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 480;
    public string Key { get; set; } = "";
}

/// <summary>PBKDF2 hashing via ASP.NET Core's PasswordHasher (salted, versioned, rehash-capable).</summary>
public sealed class IdentityPasswordVerifier : IPasswordVerifier, IPasswordHashService
{
    private readonly PasswordHasher<User> _hasher = new();
    public bool Verify(User user, string password) =>
        _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    public string Hash(User user, string password) => _hasher.HashPassword(user, password);
}

public static class QualityClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub, EmployeeCode = "emp", Role = "role";
    /// <summary>User.TokenVersion at issue time (review Q2).</summary>
    public const string TokenVersion = "tv";
}

public sealed class JwtTokenIssuer : ITokenIssuer
{
    private readonly JwtSettings _s;
    private readonly TimeProvider _time;
    public JwtTokenIssuer(IOptions<JwtSettings> s, TimeProvider time) => (_s, _time) = (s.Value, time);

    public (string Token, DateTime ExpiresAtUtc) Issue(User user)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_s.ExpiryMinutes);
        var claims = new[]
        {
            new Claim(QualityClaims.Subject, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(QualityClaims.EmployeeCode, user.EmployeeCode),
            new Claim(QualityClaims.Role, user.Role!.RoleName),
            new Claim(QualityClaims.TokenVersion, user.TokenVersion.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer32),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.Key)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(_s.Issuer, _s.Audience, claims, now, expires, creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}

/// <summary>
/// Runs on every authenticated request after the signature and lifetime checks. The token is refused when the user is gone,
/// inactive, has another role, or the token's version is older than User.TokenVersion (password reset, deactivation or role
/// change since it was issued - review Q2). Malformed claims are refused (401) instead of causing a 500.
/// </summary>
public static class TokenRevocation
{
    public static async Task ValidateAsync(TokenValidatedContext ctx)
    {
        var p = ctx.Principal!;
        if (!int.TryParse(p.FindFirst(QualityClaims.Subject)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !int.TryParse(p.FindFirst(QualityClaims.TokenVersion)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tv))
        {
            ctx.Fail("Token is missing required claims.");   // includes tokens issued before db/08 (no "tv"): users sign in again once
            return;
        }
        // Anonymous endpoints (health, readiness, login) do not use the identity: a stale token sent along must not make them
        // depend on the master database (Fable review). The token is simply ignored there.
        if (ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            ctx.NoResult();
            return;
        }
        var db = ctx.HttpContext.RequestServices.GetRequiredService<MasterDbContext>();
        var u = await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.IsActive, x.TokenVersion, Role = x.Role!.RoleName }).FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);
        if (u is null || !u.IsActive || u.Role != p.FindFirst(QualityClaims.Role)?.Value || u.TokenVersion != tv)
            ctx.Fail("Token has been revoked.");
    }
}

public static class ClaimsExtensions
{
    /// <summary>Builds the server-side identity from validated token claims. Nothing here comes from request bodies.</summary>
    public static CurrentUser ToCurrentUser(this ClaimsPrincipal p) => new(
        int.Parse(p.FindFirstValue(QualityClaims.Subject) ?? throw new InvalidOperationException("sub claim missing"), CultureInfo.InvariantCulture),
        p.FindFirstValue(QualityClaims.EmployeeCode) ?? "",
        p.FindFirstValue(QualityClaims.Role) ?? "");
}
