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
