using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <summary>
/// Maps a UTC instant to the plant's production day and shift (feature #2).
/// Shifts are given in plant local time; each runs from its start until the next shift's start (wrapping past midnight).
/// The production day starts at the start of the FIRST shift (lowest SortOrder), so with A 06:00 / B 14:00 / C 22:00
/// an entry at 02:00 on 27 Nov belongs to shift C of production day 26 Nov.
/// </summary>
public sealed class ShiftCalendar
{
    private readonly TimeZoneInfo _zone;
    private readonly (TimeOnly Start, string Code)[] _byStart;
    public TimeOnly DayStart { get; }
    public IReadOnlyList<ShiftDefinition> Shifts { get; }
    public TimeZoneInfo Zone => _zone;

    /// <summary>Every shift code ever defined (active or not): old entries keep the code they were saved with.</summary>
    public IReadOnlySet<string> AllCodes { get; }
    /// <summary>True when built from the default because qa.ShiftDefinitions could not be read or was unusable.</summary>
    public bool IsFallback { get; init; }

    public ShiftCalendar(IEnumerable<ShiftDefinition> shifts, TimeZoneInfo zone)
    {
        var all = shifts.ToList();
        AllCodes = all.Select(s => s.ShiftCode).ToHashSet(StringComparer.Ordinal);
        Shifts = all.Where(s => s.IsActive).OrderBy(s => s.SortOrder).ToList();
        if (Shifts.Count == 0) throw new InvalidOperationException("No active shifts are defined (qa.ShiftDefinitions).");
        if (Shifts.Select(s => s.StartTime).Distinct().Count() != Shifts.Count) throw new InvalidOperationException("Two shifts start at the same time.");
        _zone = zone;
        DayStart = Shifts[0].StartTime;
        _byStart = Shifts.Select(s => (s.StartTime, s.ShiftCode)).OrderBy(x => x.StartTime).ToArray();
    }

    /// <summary>The default calendar: A 06:00, B 14:00, C 22:00 (db/11 seed).</summary>
    public static ShiftCalendar Default(TimeZoneInfo zone) => new(new[]
    {
        new ShiftDefinition { ShiftCode = "A", ShiftName = "Shift A", StartTime = new(6, 0), SortOrder = 1 },
        new ShiftDefinition { ShiftCode = "B", ShiftName = "Shift B", StartTime = new(14, 0), SortOrder = 2 },
        new ShiftDefinition { ShiftCode = "C", ShiftName = "Shift C", StartTime = new(22, 0), SortOrder = 3 },
    }, zone);

    public (DateOnly ProductionDate, string ShiftCode) Resolve(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);
        var timeOfDay = TimeOnly.FromDateTime(local);
        var date = DateOnly.FromDateTime(local);
        var productionDate = timeOfDay < DayStart ? date.AddDays(-1) : date;

        // The shift with the latest start <= time of day; before the earliest start it is the last shift of the previous evening.
        var code = _byStart[^1].Code;
        foreach (var (start, c) in _byStart) { if (start <= timeOfDay) code = c; else break; }
        return (productionDate, code);
    }

    public bool IsKnownShift(string? code) => code is not null && AllCodes.Contains(code);
}

/// <summary>Supplies the current calendar (shift definitions are read from the master database and cached).</summary>
public interface IShiftCalendarProvider
{
    Task<ShiftCalendar> GetAsync();
    /// <summary>The last attempt to read the shift table failed (the calendar in use may be an older copy or the default).</summary>
    bool LastLoadFailed => false;
}
