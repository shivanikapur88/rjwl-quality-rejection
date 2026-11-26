using Quality.Core.Entities;

namespace Quality.Core.Abstractions;

/// <summary>Master-database access (users, shops, stages, machines, phenomena, audit). Implemented with EF Core in Quality.Data.</summary>
public interface IMasterData
{
    Task<User?> FindUserByLoginAsync(string login);                         // matches Username or EmployeeCode; includes Role
    Task<IReadOnlyList<Shop>> GetAllShopsAsync();
    Task<IReadOnlyList<int>> GetUserShopIdsAsync(int userId);
    Task<Shop?> GetShopAsync(int shopId);
    Task<IReadOnlyList<InspectionStage>> GetStagesForShopAsync(int shopId); // active stages with an active shop mapping, ordered by SortOrder
    Task<InspectionStage?> GetStageAsync(int stageId);
    Task<bool> IsStageMappedToShopAsync(int shopId, int stageId);           // active mapping
    Task<IReadOnlyList<Shop>> GetShopsForStageAsync(int stageId);
    Task<Machine?> GetMachineAsync(int machineId);
    Task<IReadOnlyList<Machine>> GetActiveMachinesForShopAsync(int shopId);
    Task<IReadOnlyList<RejectionPhenomenon>> GetPhenomenaAsync(int? stageId, bool? isActive, string? search); // ordered by Id (Excel order)
    Task<RejectionPhenomenon?> GetPhenomenonAsync(int id);
    Task<bool> NormalizedNameExistsAsync(int stageId, string normalizedName, int? excludePhenomenonId);
    Task<string> NextPhenomenonCodeAsync(InspectionStage stage);

    // ---- machine administration ----
    Task<IReadOnlyList<Machine>> GetMachinesAsync(int? shopId, bool? isActive, string? search);   // ordered by shop, code
    Task<Machine?> GetMachineForEditAsync(int machineId);                                        // tracked
    Task<bool> MachineCodeExistsAsync(int shopId, string machineCode, int? excludeMachineId);     // case-insensitive, per shop
    void AddMachine(Machine m);
    void RemoveMachine(Machine m);

    // ---- user administration ----
    Task<IReadOnlyList<User>> GetUsersAsync(string? search, bool? isActive);                      // includes Role
    Task<User?> GetUserAsync(int userId);                                                         // tracked, includes Role
    /// <summary>True if any other user already uses this value as Username OR EmployeeCode (login accepts either, so they share one namespace).</summary>
    Task<bool> LoginIdentifierTakenAsync(string identifier, int? excludeUserId);
    Task<Role?> GetRoleAsync(string roleName);
    Task<int> CountOtherActiveAdministratorsAsync(int excludeUserId);
    Task<IReadOnlyList<UserShop>> GetAllUserShopsAsync();
    Task ReplaceUserShopsAsync(int userId, IReadOnlyCollection<int> shopIds);
    void AddUser(User u);

    void AddPhenomenon(RejectionPhenomenon p);
    void RemovePhenomenon(RejectionPhenomenon p);
    void AddAudit(AuditLogEntry entry);
    Task SaveChangesAsync();
    /// <summary>Runs <paramref name="work"/> in one database transaction (commit on success, roll back on exception).</summary>
    Task InTransactionAsync(Func<Task> work);
}

public sealed class DuplicateSubmissionException : Exception
{
    public DuplicateSubmissionException() : base("A rejection entry with this SubmissionId already exists.") { }
}

/// <summary>ShopId is mandatory: two shops (e.g. PRE and FINAL) may store entries in the same table.</summary>
public sealed record EntryFilter(int ShopId, int? InspectorId, int? StageId, DateTime? FromUtc, DateTime? ToUtc, int Limit);

/// <summary>Entry storage for ONE shop database (GDC_AndonDB or MachineShopDB).</summary>
public interface IEntryStore
{
    Task<QualityRejectionEntry?> FindBySubmissionIdAsync(Guid submissionId);
    /// <exception cref="DuplicateSubmissionException">unique SubmissionId violated (concurrent retry)</exception>
    Task<QualityRejectionEntry> InsertAsync(QualityRejectionEntry entry);
    Task<QualityRejectionEntry?> GetByIdAsync(long id);
    Task<IReadOnlyList<QualityRejectionEntry>> ListAsync(EntryFilter filter); // newest first
    Task<bool> IsPhenomenonUsedAsync(int phenomenonId);
    Task<bool> IsMachineUsedAsync(int machineId);
}

/// <summary>Maps Shop.EntryStoreKey to the right database. The Android app never chooses a database.</summary>
public interface IEntryStoreRouter
{
    IEntryStore For(string entryStoreKey);
    IReadOnlyList<IEntryStore> All();
}

public interface IPasswordVerifier { bool Verify(User user, string password); }
public interface IPasswordHashService { string Hash(User user, string password); }
public interface ITokenIssuer { (string Token, DateTime ExpiresAtUtc) Issue(User user); }
