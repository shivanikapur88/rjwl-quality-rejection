using Quality.Core.Entities;

namespace Quality.Core.Abstractions;

/// <summary>Master-database access (users, shops, stages, machines, phenomena, audit). Implemented with EF Core in Quality.Data.</summary>
public interface IMasterData
{
    /// <summary>Resolves a username OR employee code through qa.LoginIdentifiers, so at most one user can match (review Q5). Includes Role.</summary>
    Task<User?> FindUserByLoginAsync(string login);
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
    /// <summary>Call inside <see cref="InTransactionAsync"/>: serialises code allocation and name checks for one stage until commit (review Q8).</summary>
    Task LockStageForPhenomenonChangesAsync(int stageId);

    // ---- entry-save / delete coordination across databases (review Q9) ----
    /// <summary>
    /// Call inside <see cref="InTransactionAsync"/> while an entry is being saved. Reads the machine and phenomenon with shared
    /// locks held until commit, so neither can be deleted or deactivated until the entry insert has committed.
    /// </summary>
    Task<(Machine? Machine, RejectionPhenomenon? Phenomenon)> ReadMastersForEntryLockedAsync(int machineId, int phenomenonId);
    /// <summary>Call inside <see cref="InTransactionAsync"/> BEFORE the "never used" check: takes an exclusive lock on the machine row.</summary>
    Task LockMachineExclusiveAsync(int machineId);
    /// <summary>Call inside <see cref="InTransactionAsync"/> BEFORE the "never used" check: takes an exclusive lock on the phenomenon row.</summary>
    Task LockPhenomenonExclusiveAsync(int phenomenonId);
    /// <summary>Makes the next save of <paramref name="entity"/> fail with <see cref="ConcurrencyConflictException"/> unless its row version still equals <paramref name="rowVersion"/>.</summary>
    void ExpectRowVersion(object entity, byte[] rowVersion);
    /// <summary>Stops tracking an entity whose save failed, so a retry can add a fresh one.</summary>
    void Detach(object entity);

    // ---- shifts and corrections (features #1, #2) ----
    Task<IReadOnlyList<ShiftDefinition>> GetShiftDefinitionsAsync();
    void AddCorrection(EntryCorrection c);
    /// <summary>Call inside <see cref="InTransactionAsync"/>: reads the request with an update lock held to commit, so two decisions cannot both apply.</summary>
    Task<EntryCorrection?> GetCorrectionLockedAsync(long id);
    Task<EntryCorrection?> GetCorrectionAsync(long id);
    /// <summary>Requests filtered by shop set (null = all), requester (null = anyone) and status (null = any); newest first.</summary>
    Task<IReadOnlyList<EntryCorrection>> ListCorrectionsAsync(IReadOnlyCollection<int>? shopIds, int? requestedBy, string? status, int limit);
    Task<int> CountCorrectionsAsync(IReadOnlyCollection<int>? shopIds, int? requestedBy, string? status);
    /// <summary>Latest request per entry for the given entries of one shop (to show "correction pending/approved" in history).</summary>
    Task<IReadOnlyList<EntryCorrection>> GetCorrectionsForEntriesAsync(int shopId, IReadOnlyCollection<long> entryIds);
    Task<IReadOnlyDictionary<int, string>> GetUserCodesAsync(IReadOnlyCollection<int> userIds);

    // ---- production counts (round 3, #8) ----
    Task<IReadOnlyList<ProductionCount>> GetProductionCountsAsync(IReadOnlyCollection<int> shopIds, DateOnly fromDate, DateOnly toDate, string? shiftCode, int? machineId);
    /// <summary>Tracked rows for the given machines and day range (for an upsert inside <see cref="InTransactionAsync"/>, with update locks).</summary>
    Task<IReadOnlyList<ProductionCount>> GetProductionCountsForUpdateAsync(IReadOnlyCollection<int> machineIds, DateOnly fromDate, DateOnly toDate);
    void AddProductionCount(ProductionCount c);

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
    /// <summary>
    /// Call inside <see cref="InTransactionAsync"/>. Takes update locks on every active administrator row (held to commit),
    /// so two concurrent demotions/deactivations are serialised and cannot both pass the "another admin remains" check (review Q3).
    /// </summary>
    Task<int> LockAndCountOtherActiveAdministratorsAsync(int excludeUserId);
    /// <summary>Adds the qa.LoginIdentifiers rows for a saved user (same transaction). A clash raises <see cref="UniqueViolationException"/>.</summary>
    void AddLoginIdentifiers(User user);
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

/// <summary>A unique constraint or unique index was violated. <see cref="Name"/> is the constraint/index name when known.</summary>
public sealed class UniqueViolationException : Exception
{
    public UniqueViolationException(string? name, Exception? inner = null) : base("Unique constraint violated: " + name, inner) => Name = name;
    public string? Name { get; }
}

/// <summary>The record was changed by someone else after the caller read it (row version mismatch).</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(Exception? inner = null) : base("The record was changed by someone else.", inner) { }
}

public sealed class DuplicateSubmissionException : Exception
{
    public DuplicateSubmissionException() : base("A rejection entry with this SubmissionId already exists.") { }
}

/// <summary>ShopId is mandatory: two shops (e.g. PRE and FINAL) may store entries in the same table.</summary>
/// <summary>
/// ShopId is mandatory: two shops (e.g. PRE and FINAL) may store entries in the same table.
/// FromUtc is inclusive; ToUtc is inclusive when ToInclusive is true (legacy list), exclusive otherwise (paged list).
/// Cursor: only rows ordered strictly after (CursorTimeUtc, CursorIdBelow) in "ServerCreatedAt DESC, Id DESC" order,
/// i.e. ServerCreatedAt &lt; CursorTimeUtc, or ServerCreatedAt = CursorTimeUtc AND Id &lt; CursorIdBelow.
/// </summary>
public sealed record EntryFilter(int ShopId, int? InspectorId, int? StageId, DateTime? FromUtc, DateTime? ToUtc, int Limit,
    bool ToInclusive = true, DateTime? CursorTimeUtc = null, long CursorIdBelow = long.MinValue);

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

    /// <summary>Totals of NON-reversed entries of one shop, grouped by production date, shift, machine and phenomenon (reports, #4/#6).</summary>
    Task<IReadOnlyList<EntryAggregateRow>> AggregateAsync(int shopId, DateOnly fromDate, DateOnly toDate, string? shiftCode);

    /// <summary>
    /// Applies an approved correction in ONE transaction of this entry database: marks entry <paramref name="entryId"/> of
    /// <paramref name="shopId"/> as reversed by <paramref name="correctionId"/> and, if given, inserts <paramref name="replacement"/>.
    /// Idempotent for the same correction (a retry returns the replacement already inserted).
    /// </summary>
    /// <exception cref="CorrectionConflictException">entry missing, or already reversed by another correction</exception>
    Task<QualityRejectionEntry?> ApplyCorrectionAsync(int shopId, long entryId, long correctionId, DateTime nowUtc, QualityRejectionEntry? replacement);

    /// <summary>NON-reversed entries of one shop for production days fromDate..toDate (drill-down list), newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<QualityRejectionEntry>> ListForReportAsync(int shopId, DateOnly fromDate, DateOnly toDate, string? shiftCode, int? machineId, int? phenomenonId, int limit);
}

public sealed record EntryAggregateRow(DateOnly ProductionDate, string ShiftCode, int MachineId, string MachineCode, string MachineName,
    int PhenomenonId, string PhenomenonName, int Quantity, int Entries);

public sealed class CorrectionConflictException : Exception
{
    public CorrectionConflictException(string message) : base(message) { }
}

/// <summary>Maps Shop.EntryStoreKey to the right database. The Android app never chooses a database.</summary>
public interface IEntryStoreRouter
{
    IEntryStore For(string entryStoreKey);
    IReadOnlyList<IEntryStore> All();
    /// <summary>The configured store keys (GDC, CNC, PRE, FINAL).</summary>
    IReadOnlyCollection<string> Keys { get; }
    /// <summary>Readiness probe for one entry database.</summary>
    Task<bool> CanConnectAsync(string entryStoreKey, CancellationToken ct);
}

public interface IPasswordVerifier { bool Verify(User user, string password); }
public interface IPasswordHashService { string Hash(User user, string password); }
/// <summary>Issues a signed token carrying the user's id, employee code, role and TokenVersion.</summary>
public interface ITokenIssuer { (string Token, DateTime ExpiresAtUtc) Issue(User user); }
