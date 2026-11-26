using Microsoft.EntityFrameworkCore;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Entities;

namespace Quality.Data;

public sealed class EfMasterData : IMasterData
{
    private readonly MasterDbContext _db;
    private readonly TimeProvider _time;
    public EfMasterData(MasterDbContext db, TimeProvider time) => (_db, _time) = (db, time);

    public Task<User?> FindUserByLoginAsync(string login) =>
        _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Username == login || u.EmployeeCode == login);

    public async Task<IReadOnlyList<Shop>> GetAllShopsAsync() => await _db.Shops.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
    public async Task<IReadOnlyList<int>> GetUserShopIdsAsync(int userId) => await _db.UserShops.Where(x => x.UserId == userId).Select(x => x.ShopId).ToListAsync();
    public Task<Shop?> GetShopAsync(int shopId) => _db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shopId);

    public async Task<IReadOnlyList<InspectionStage>> GetStagesForShopAsync(int shopId) =>
        await _db.ShopStages.AsNoTracking().Where(x => x.ShopId == shopId && x.IsActive && x.InspectionStage!.IsActive)
            .Select(x => x.InspectionStage!).OrderBy(s => s.SortOrder).ToListAsync();

    public Task<InspectionStage?> GetStageAsync(int stageId) => _db.InspectionStages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stageId);
    public Task<bool> IsStageMappedToShopAsync(int shopId, int stageId) => _db.ShopStages.AnyAsync(x => x.ShopId == shopId && x.InspectionStageId == stageId && x.IsActive);

    public async Task<IReadOnlyList<Shop>> GetShopsForStageAsync(int stageId) =>
        await _db.ShopStages.AsNoTracking().Where(x => x.InspectionStageId == stageId && x.IsActive).Select(x => x.Shop!).ToListAsync();

    public Task<Machine?> GetMachineAsync(int machineId) => _db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId);
    public async Task<IReadOnlyList<Machine>> GetActiveMachinesForShopAsync(int shopId) =>
        await _db.Machines.AsNoTracking().Where(m => m.ShopId == shopId && m.IsActive).OrderBy(m => m.MachineCode).ToListAsync();

    public async Task<IReadOnlyList<RejectionPhenomenon>> GetPhenomenaAsync(int? stageId, bool? isActive, string? search)
    {
        var q = _db.RejectionPhenomena.AsQueryable();
        if (stageId is int s) q = q.Where(p => p.InspectionStageId == s);
        if (isActive is bool a) q = q.Where(p => p.IsActive == a);
        if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim(); q = q.Where(p => p.PhenomenonName.Contains(t)); }
        return await q.OrderBy(p => p.Id).ToListAsync();   // tracked on purpose: services mutate and save
    }

    public Task<RejectionPhenomenon?> GetPhenomenonAsync(int id) => _db.RejectionPhenomena.FirstOrDefaultAsync(p => p.Id == id);

    public Task<bool> NormalizedNameExistsAsync(int stageId, string normalizedName, int? excludeId) =>
        _db.RejectionPhenomena.AnyAsync(p => p.InspectionStageId == stageId && p.NormalizedName == normalizedName && p.Id != excludeId);

    public async Task<string> NextPhenomenonCodeAsync(InspectionStage stage)
    {
        var codes = await _db.RejectionPhenomena.Where(p => p.InspectionStageId == stage.Id).Select(p => p.PhenomenonCode).ToListAsync();
        int max = codes.Select(c => int.TryParse(c[(c.LastIndexOf('-') + 1)..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"{stage.StageCode}-{max + 1:000}";
    }

    // ---- machine administration ----
    public async Task<IReadOnlyList<Machine>> GetMachinesAsync(int? shopId, bool? isActive, string? search)
    {
        var q = _db.Machines.AsNoTracking().AsQueryable();
        if (shopId is int s) q = q.Where(m => m.ShopId == s);
        if (isActive is bool a) q = q.Where(m => m.IsActive == a);
        if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim(); q = q.Where(m => m.MachineCode.Contains(t) || m.MachineName.Contains(t)); }
        return await q.OrderBy(m => m.ShopId).ThenBy(m => m.MachineCode).ToListAsync();
    }
    public Task<Machine?> GetMachineForEditAsync(int machineId) => _db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
    // SQL Server's default collation is case-insensitive, which is the behaviour we want for codes.
    public Task<bool> MachineCodeExistsAsync(int shopId, string machineCode, int? excludeMachineId) =>
        _db.Machines.AnyAsync(m => m.ShopId == shopId && m.MachineCode == machineCode && m.Id != excludeMachineId);
    public void AddMachine(Machine m) => _db.Machines.Add(m);
    public void RemoveMachine(Machine m) => _db.Machines.Remove(m);

    // ---- user administration ----
    public async Task<IReadOnlyList<User>> GetUsersAsync(string? search, bool? isActive)
    {
        var q = _db.Users.AsNoTracking().Include(u => u.Role).AsQueryable();
        if (isActive is bool a) q = q.Where(u => u.IsActive == a);
        if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim(); q = q.Where(u => u.Username.Contains(t) || u.EmployeeCode.Contains(t) || u.DisplayName.Contains(t)); }
        return await q.OrderBy(u => u.Id).ToListAsync();
    }
    public Task<User?> GetUserAsync(int userId) => _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId);
    public Task<bool> LoginIdentifierTakenAsync(string identifier, int? excludeUserId) =>
        _db.Users.AnyAsync(u => (u.Username == identifier || u.EmployeeCode == identifier) && u.Id != excludeUserId);
    public Task<Role?> GetRoleAsync(string roleName) => _db.Roles.FirstOrDefaultAsync(r => r.RoleName == roleName);
    public Task<int> CountOtherActiveAdministratorsAsync(int excludeUserId) =>
        _db.Users.CountAsync(u => u.Id != excludeUserId && u.IsActive && u.Role!.RoleName == RoleNames.Administrator);
    public async Task<IReadOnlyList<UserShop>> GetAllUserShopsAsync() => await _db.UserShops.AsNoTracking().ToListAsync();
    public async Task ReplaceUserShopsAsync(int userId, IReadOnlyCollection<int> shopIds)
    {
        // diff instead of delete-all/insert-all: re-adding an existing key in one SaveChanges would clash in the change tracker
        var existing = await _db.UserShops.Where(x => x.UserId == userId).ToListAsync();
        _db.UserShops.RemoveRange(existing.Where(x => !shopIds.Contains(x.ShopId)));
        foreach (var id in shopIds.Where(id => existing.All(x => x.ShopId != id))) _db.UserShops.Add(new UserShop { UserId = userId, ShopId = id });
    }
    public void AddUser(User u) => _db.Users.Add(u);

    public void AddPhenomenon(RejectionPhenomenon p) => _db.RejectionPhenomena.Add(p);
    public void RemovePhenomenon(RejectionPhenomenon p) => _db.RejectionPhenomena.Remove(p);
    public void AddAudit(AuditLogEntry entry)
    {
        if (entry.Timestamp == default) entry.Timestamp = _time.GetUtcNow().UtcDateTime;
        _db.AuditLog.Add(entry);
    }
    public Task SaveChangesAsync() => _db.SaveChangesAsync();

    public async Task InTransactionAsync(Func<Task> work)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        await work();
        await tx.CommitAsync();   // disposing without commit rolls back on exception
    }
}
