using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Services;
using Quality.Core.Entities;

namespace Quality.Data;

public sealed class EfMasterData : IMasterData
{
    private readonly MasterDbContext _db;
    private readonly TimeProvider _time;
    public EfMasterData(MasterDbContext db, TimeProvider time) => (_db, _time) = (db, time);

    /// <summary>qa.LoginIdentifiers has one row per identifier, so at most one user can match (review Q5).</summary>
    public async Task<User?> FindUserByLoginAsync(string login)
    {
        var key = LoginRules.Normalize(login);
        var userId = await _db.LoginIdentifiers.AsNoTracking().Where(x => x.NormalizedIdentifier == key).Select(x => (int?)x.UserId).SingleOrDefaultAsync();
        return userId is null ? null : await _db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId);
    }

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

    public async Task LockStageForPhenomenonChangesAsync(int stageId)
    {
        RequireTransaction();
        await _db.Database.SqlQuery<int>($"SELECT Id AS Value FROM qa.InspectionStages WITH (UPDLOCK, HOLDLOCK) WHERE Id = {stageId}").ToListAsync();
    }

    public async Task<(Machine? Machine, RejectionPhenomenon? Phenomenon)> ReadMastersForEntryLockedAsync(int machineId, int phenomenonId)
    {
        RequireTransaction();
        // HOLDLOCK = locking read even under READ_COMMITTED_SNAPSHOT, shared locks kept until commit (review Q9).
        var m = await _db.Machines.FromSqlInterpolated($"SELECT * FROM qa.Machines WITH (HOLDLOCK, ROWLOCK) WHERE Id = {machineId}").AsNoTracking().FirstOrDefaultAsync();
        var p = await _db.RejectionPhenomena.FromSqlInterpolated($"SELECT * FROM qa.RejectionPhenomena WITH (HOLDLOCK, ROWLOCK) WHERE Id = {phenomenonId}").AsNoTracking().FirstOrDefaultAsync();
        return (m, p);
    }

    // A real UPDATE is the most reliable way to take (and keep) an exclusive row lock: it waits for every shared lock an
    // in-flight entry save holds. It moves the row version, so a tracked copy is reloaded afterwards.
    public async Task LockMachineExclusiveAsync(int machineId)
    {
        RequireTransaction();
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE qa.Machines SET IsActive = IsActive WHERE Id = {machineId}");
        if (_db.ChangeTracker.Entries<Machine>().FirstOrDefault(e => e.Entity.Id == machineId) is { } tracked) await tracked.ReloadAsync();
    }

    public async Task LockPhenomenonExclusiveAsync(int phenomenonId)
    {
        RequireTransaction();
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE qa.RejectionPhenomena SET IsActive = IsActive WHERE Id = {phenomenonId}");
        if (_db.ChangeTracker.Entries<RejectionPhenomenon>().FirstOrDefault(e => e.Entity.Id == phenomenonId) is { } tracked) await tracked.ReloadAsync();
    }

    // ---- shifts and corrections ----
    public async Task<IReadOnlyList<ShiftDefinition>> GetShiftDefinitionsAsync() => await _db.ShiftDefinitions.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync();
    public void AddCorrection(EntryCorrection c) => _db.EntryCorrections.Add(c);

    public async Task<EntryCorrection?> GetCorrectionLockedAsync(long id)
    {
        RequireTransaction();
        // Tracked, with an update lock held to commit: a second decision on the same request waits, then sees it is no longer Pending.
        var c = await _db.EntryCorrections.FromSqlInterpolated($"SELECT * FROM qa.EntryCorrections WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = {id}").FirstOrDefaultAsync();
        if (c is not null) await _db.Entry(c).ReloadAsync();   // if it was already tracked, make sure we hold the committed values
        return c;
    }

    public Task<EntryCorrection?> GetCorrectionAsync(long id) => _db.EntryCorrections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);

    public async Task<IReadOnlyList<EntryCorrection>> ListCorrectionsAsync(IReadOnlyCollection<int>? shopIds, int? requestedBy, string? status, int limit)
    {
        var q = _db.EntryCorrections.AsNoTracking().AsQueryable();
        if (shopIds is not null) q = q.Where(c => shopIds.Contains(c.ShopId));
        if (requestedBy is int u) q = q.Where(c => c.RequestedBy == u);
        if (status is not null) q = q.Where(c => c.Status == status);
        return await q.OrderByDescending(c => c.RequestedAt).ThenByDescending(c => c.Id).Take(limit).ToListAsync();
    }

    public async Task<IReadOnlyList<ProductionCount>> GetProductionCountsAsync(IReadOnlyCollection<int> shopIds, DateOnly fromDate, DateOnly toDate, string? shiftCode, int? machineId)
    {
        var q = _db.ProductionCounts.AsNoTracking().Where(p => shopIds.Contains(p.ShopId) && p.ProductionDate >= fromDate && p.ProductionDate <= toDate);
        if (shiftCode is not null) q = q.Where(p => p.ShiftCode == shiftCode);
        if (machineId is int m) q = q.Where(p => p.MachineId == m);
        return await q.ToListAsync();
    }

    public async Task<IReadOnlyList<ProductionCount>> GetProductionCountsForUpdateAsync(IReadOnlyCollection<int> machineIds, DateOnly fromDate, DateOnly toDate)
    {
        RequireTransaction();
        // UPDLOCK+HOLDLOCK on each machine's (MachineId, ProductionDate) key range through the unique index that leads on
        // MachineId: two uploads (or an upload and the feed) touching the same machine and days are serialised instead of
        // colliding on the unique key; other machines are not blocked. The predicate sits INSIDE the hinted query, so the
        // optimizer cannot turn it into a table-wide range lock. One parameterised query per distinct machine.
        var rows = new List<ProductionCount>();
        foreach (var id in machineIds.Distinct().OrderBy(x => x))   // fixed order: no deadlock between two uploads
            rows.AddRange(await _db.ProductionCounts
                .FromSqlInterpolated($"SELECT * FROM qa.ProductionCounts WITH (UPDLOCK, HOLDLOCK, INDEX(UQ_ProductionCounts_Machine_Day_Shift)) WHERE MachineId = {id} AND ProductionDate BETWEEN {fromDate} AND {toDate}")
                .ToListAsync());
        return rows;
    }

    public void AddProductionCount(ProductionCount c) => _db.ProductionCounts.Add(c);

    public Task<int> CountCorrectionsAsync(IReadOnlyCollection<int>? shopIds, int? requestedBy, string? status)
    {
        var q = _db.EntryCorrections.AsNoTracking().AsQueryable();
        if (shopIds is not null) q = q.Where(c => shopIds.Contains(c.ShopId));
        if (requestedBy is int u) q = q.Where(c => c.RequestedBy == u);
        if (status is not null) q = q.Where(c => c.Status == status);
        return q.CountAsync();
    }

    public async Task<IReadOnlyList<EntryCorrection>> GetCorrectionsForEntriesAsync(int shopId, IReadOnlyCollection<long> entryIds)
    {
        if (entryIds.Count == 0) return Array.Empty<EntryCorrection>();
        var all = await _db.EntryCorrections.AsNoTracking().Where(c => c.ShopId == shopId && entryIds.Contains(c.EntryId)).ToListAsync();
        return all.GroupBy(c => c.EntryId).Select(g => g.OrderByDescending(c => c.Id).First()).ToList();   // latest request per entry
    }

    public async Task<IReadOnlyDictionary<int, string>> GetUserCodesAsync(IReadOnlyCollection<int> userIds) =>
        await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.EmployeeCode);

    public void ExpectRowVersion(object entity, byte[] rowVersion) => _db.Entry(entity).Property("RowVer").OriginalValue = rowVersion;
    public void Detach(object entity) => _db.Entry(entity).State = EntityState.Detached;

    private void RequireTransaction()
    {
        if (_db.Database.CurrentTransaction is null) throw new InvalidOperationException("This lock must be taken inside InTransactionAsync.");
    }

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
    public async Task<bool> IsMachineReferencedInMasterAsync(int machineId) =>
        await _db.ProductionCounts.AnyAsync(c => c.MachineId == machineId) || await _db.EntryCorrections.AnyAsync(c => c.ProposedMachineId == machineId);
    public Task<bool> IsPhenomenonReferencedInMasterAsync(int phenomenonId) => _db.EntryCorrections.AnyAsync(c => c.ProposedPhenomenonId == phenomenonId);

    // ---- user administration ----
    public async Task<IReadOnlyList<User>> GetUsersAsync(string? search, bool? isActive)
    {
        var q = _db.Users.AsNoTracking().Include(u => u.Role).AsQueryable();
        if (isActive is bool a) q = q.Where(u => u.IsActive == a);
        if (!string.IsNullOrWhiteSpace(search)) { var t = search.Trim(); q = q.Where(u => u.Username.Contains(t) || u.EmployeeCode.Contains(t) || u.DisplayName.Contains(t)); }
        return await q.OrderBy(u => u.Id).ToListAsync();
    }
    public Task<User?> GetUserAsync(int userId) => _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId);
    public Task<bool> LoginIdentifierTakenAsync(string identifier, int? excludeUserId)
    {
        var key = LoginRules.Normalize(identifier);
        return _db.LoginIdentifiers.AnyAsync(x => x.NormalizedIdentifier == key && x.UserId != excludeUserId);
    }
    public void AddLoginIdentifiers(User user) => _db.LoginIdentifiers.AddRange(LoginRules.IdentifiersFor(user));
    public Task<Role?> GetRoleAsync(string roleName) => _db.Roles.FirstOrDefaultAsync(r => r.RoleName == roleName);
    public Task<int> CountOtherActiveAdministratorsAsync(int excludeUserId) =>
        _db.Users.CountAsync(u => u.Id != excludeUserId && u.IsActive && u.Role!.RoleName == RoleNames.Administrator);

    public async Task<int> LockAndCountOtherActiveAdministratorsAsync(int excludeUserId)
    {
        RequireTransaction();
        // UPDLOCK + HOLDLOCK: update locks on every active administrator row (and the key range), held to commit.
        // A second demotion/deactivation blocks here until the first commits, then counts the committed state (review Q3).
        var ids = await _db.Database.SqlQuery<int>($"""
            SELECT u.Id AS Value FROM qa.Users u WITH (UPDLOCK, HOLDLOCK)
            JOIN qa.Roles r ON r.Id = u.RoleId
            WHERE r.RoleName = {RoleNames.Administrator} AND u.IsActive = 1
            """).ToListAsync();
        return ids.Count(id => id != excludeUserId);
    }
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
    /// <summary>Translates SQL Server unique-key and row-version failures into the Core exceptions the services handle.</summary>
    public async Task SaveChangesAsync()
    {
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException ex) { throw new ConcurrencyConflictException(ex); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql)
        {
            throw new UniqueViolationException(ConstraintName(sql.Message), ex);
        }
    }

    // 2627: "Violation of UNIQUE KEY constraint 'UQ_x'..." / "Violation of PRIMARY KEY constraint 'PK_x'..."; 2601: "... unique index 'IX_x'."
    private static readonly Regex NameInMessage = new(@"(?:constraint|index) '([^']+)'", RegexOptions.CultureInvariant);
    private static string? ConstraintName(string message) => NameInMessage.Match(message) is { Success: true } m ? m.Groups[1].Value : null;

    public async Task InTransactionAsync(Func<Task> work)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        await work();
        await tx.CommitAsync();   // disposing without commit rolls back on exception
    }
}
