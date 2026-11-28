using Quality.Core.Abstractions;
using Quality.Core.Dtos;

namespace Quality.Core.Services;

public sealed class AuthService
{
    private readonly IMasterData _master;
    private readonly IPasswordVerifier _verifier;
    // A real hash (same algorithm and cost as user passwords) of a random, discarded password; built once.
    private static Entities.User? _dummy;
    private Entities.User Dummy => _dummy ??= MakeDummy();
    private readonly IPasswordHashService _hasher;
    private Entities.User MakeDummy()
    {
        var u = new Entities.User { Username = "dummy" };
        u.PasswordHash = _hasher.Hash(u, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
        return u;
    }
    private readonly ITokenIssuer _tokens;

    public AuthService(IMasterData master, IPasswordVerifier verifier, ITokenIssuer tokens, IPasswordHashService hasher)
        => (_master, _verifier, _tokens, _hasher) = (master, verifier, tokens, hasher);

    public async Task<ServiceResult<LoginResponse>> LoginAsync(LoginRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            return ServiceResult<LoginResponse>.Fail(ErrorKind.Validation, "credentials_required", "Enter your username or employee ID and password.");

        // No identifier is longer than 50 characters and no password longer than 128 (the admin rules): refuse without a
        // database lookup or hashing a huge string. Same generic answer as a wrong password (counted by the throttle).
        if (request.Username.Trim().Length > LoginRules.MaxUsernameLength || request.Password.Length > UserAdminService.MaxPasswordLength)
            return ServiceResult<LoginResponse>.Fail(ErrorKind.Unauthorized, "invalid_credentials", "Invalid username or password.");

        var user = await _master.FindUserByLoginAsync(request.Username.Trim());
        // One generic message for unknown user, wrong password and inactive account: no account enumeration.
        if (user is null || !user.IsActive || user.Role is null)
        {
            _verifier.Verify(Dummy, request.Password);   // same hashing cost as a real account: no account enumeration by timing (full review #8)
            return ServiceResult<LoginResponse>.Fail(ErrorKind.Unauthorized, "invalid_credentials", "Invalid username or password.");
        }
        if (!_verifier.Verify(user, request.Password))
            return ServiceResult<LoginResponse>.Fail(ErrorKind.Unauthorized, "invalid_credentials", "Invalid username or password.");

        var (token, expires) = _tokens.Issue(user);
        return ServiceResult<LoginResponse>.Ok(new LoginResponse(token, expires, user.Id, user.EmployeeCode, user.DisplayName, user.Role.RoleName));
    }
}
