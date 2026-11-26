using Microsoft.EntityFrameworkCore;
using Quality.Core.Services;
using Quality.Data;

namespace Quality.Api.Infrastructure;

/// <summary>
/// Reads qa.ShiftDefinitions (db/11) and the plant time zone (Plant:TimeZoneId, default Asia/Kolkata) and caches the calendar
/// for 5 minutes, so edited shift times take effect without a restart. If the table cannot be read, the last good calendar
/// (or the default A 06:00 / B 14:00 / C 22:00) is used and a warning is logged.
/// </summary>
public sealed class ShiftCalendarProvider : IShiftCalendarProvider
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopes; private readonly TimeProvider _time; private readonly ILogger<ShiftCalendarProvider> _log;
    private readonly TimeZoneInfo _zone;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ShiftCalendar? _cached; private DateTime _loadedAt;

    public ShiftCalendarProvider(IServiceScopeFactory scopes, IConfiguration cfg, TimeProvider time, ILogger<ShiftCalendarProvider> log)
    {
        (_scopes, _time, _log) = (scopes, time, log);
        _zone = TimeZoneInfo.FindSystemTimeZoneById(cfg["Plant:TimeZoneId"] is { Length: > 0 } z ? z : "Asia/Kolkata");
    }

    public async Task<ShiftCalendar> GetAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_cached is not null && now - _loadedAt < CacheFor) return _cached;
        await _gate.WaitAsync();
        try
        {
            if (_cached is not null && now - _loadedAt < CacheFor) return _cached;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
                var shifts = await db.ShiftDefinitions.AsNoTracking().ToListAsync();
                // An empty or unusable table (e.g. every shift inactive) throws or falls back; both are flagged for readiness.
                _cached = shifts.Count == 0 ? Fallback() : new ShiftCalendar(shifts, _zone);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not read qa.ShiftDefinitions; using the {Which} shift calendar.", _cached is null ? "default" : "last loaded");
                _cached ??= Fallback();   // reported by /api/health/ready
            }
            _loadedAt = now;
            return _cached;
        }
        finally { _gate.Release(); }
    }

    private ShiftCalendar Fallback() => new(ShiftCalendar.Default(_zone).Shifts, _zone) { IsFallback = true };
}
