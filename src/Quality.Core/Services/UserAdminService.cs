using System.Text.Json;
using System.Text.RegularExpressions;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <summary>
/// Administrator-only user management. Users are never hard-deleted (their id is referenced by history); they are deactivated.
/// Passwords are hashed with the same hasher the login uses and are never stored, returned or written to the audit log.
/// </summary>
public sealed class UserAdminService
{
    public const int MinPasswordLength = 10, MaxPasswordLength = 128;
    private const string Entity = "User";
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9._-]{2,50}$");
    private readonly IMasterData _master; private readonly IPasswordHashService _hasher; private readonly TimeProvider _time;

    public UserAdminService(IMasterData master, IPasswordHashService hasher, TimeProvider time) => (_master, _hasher, _time) = (master, hasher, time);

    private static ServiceResult<T>? RequireAdmin<T>(CurrentUser u) =>
        u.IsAdministrator ? null : ServiceResult<T>.Fail(ErrorKind.Forbidden, "forbidden", "Administrator access is required.");

    private static string? PasswordProblem(string? pw, params string[] identifiers)
    {
        if (string.IsNullOrEmpty(pw) || pw.Length < MinPasswordLength) return $"The password must be at least {MinPasswordLength} characters.";
        if (pw.Length > MaxPasswordLength) return $"The password may not exceed {MaxPasswordLength} characters.";
        if (identifiers.Any(i => i.Length > 0 && string.Equals(pw, i, StringComparison.OrdinalIgnoreCase))) return "The password must not be the same as the username or employee code.";
        return null;
    }

    private static string Snapshot(User u, IEnumerable<int> shopIds) =>
        JsonSerializer.Serialize(new { u.EmployeeCode, u.Username, u.DisplayName, Role = u.Role?.RoleName, ShopIds = shopIds.OrderBy(x => x), u.IsActive });

    private async Task<(List<int>? Ids, ServiceError? Error)> ResolveShopsAsync(string role, IReadOnlyList<int>? requested)
    {
        if (role == RoleNames.Administrator) return (new List<int>(), null);   // administrators implicitly see every shop
        var ids = (requested ?? Array.Empty<int>()).Distinct().ToList();
        if (ids.Count == 0) return (null, new ServiceError(ErrorKind.Validation, "shops_required", "Assign at least one shop to this user."));
        foreach (var id in ids)
        {
            var s = await _master.GetShopAsync(id);
            if (s is null || !s.IsActive) return (null, new ServiceError(ErrorKind.Validation, "shop_not_found", "One of the selected shops does not exist."));
        }
        return (ids, null);
    }

    private static AdminUserDto ToDto(User u, IReadOnlyList<Shop> allShops, IReadOnlyList<int> assigned)
    {
        var isAdmin = u.Role?.RoleName == RoleNames.Administrator;
        var codes = isAdmin ? allShops.Select(s => s.ShopCode).ToList() : allShops.Where(s => assigned.Contains(s.Id)).Select(s => s.ShopCode).ToList();
        return new AdminUserDto(u.Id, u.EmployeeCode, u.Username, u.DisplayName, u.Role?.RoleName ?? "", isAdmin ? Array.Empty<int>() : assigned.ToList(), codes, u.IsActive, DateTime.SpecifyKind(u.CreatedAt, DateTimeKind.Utc));
    }

    private async Task<AdminUserDto> DtoAsync(User u)
    {
        var shops = await _master.GetAllShopsAsync();
        var assigned = (await _master.GetAllUserShopsAsync()).Where(x => x.UserId == u.Id).Select(x => x.ShopId).ToList();
        return ToDto(u, shops, assigned);
    }

    public async Task<ServiceResult<IReadOnlyList<AdminUserDto>>> ListAsync(CurrentUser actor, string? search, bool? isActive)
    {
        var deny = RequireAdmin<IReadOnlyList<AdminUserDto>>(actor); if (deny is not null) return deny;
        var shops = await _master.GetAllShopsAsync();
        var map = (await _master.GetAllUserShopsAsync()).GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.ShopId).ToList());
        var users = await _master.GetUsersAsync(search, isActive);
        return ServiceResult<IReadOnlyList<AdminUserDto>>.Ok(users.Select(u => ToDto(u, shops, map.TryGetValue(u.Id, out var l) ? l : new List<int>())).ToList());
    }

    public async Task<ServiceResult<AdminUserDto>> CreateAsync(CurrentUser actor, CreateUserRequest req)
    {
        var deny = RequireAdmin<AdminUserDto>(actor); if (deny is not null) return deny;
        static ServiceResult<AdminUserDto> Bad(string code, string msg) => ServiceResult<AdminUserDto>.Fail(ErrorKind.Validation, code, msg);
        if (req is null) return Bad("request_required", "Request body required.");

        var emp = (req.EmployeeCode ?? "").Trim(); var user = (req.Username ?? "").Trim(); var display = (req.DisplayName ?? "").Trim();
        if (!IdPattern.IsMatch(emp)) return Bad("employee_code_invalid", "Employee code: 2-50 characters, letters, digits, '.', '_' or '-'.");
        if (!IdPattern.IsMatch(user)) return Bad("username_invalid", "Username: 2-50 characters, letters, digits, '.', '_' or '-'.");
        if (display.Length == 0 || display.Length > 100) return Bad("display_name_invalid", "Enter a name (up to 100 characters).");
        var roleName = RoleNames.All.FirstOrDefault(r => r == req.Role);
        if (roleName is null) return Bad("role_invalid", "Select a valid role.");
        var pwProblem = PasswordProblem(req.Password, emp, user);
        if (pwProblem is not null) return Bad("password_weak", pwProblem);

        var (shopIds, shopErr) = await ResolveShopsAsync(roleName, req.ShopIds);
        if (shopErr is not null) return ServiceResult<AdminUserDto>.Fail(shopErr);

        // Login accepts username OR employee code, so both values must be unused as either.
        if (await _master.LoginIdentifierTakenAsync(emp, null)) return ServiceResult<AdminUserDto>.Fail(ErrorKind.Conflict, "duplicate_user", $"'{emp}' is already used as an employee code or username.");
        if (!string.Equals(emp, user, StringComparison.OrdinalIgnoreCase) && await _master.LoginIdentifierTakenAsync(user, null))
            return ServiceResult<AdminUserDto>.Fail(ErrorKind.Conflict, "duplicate_user", $"'{user}' is already used as an employee code or username.");

        var role = await _master.GetRoleAsync(roleName) ?? throw new InvalidOperationException("Role missing in database: " + roleName);
        var u = new User { EmployeeCode = emp, Username = user, DisplayName = display, RoleId = role.Id, Role = role, IsActive = true, CreatedAt = _time.GetUtcNow().UtcDateTime };
        u.PasswordHash = _hasher.Hash(u, req.Password);
        await _master.InTransactionAsync(async () =>
        {
            _master.AddUser(u);
            await _master.SaveChangesAsync();                                   // assigns Id
            await _master.ReplaceUserShopsAsync(u.Id, shopIds!);
            _master.AddAudit(new AuditLogEntry { UserId = actor.Id, Action = AuditActions.UserCreated, EntityType = Entity, EntityId = u.Id.ToString(), NewValue = Snapshot(u, shopIds!), Timestamp = u.CreatedAt });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<AdminUserDto>.Ok(await DtoAsync(u));
    }

    public async Task<ServiceResult<AdminUserDto>> UpdateAsync(CurrentUser actor, int id, UpdateUserRequest req)
    {
        var deny = RequireAdmin<AdminUserDto>(actor); if (deny is not null) return deny;
        static ServiceResult<AdminUserDto> Bad(string code, string msg) => ServiceResult<AdminUserDto>.Fail(ErrorKind.Validation, code, msg);
        if (req is null) return Bad("request_required", "Request body required.");
        var u = await _master.GetUserAsync(id);
        if (u is null) return ServiceResult<AdminUserDto>.Fail(ErrorKind.NotFound, "user_not_found", "User not found.");

        var display = (req.DisplayName ?? "").Trim();
        if (display.Length == 0 || display.Length > 100) return Bad("display_name_invalid", "Enter a name (up to 100 characters).");
        var roleName = RoleNames.All.FirstOrDefault(r => r == req.Role);
        if (roleName is null) return Bad("role_invalid", "Select a valid role.");

        var currentRole = u.Role?.RoleName ?? "";
        if (roleName != currentRole)
        {
            if (u.Id == actor.Id) return ServiceResult<AdminUserDto>.Fail(ErrorKind.Conflict, "cannot_change_own_role", "You cannot change your own role.");
            if (currentRole == RoleNames.Administrator && u.IsActive && await _master.CountOtherActiveAdministratorsAsync(u.Id) == 0)
                return ServiceResult<AdminUserDto>.Fail(ErrorKind.Conflict, "last_admin", "This is the last active administrator and cannot be changed to another role.");
        }
        var (shopIds, shopErr) = await ResolveShopsAsync(roleName, req.ShopIds);
        if (shopErr is not null) return ServiceResult<AdminUserDto>.Fail(shopErr);

        var oldShops = (await _master.GetAllUserShopsAsync()).Where(x => x.UserId == u.Id).Select(x => x.ShopId).ToList();
        var old = Snapshot(u, oldShops);
        var role = await _master.GetRoleAsync(roleName) ?? throw new InvalidOperationException("Role missing in database: " + roleName);
        u.DisplayName = display; u.RoleId = role.Id; u.Role = role;
        await _master.InTransactionAsync(async () =>
        {
            await _master.ReplaceUserShopsAsync(u.Id, shopIds!);
            _master.AddAudit(new AuditLogEntry { UserId = actor.Id, Action = AuditActions.UserEdited, EntityType = Entity, EntityId = u.Id.ToString(), OldValue = old, NewValue = Snapshot(u, shopIds!), Timestamp = _time.GetUtcNow().UtcDateTime });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<AdminUserDto>.Ok(await DtoAsync(u));
    }

    public async Task<ServiceResult<AdminUserDto>> SetStatusAsync(CurrentUser actor, int id, bool isActive)
    {
        var deny = RequireAdmin<AdminUserDto>(actor); if (deny is not null) return deny;
        var u = await _master.GetUserAsync(id);
        if (u is null) return ServiceResult<AdminUserDto>.Fail(ErrorKind.NotFound, "user_not_found", "User not found.");
        if (u.IsActive == isActive) return ServiceResult<AdminUserDto>.Ok(await DtoAsync(u));
        if (!isActive)
        {
            if (u.Id == actor.Id) return ServiceResult<AdminUserDto>.Fail(ErrorKind.Conflict, "cannot_deactivate_self", "You cannot deactivate your own account.");
            if (u.Role?.RoleName == RoleNames.Administrator && await _master.CountOtherActiveAdministratorsAsync(u.Id) == 0)
                return ServiceResult<AdminUserDto>.Fail(ErrorKind.Conflict, "last_admin", "This is the last active administrator and cannot be deactivated.");
        }
        var shops = (await _master.GetAllUserShopsAsync()).Where(x => x.UserId == u.Id).Select(x => x.ShopId).ToList();
        var old = Snapshot(u, shops); u.IsActive = isActive;
        await _master.InTransactionAsync(async () =>
        {
            _master.AddAudit(new AuditLogEntry { UserId = actor.Id, Action = isActive ? AuditActions.UserActivated : AuditActions.UserDeactivated, EntityType = Entity, EntityId = u.Id.ToString(), OldValue = old, NewValue = Snapshot(u, shops), Timestamp = _time.GetUtcNow().UtcDateTime });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<AdminUserDto>.Ok(await DtoAsync(u));
    }

    public async Task<ServiceResult<bool>> ResetPasswordAsync(CurrentUser actor, int id, ResetPasswordRequest req)
    {
        var deny = RequireAdmin<bool>(actor); if (deny is not null) return deny;
        var u = await _master.GetUserAsync(id);
        if (u is null) return ServiceResult<bool>.Fail(ErrorKind.NotFound, "user_not_found", "User not found.");
        var problem = PasswordProblem(req?.NewPassword, u.EmployeeCode, u.Username);
        if (problem is not null) return ServiceResult<bool>.Fail(ErrorKind.Validation, "password_weak", problem);

        u.PasswordHash = _hasher.Hash(u, req!.NewPassword);
        await _master.InTransactionAsync(async () =>
        {
            // The audit row records THAT the password was reset, never the password or the hash.
            _master.AddAudit(new AuditLogEntry { UserId = actor.Id, Action = AuditActions.UserPasswordReset, EntityType = Entity, EntityId = u.Id.ToString(), Timestamp = _time.GetUtcNow().UtcDateTime });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<bool>.Ok(true);
    }
}
