using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

public enum ErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict }

public sealed record ServiceError(ErrorKind Kind, string Code, string Message);

public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error) { Value = value; Error = error; }
    public T? Value { get; }
    public ServiceError? Error { get; }
    public bool IsSuccess => Error is null;
    public static ServiceResult<T> Ok(T value) => new(value, null);
    public static ServiceResult<T> Fail(ErrorKind kind, string code, string message) => new(default, new ServiceError(kind, code, message));
    public static ServiceResult<T> Fail(ServiceError error) => new(default, error);
}

/// <summary>Identity taken from the validated token on the server; never from request bodies.</summary>
public sealed record CurrentUser(int Id, string EmployeeCode, string Role)
{
    public bool IsAdministrator => Role == RoleNames.Administrator;
    public bool IsInspector => Role == RoleNames.QualityInspector;
    public bool IsSupervisor => Role == RoleNames.Supervisor;
}

/// <summary>Shop-level authorization shared by all services. Administrators see every active shop; others only assigned shops.</summary>
public sealed class ShopAccessGuard
{
    private readonly IMasterData _master;
    public ShopAccessGuard(IMasterData master) => _master = master;

    public async Task<(Shop? Shop, ServiceError? Error)> AuthorizeAsync(CurrentUser user, int shopId)
    {
        var shop = await _master.GetShopAsync(shopId);
        if (shop is null || !shop.IsActive)
            return (null, new ServiceError(ErrorKind.NotFound, "shop_not_found", "Shop not found."));
        if (!user.IsAdministrator)
        {
            var ids = await _master.GetUserShopIdsAsync(user.Id);
            if (!ids.Contains(shopId))
                return (null, new ServiceError(ErrorKind.Forbidden, "shop_not_assigned", "You are not assigned to this shop."));
        }
        return (shop, null);
    }

    public async Task<IReadOnlyList<Shop>> AccessibleShopsAsync(CurrentUser user)
    {
        var all = (await _master.GetAllShopsAsync()).Where(s => s.IsActive).ToList();
        if (user.IsAdministrator) return all;
        var ids = await _master.GetUserShopIdsAsync(user.Id);
        return all.Where(s => ids.Contains(s.Id)).ToList();
    }
}

internal static class Mapping
{
    public static EntryDto ToDto(QualityRejectionEntry e) => new(
        e.Id, e.SubmissionId, e.ShopId, e.ShopCode, e.InspectionStageId, e.StageCode, e.StageName,
        e.MachineId, e.MachineCode, e.MachineName, e.RejectionPhenomenonId, e.PhenomenonNameSnapshot, e.Quantity,
        e.InspectorId, e.InspectorEmployeeCode, e.Remarks, DateTime.SpecifyKind(e.ServerCreatedAt, DateTimeKind.Utc));
}

/// <summary>Row versions travel to the client as base64 strings ("Version") and come back on edits.</summary>
public static class RowVersions
{
    public static string? Encode(byte[]? v) => v is null || v.Length == 0 ? null : Convert.ToBase64String(v);

    /// <summary>Null when the client sent no version (older app); throws nothing on garbage - returns an empty array, which never matches.</summary>
    public static byte[]? Decode(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        try { return Convert.FromBase64String(v); } catch (FormatException) { return Array.Empty<byte>(); }
    }

    public static bool Matches(byte[]? current, byte[] expected) => current is not null && current.AsSpan().SequenceEqual(expected);

    public static ServiceError Stale() => new(ErrorKind.Conflict, "concurrent_edit", "This record was changed by someone else after you opened it. Go back, refresh and try again.");
}

/// <summary>Login identifier rules shared by user administration and the bootstrap administrator (review Q6).</summary>
public static class LoginRules
{
    public const int MinLength = 2, MaxEmployeeCodeLength = 30, MaxUsernameLength = 50;
    private static readonly System.Text.RegularExpressions.Regex Allowed = new(@"^[A-Za-z0-9._-]+$");

    /// <summary>Matches qa.LoginIdentifiers.NormalizedIdentifier and the 08 backfill: UPPER(trimmed value).</summary>
    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public static string? EmployeeCodeProblem(string code) =>
        code.Length < MinLength || code.Length > MaxEmployeeCodeLength || !Allowed.IsMatch(code)
            ? $"Employee code: {MinLength}-{MaxEmployeeCodeLength} characters, letters, digits, '.', '_' or '-'." : null;

    public static string? UsernameProblem(string name) =>
        name.Length < MinLength || name.Length > MaxUsernameLength || !Allowed.IsMatch(name)
            ? $"Username: {MinLength}-{MaxUsernameLength} characters, letters, digits, '.', '_' or '-'." : null;

    /// <summary>The qa.LoginIdentifiers rows for one user.</summary>
    public static IReadOnlyList<LoginIdentifier> IdentifiersFor(User u)
    {
        var un = Normalize(u.Username); var ec = Normalize(u.EmployeeCode);
        return un == ec
            ? new[] { new LoginIdentifier { NormalizedIdentifier = un, UserId = u.Id, Kind = "B" } }
            : new[] { new LoginIdentifier { NormalizedIdentifier = un, UserId = u.Id, Kind = "U" }, new LoginIdentifier { NormalizedIdentifier = ec, UserId = u.Id, Kind = "E" } };
    }
}
