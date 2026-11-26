using Quality.Core.Abstractions;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <summary>
/// Features #4 (app shift summary) and #6 (web dashboard): rejected-part totals by production day and shift, shop, phenomenon
/// and machine. Supervisors see their assigned shops, administrators all shops; inspectors have no access.
/// Reversed entries (approved corrections) are excluded; replacement entries are included.
/// These are COUNTS of rejected parts. No rejection percentage is computed: production counts are not available (decision #13).
/// </summary>
public sealed class ReportService
{
    public const int MaxDays = 92, DefaultTop = 10, MaxTop = 50;
    private readonly IEntryStoreRouter _router; private readonly ShopAccessGuard _guard; private readonly TimeProvider _time; private readonly IShiftCalendarProvider _calendar;

    public ReportService(IEntryStoreRouter router, ShopAccessGuard guard, TimeProvider time, IShiftCalendarProvider calendar)
        => (_router, _guard, _time, _calendar) = (router, guard, time, calendar);

    public async Task<CalendarDto> CalendarAsync()
    {
        var cal = await _calendar.GetAsync();
        var (day, shift) = cal.Resolve(_time.GetUtcNow().UtcDateTime);
        return new CalendarDto(day, shift, cal.Zone.Id, cal.Shifts.Select(s => new ShiftDto(s.ShiftCode, s.ShiftName, s.StartTime.ToString("HH:mm"))).ToList());
    }

    public async Task<ServiceResult<ReportDto>> RangeAsync(CurrentUser user, ReportQuery q)
    {
        static ServiceResult<ReportDto> Bad(string code, string msg) => ServiceResult<ReportDto>.Fail(ErrorKind.Validation, code, msg);
        if (!user.IsSupervisor && !user.IsAdministrator)
            return ServiceResult<ReportDto>.Fail(ErrorKind.Forbidden, "forbidden", "Reports are available to supervisors and administrators.");
        var cal = await _calendar.GetAsync();
        var today = cal.Resolve(_time.GetUtcNow().UtcDateTime).ProductionDate;
        var to = q.ToDate ?? today; var from = q.FromDate ?? to;
        if (from > to) return Bad("date_range_invalid", "The start date must not be after the end date.");
        if (to.DayNumber - from.DayNumber + 1 > MaxDays) return Bad("date_range_too_long", $"Choose at most {MaxDays} days.");
        var shift = string.IsNullOrWhiteSpace(q.ShiftCode) ? null : q.ShiftCode.Trim();
        if (shift is not null && !cal.IsKnownShift(shift)) return Bad("shift_invalid", "Unknown shift.");
        var top = Math.Clamp(q.Top ?? DefaultTop, 1, MaxTop);

        IReadOnlyList<Shop> shops;
        if (q.ShopId is int sid)
        {
            var (s, err) = await _guard.AuthorizeAsync(user, sid);
            if (err is not null) return ServiceResult<ReportDto>.Fail(err);
            shops = new[] { s! };
        }
        else shops = await _guard.AccessibleShopsAsync(user);

        var rows = new List<(Shop Shop, EntryAggregateRow Row)>();
        var unavailable = new List<string>();
        foreach (var s in shops)
        {
            try { rows.AddRange((await _router.For(s.EntryStoreKey).AggregateAsync(s.Id, from, to, shift)).Select(r => (s, r))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { unavailable.Add(s.ShopCode); }
        }

        var shopTotals = shops.Where(s => !unavailable.Contains(s.ShopCode))
            .Select(s => { var mine = rows.Where(r => r.Shop.Id == s.Id).ToList(); return new ShopTotalDto(s.Id, s.ShopCode, s.ShopName, mine.Sum(r => r.Row.Quantity), mine.Sum(r => r.Row.Entries)); })
            .ToList();
        var byDay = rows.GroupBy(r => (r.Row.ProductionDate, r.Row.ShiftCode))
            .Select(g => new DayShiftTotalDto(g.Key.ProductionDate, g.Key.ShiftCode, g.Sum(r => r.Row.Quantity), g.Sum(r => r.Row.Entries)))
            .OrderBy(d => d.ProductionDate).ThenBy(d => d.ShiftCode, StringComparer.Ordinal).ToList();
        var phen = rows.GroupBy(r => r.Row.PhenomenonId)
            .Select(g => new PhenomenonTotalDto(g.Key, g.First().Row.PhenomenonName, g.First().Shop.ShopCode, g.Sum(r => r.Row.Quantity), g.Sum(r => r.Row.Entries)))
            .OrderByDescending(p => p.Quantity).ThenBy(p => p.PhenomenonName, StringComparer.Ordinal).Take(top).ToList();
        var machines = rows.GroupBy(r => r.Row.MachineId)
            .Select(g => new MachineTotalDto(g.Key, g.First().Row.MachineCode, g.First().Row.MachineName, g.First().Shop.ShopCode, g.Sum(r => r.Row.Quantity), g.Sum(r => r.Row.Entries)))
            .OrderByDescending(m => m.Quantity).ThenBy(m => m.MachineCode, StringComparer.Ordinal).Take(top).ToList();
        var total = new ReportTotal(rows.Sum(r => r.Row.Quantity), rows.Sum(r => r.Row.Entries));
        return ServiceResult<ReportDto>.Ok(new ReportDto(from, to, shift, total, shopTotals, byDay, phen, machines, unavailable));
    }
}

/// <summary>Feature #8: machine QR labels carry "RJWLQR:M:&lt;machine id&gt;". Scanning resolves it to shop, stages and machine.</summary>
public sealed class MachineLookupService
{
    public const string Prefix = "RJWLQR:M:";
    private readonly IMasterData _master; private readonly ShopAccessGuard _guard;
    public MachineLookupService(IMasterData master, ShopAccessGuard guard) => (_master, _guard) = (master, guard);

    public static string LabelPayload(int machineId) => Prefix + machineId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static int? ParsePayload(string? payload)
    {
        if (payload is null) return null;
        payload = payload.Trim();
        if (!payload.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = payload[Prefix.Length..];
        var colon = rest.IndexOf(':');   // tolerate a human-readable suffix, e.g. "RJWLQR:M:12:GDC-02"
        if (colon >= 0) rest = rest[..colon];
        return int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public async Task<ServiceResult<MachineLookupDto>> LookupAsync(CurrentUser user, string? payload)
    {
        if (!user.IsInspector) return ServiceResult<MachineLookupDto>.Fail(ErrorKind.Forbidden, "forbidden", "Only quality inspectors record entries.");
        if (ParsePayload(payload) is not int id)
            return ServiceResult<MachineLookupDto>.Fail(ErrorKind.Validation, "qr_invalid", "This is not a Quality Rejection machine label.");
        var m = await _master.GetMachineAsync(id);
        if (m is null) return ServiceResult<MachineLookupDto>.Fail(ErrorKind.NotFound, "machine_not_found", "This machine is not in the system. Use the machine list.");
        var (shop, err) = await _guard.AuthorizeAsync(user, m.ShopId);
        if (err is not null) return ServiceResult<MachineLookupDto>.Fail(err);
        if (!m.IsActive) return ServiceResult<MachineLookupDto>.Fail(ErrorKind.Conflict, "machine_inactive", "This machine is not active. Ask your administrator.");
        var stages = await _master.GetStagesForShopAsync(shop!.Id);
        return ServiceResult<MachineLookupDto>.Ok(new MachineLookupDto(new ShopDto(shop.Id, shop.ShopCode, shop.ShopName),
            stages.Select(s => new StageDto(s.Id, s.StageCode, s.StageName)).ToList(), new MachineDto(m.Id, m.MachineCode, m.MachineName)));
    }
}
