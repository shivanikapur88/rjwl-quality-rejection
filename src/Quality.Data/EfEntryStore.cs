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
