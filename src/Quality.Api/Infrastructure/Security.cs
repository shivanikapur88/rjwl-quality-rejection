using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Quality.Core.Abstractions;
using Quality.Core.Entities;
using Quality.Core.Services;

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
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("emp", user.EmployeeCode),
            new Claim("role", user.Role!.RoleName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.Key)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(_s.Issuer, _s.Audience, claims, now, expires, creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}

public static class ClaimsExtensions
{
    /// <summary>Builds the server-side identity from validated token claims. Nothing here comes from request bodies.</summary>
    public static CurrentUser ToCurrentUser(this ClaimsPrincipal p) => new(
        int.Parse(p.FindFirstValue("sub") ?? throw new InvalidOperationException("sub claim missing")),
        p.FindFirstValue("emp") ?? "",
        p.FindFirstValue("role") ?? "");
}
