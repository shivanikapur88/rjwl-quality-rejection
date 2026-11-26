using Quality.Core.Abstractions;
using Quality.Core.Dtos;

namespace Quality.Core.Services;

public sealed class AuthService
{
    private readonly IMasterData _master;
    private readonly IPasswordVerifier _verifier;
    private readonly ITokenIssuer _tokens;

    public AuthService(IMasterData master, IPasswordVerifier verifier, ITokenIssuer tokens)
        => (_master, _verifier, _tokens) = (master, verifier, tokens);

    public async Task<ServiceResult<LoginResponse>> LoginAsync(LoginRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            return ServiceResult<LoginResponse>.Fail(ErrorKind.Validation, "credentials_required", "Enter your username or employee ID and password.");

        var user = await _master.FindUserByLoginAsync(request.Username.Trim());
        // One generic message for unknown user, wrong password and inactive account: no account enumeration.
        if (user is null || !user.IsActive || user.Role is null || !_verifier.Verify(user, request.Password))
            return ServiceResult<LoginResponse>.Fail(ErrorKind.Unauthorized, "invalid_credentials", "Invalid username or password.");

        var (token, expires) = _tokens.Issue(user);
        return ServiceResult<LoginResponse>.Ok(new LoginResponse(token, expires, user.Id, user.EmployeeCode, user.DisplayName, user.Role.RoleName));
    }
}
