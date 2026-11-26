using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Quality.Core.Abstractions;
using Quality.Core.Entities;

namespace Quality.Data;

public sealed class EfEntryStore : IEntryStore
{
    private readonly EntryDbContext _db;
    public EfEntryStore(EntryDbContext db) => _db = db;

    public Task<QualityRejectionEntry?> FindBySubmissionIdAsync(Guid submissionId) =>
        _db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.SubmissionId == submissionId);

    public async Task<QualityRejectionEntry> InsertAsync(QualityRejectionEntry entry)
    {
        _db.Entries.Add(entry);
        try { await _db.SaveChangesAsync(); return entry; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            _db.Entry(entry).State = EntityState.Detached;
            throw new DuplicateSubmissionException();   // unique index UQ_QRE_SubmissionId
        }
    }

    public Task<QualityRejectionEntry?> GetByIdAsync(long id) => _db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);

    public async Task<IReadOnlyList<QualityRejectionEntry>> ListAsync(EntryFilter f)
    {
        var q = _db.Entries.AsNoTracking().Where(e => e.ShopId == f.ShopId);
        if (f.InspectorId is int i) q = q.Where(e => e.InspectorId == i);
        if (f.StageId is int s) q = q.Where(e => e.InspectionStageId == s);
        if (f.FromUtc is DateTime from) q = q.Where(e => e.ServerCreatedAt >= from);
        if (f.ToUtc is DateTime to) q = f.ToInclusive ? q.Where(e => e.ServerCreatedAt <= to) : q.Where(e => e.ServerCreatedAt < to);
        if (f.CursorTimeUtc is DateTime ct)
        {
            var idBelow = f.CursorIdBelow;
            q = q.Where(e => e.ServerCreatedAt < ct || (e.ServerCreatedAt == ct && e.Id < idBelow));
        }
        return await q.OrderByDescending(e => e.ServerCreatedAt).ThenByDescending(e => e.Id).Take(f.Limit).ToListAsync();
    }

    // Uses the filtered index IX_QRE_NoProductionDate (db/12), which is normally empty, so this is not a table scan.
    public Task<int> CountWithoutProductionDateAsync(CancellationToken ct) => _db.Entries.CountAsync(e => e.ProductionDate == null, ct);

    public async Task<IReadOnlyList<EntryAggregateRow>> AggregateAsync(int shopId, DateOnly fromDate, DateOnly toDate, string? shiftCode)
    {
        var q = _db.Entries.AsNoTracking().Where(e => e.ShopId == shopId && e.ReversedAt == null
            && e.ProductionDate != null && e.ProductionDate >= fromDate && e.ProductionDate <= toDate);
        if (shiftCode is not null) q = q.Where(e => e.ShiftCode == shiftCode);
        var rows = await q.GroupBy(e => new { e.ProductionDate, e.ShiftCode, e.MachineId, e.RejectionPhenomenonId })
            .Select(g => new
            {
                g.Key.ProductionDate, g.Key.ShiftCode, g.Key.MachineId, g.Key.RejectionPhenomenonId,
                MachineCode = g.Max(e => e.MachineCode), MachineName = g.Max(e => e.MachineName), Phenomenon = g.Max(e => e.PhenomenonNameSnapshot),
                Quantity = g.Sum(e => e.Quantity), Entries = g.Count(),
            }).ToListAsync();
        // Snapshot columns are NOT NULL; the ?? only satisfies the compiler's view of MAX() over strings.
        return rows.Select(r => new EntryAggregateRow(r.ProductionDate!.Value, r.ShiftCode ?? "?", r.MachineId, r.MachineCode ?? "", r.MachineName ?? "",
            r.RejectionPhenomenonId, r.Phenomenon ?? "", r.Quantity, r.Entries)).ToList();
    }

    public async Task<IReadOnlyList<QualityRejectionEntry>> ListForReportAsync(int shopId, DateOnly fromDate, DateOnly toDate, string? shiftCode, int? machineId, int? phenomenonId, int limit)
    {
        var q = _db.Entries.AsNoTracking().Where(e => e.ShopId == shopId && e.ReversedAt == null
            && e.ProductionDate != null && e.ProductionDate >= fromDate && e.ProductionDate <= toDate);
        if (shiftCode is not null) q = q.Where(e => e.ShiftCode == shiftCode);
        if (machineId is int m) q = q.Where(e => e.MachineId == m);
        if (phenomenonId is int p) q = q.Where(e => e.RejectionPhenomenonId == p);
        return await q.OrderByDescending(e => e.ServerCreatedAt).ThenByDescending(e => e.Id).Take(limit).ToListAsync();
    }

    public async Task<QualityRejectionEntry?> ApplyCorrectionAsync(int shopId, long entryId, long correctionId, DateTime nowUtc, QualityRejectionEntry? replacement)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        // Lock the original row; set the reversal marker only if it is not reversed yet (or already by THIS correction: retry).
        var original = await _db.Entries.FromSqlInterpolated($"SELECT * FROM qa.QualityRejectionEntries WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = {entryId}")
            .AsNoTracking().FirstOrDefaultAsync();
        if (original is null || original.ShopId != shopId) throw new CorrectionConflictException("The entry no longer exists.");
        if (original.ReversedByCorrectionId is long other && other != correctionId) throw new CorrectionConflictException("This entry was already reversed by another correction.");
        if (original.ReversedAt is null)
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE qa.QualityRejectionEntries SET ReversedAt = {nowUtc}, ReversedByCorrectionId = {correctionId} WHERE Id = {entryId} AND ReversedAt IS NULL");

        QualityRejectionEntry? saved = null;
        if (replacement is not null)
        {
            saved = await _db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.SubmissionId == replacement.SubmissionId);   // retry: already inserted
            if (saved is not null && (saved.ShopId != shopId || saved.CorrectsEntryId != entryId))
                throw new CorrectionConflictException("The replacement id is already used by another entry.");   // never adopt a foreign row
            if (saved is null)
            {
                _db.Entries.Add(replacement);
                await _db.SaveChangesAsync();
                _db.Entry(replacement).State = EntityState.Detached;
                saved = replacement;
            }
        }
        await tx.CommitAsync();
        return saved;
    }

    public async Task<IReadOnlyList<QualityRejectionEntry>> GetByIdsAsync(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return Array.Empty<QualityRejectionEntry>();
        var list = ids.Distinct().ToList();
        return await _db.Entries.AsNoTracking().Where(e => list.Contains(e.Id)).ToListAsync();
    }
    public Task<bool> IsPhenomenonUsedAsync(int phenomenonId) => _db.Entries.AnyAsync(e => e.RejectionPhenomenonId == phenomenonId);
    public Task<bool> IsMachineUsedAsync(int machineId) => _db.Entries.AnyAsync(e => e.MachineId == machineId);
}

/// <summary>Resolves the logical store key ("GDC"/"CNC") to its database. Connection strings come from server configuration only.</summary>
public sealed class EntryStoreRouter : IEntryStoreRouter, IDisposable
{
    private readonly IReadOnlyDictionary<string, string> _connectionStrings;
    private readonly Dictionary<string, (EntryDbContext Db, EfEntryStore Store)> _open = new();
    public EntryStoreRouter(IReadOnlyDictionary<string, string> connectionStrings) => _connectionStrings = connectionStrings;

    public IEntryStore For(string key)
    {
        if (_open.TryGetValue(key, out var existing)) return existing.Store;
        if (!_connectionStrings.TryGetValue(key, out var cs) || string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"No connection string configured for entry store '{key}'.");
        var db = new EntryDbContext(new DbContextOptionsBuilder<EntryDbContext>().UseSqlServer(cs).Options);
        var store = new EfEntryStore(db);
        _open[key] = (db, store);
        return store;
    }

    public IReadOnlyList<IEntryStore> All() => _connectionStrings.Keys.Select(For).ToList();
    public IReadOnlyCollection<string> Keys => _connectionStrings.Keys.ToList();

    public async Task<bool> CanConnectAsync(string key, CancellationToken ct)
    {
        For(key);   // throws if not configured
        return await _open[key].Db.Database.CanConnectAsync(ct);
    }
    public void Dispose() { foreach (var (db, _) in _open.Values) db.Dispose(); _open.Clear(); }
}
