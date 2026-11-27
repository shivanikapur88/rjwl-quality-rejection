using Quality.Core.Common;
using Quality.Core.Abstractions;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <summary>
/// Features #4 (app shift summary) and #6 (web dashboard): rejected-part totals by production day and shift, shop, phenomenon
/// and machine. Supervisors see their assigned shops, administrators all shops; inspectors have no access.
/// Reversed entries (approved corrections) are excluded; replacement entries are included.
/// Totals are COUNTS of rejected parts. A rejection rate (rejected ÷ produced) is added only where production counts exist
/// (qa.ProductionCounts, round 3), always with its coverage; the counting rules still need RJWL's sign-off (decision #13).
/// </summary>
public sealed class ReportService
{
    public const int MaxDays = 92, DefaultTop = 10, MaxTop = 50, MaxEntries = 200;
    /// <summary>Earliest report date: keeps the previous-period arithmetic (from - days) far from DateOnly.MinValue.</summary>
    public static readonly DateOnly MinDate = new(2000, 1, 1);
    private readonly IEntryStoreRouter _router; private readonly ShopAccessGuard _guard; private readonly TimeProvider _time;
    private readonly IShiftCalendarProvider _calendar; private readonly IMasterData _master;

    public ReportService(IEntryStoreRouter router, ShopAccessGuard guard, TimeProvider time, IShiftCalendarProvider calendar, IMasterData master)
        => (_router, _guard, _time, _calendar, _master) = (router, guard, time, calendar, master);

    public async Task<CalendarDto> CalendarAsync()
    {
        var cal = await _calendar.GetAsync();
        var (day, shift) = cal.Resolve(_time.GetUtcNow().UtcDateTime);
        return new CalendarDto(day, shift, cal.Zone.Id, cal.Shifts.Select(s => new ShiftDto(s.ShiftCode, s.ShiftName, s.StartTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))).ToList());
    }

    private sealed record Scope(IReadOnlyList<Shop> Shops, DateOnly From, DateOnly To, string? Shift, int Top);

    /// <summary>Validates the common report parameters and resolves the shops in the caller's scope.</summary>
    private async Task<(Scope? Scope, ServiceError? Error)> ScopeAsync(CurrentUser user, ReportQuery q)
    {
        static ServiceError Bad(string code, string msg) => new(ErrorKind.Validation, code, msg);
        if (!user.IsSupervisor && !user.IsAdministrator)
            return (null, new ServiceError(ErrorKind.Forbidden, "forbidden", "Reports are available to supervisors and administrators."));
        var cal = await _calendar.GetAsync();
        var today = cal.Resolve(_time.GetUtcNow().UtcDateTime).ProductionDate;
        var to = q.ToDate ?? today; var from = q.FromDate ?? to;
        if (from > to) return (null, Bad("date_range_invalid", "The start date must not be after the end date."));
        if (from < MinDate) return (null, Bad("date_range_invalid", $"Choose dates from {MinDate:yyyy-MM-dd} onwards."));
        if (to.DayNumber - from.DayNumber + 1 > MaxDays) return (null, Bad("date_range_too_long", $"Choose at most {MaxDays} days."));
        var shift = string.IsNullOrWhiteSpace(q.ShiftCode) ? null : q.ShiftCode.Trim();
        if (shift is not null && !cal.IsKnownShift(shift)) return (null, Bad("shift_invalid", "Unknown shift."));
        IReadOnlyList<Shop> shops;
        if (q.ShopId is int sid)
        {
            var (s, err) = await _guard.AuthorizeAsync(user, sid);
            if (err is not null) return (null, err);
            shops = new[] { s! };
        }
        else shops = await _guard.AccessibleShopsAsync(user);
        // Drill-down to a machine outside the caller's scope reveals nothing: it simply matches no rows.
        return (new Scope(shops, from, to, shift, Math.Clamp(q.Top ?? DefaultTop, 1, MaxTop)), null);
    }

    private async Task<(List<(Shop Shop, EntryAggregateRow Row)> Rows, List<string> Unavailable)> LoadAsync(
        IReadOnlyList<Shop> shops, DateOnly from, DateOnly to, string? shift, int? machineId, int? phenomenonId)
    {
        var rows = new List<(Shop, EntryAggregateRow)>(); var unavailable = new List<string>();
        foreach (var s in shops)
        {
            try
            {
                rows.AddRange((await _router.For(s.EntryStoreKey).AggregateAsync(s.Id, from, to, shift))
                    .Where(r => (machineId is null || r.MachineId == machineId) && (phenomenonId is null || r.PhenomenonId == phenomenonId))
                    .Select(r => (s, r)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { unavailable.Add(s.ShopCode); }
        }
        return (rows, unavailable);
    }

    public async Task<ServiceResult<ReportDto>> RangeAsync(CurrentUser user, ReportQuery q)
    {
        var (scope, err) = await ScopeAsync(user, q);
        if (err is not null) return ServiceResult<ReportDto>.Fail(err);
        var (shops, from, to, shift, top) = (scope!.Shops, scope.From, scope.To, scope.Shift, scope.Top);
        var (rows, unavailable) = await LoadAsync(shops, from, to, shift, q.MachineId, q.PhenomenonId);

        // ---- previous period of the same length (round 3) ----
        DateOnly? prevFrom = null, prevTo = null;
        Dictionary<int, int>? prevPhen = null, prevMachine = null; int? prevQty = null, prevEntries = null;
        if (q.Compare)
        {
            var days = to.DayNumber - from.DayNumber + 1;
            prevTo = from.AddDays(-1); prevFrom = from.AddDays(-days);
            var (prev, prevUnavailable) = await LoadAsync(shops, prevFrom.Value, prevTo.Value, shift, q.MachineId, q.PhenomenonId);
            // A shop that could not be read in EITHER period is left out of BOTH, so the comparison and every total cover
            // the same shops (otherwise one period would include it and the other not).
            unavailable = unavailable.Union(prevUnavailable).ToList();
            rows = rows.Where(r => !unavailable.Contains(r.Shop.ShopCode)).ToList();
            prev = prev.Where(r => !unavailable.Contains(r.Shop.ShopCode)).ToList();
            prevQty = prev.Sum(r => r.Row.Quantity); prevEntries = prev.Sum(r => r.Row.Entries);
            prevPhen = prev.GroupBy(r => r.Row.PhenomenonId).ToDictionary(g => g.Key, g => g.Sum(r => r.Row.Quantity));
            prevMachine = prev.GroupBy(r => r.Row.MachineId).ToDictionary(g => g.Key, g => g.Sum(r => r.Row.Quantity));
        }

        // ---- production counts for the rejection rate (round 3, #8) ----
        // With a phenomenon selected, only the shops where that phenomenon occurs (its stage's shop) put production into the
        // denominator: "Pin Deep" is a GDC phenomenon, so CNC/PRE/FINAL production must not dilute its rate (UI review F1).
        var rateShops = shops.Where(s => !unavailable.Contains(s.ShopCode)).Select(s => s.Id).ToList();
        if (q.PhenomenonId is not null) rateShops = rateShops.Where(id => rows.Any(r => r.Shop.Id == id)).ToList();
        var counts = await _master.GetProductionCountsAsync(rateShops, from, to, shift, q.MachineId);
        // Only machine/day/shift combinations that PRODUCED something put rejected parts into the rate: rejections logged on a
        // shift with 0 produced (stock, rework) would otherwise raise the rate with nothing added below the line. They are
        // left outside the rate and show up as lower coverage instead (second full review; rules still decision #13).
        var produced = counts.GroupBy(c => (c.MachineId, c.ProductionDate, c.ShiftCode)).Select(g => (g.Key, Qty: g.Sum(c => (long)c.ProducedQuantity)))
            .Where(x => x.Qty > 0).ToDictionary(x => x.Key, x => x.Qty);
        RateDto? Rate(IEnumerable<(Shop Shop, EntryAggregateRow Row)> rejected, Func<ProductionCount, bool> inScope)
        {
            var list = rejected.ToList();
            var prod = counts.Where(inScope).Sum(c => (long)c.ProducedQuantity);   // long: many max-size rows would overflow int
            var hasCounts = counts.Any(inScope);
            if (!hasCounts) return null;   // no production data at all for this scope: no rate, not a zero rate
            var withProd = list.Where(r => produced.ContainsKey((r.Row.MachineId, r.Row.ProductionDate, r.Row.ShiftCode))).Sum(r => r.Row.Quantity);
            var qty = list.Sum(r => r.Row.Quantity);
            return new RateDto(prod, withProd, prod > 0 ? Math.Round((decimal)withProd / prod, 6) : null, qty > 0 ? Math.Round((decimal)withProd / qty, 6) : null);
        }

        var shopTotals = shops.Where(s => !unavailable.Contains(s.ShopCode))
            .Select(s =>
            {
                var mine = rows.Where(r => r.Shop.Id == s.Id).ToList();
                return new ShopTotalDto(s.Id, s.ShopCode, s.ShopName, mine.Sum(r => r.Row.Quantity), mine.Sum(r => r.Row.Entries), Rate(mine, c => c.ShopId == s.Id));
            }).ToList();
        var byDay = rows.GroupBy(r => (r.Row.ProductionDate, r.Row.ShiftCode))
            .Select(g => new DayShiftTotalDto(g.Key.ProductionDate, g.Key.ShiftCode, g.Sum(r => r.Row.Quantity), g.Sum(r => r.Row.Entries),
                counts.Where(c => c.ProductionDate == g.Key.ProductionDate && c.ShiftCode == g.Key.ShiftCode) is var dc && dc.Any() ? dc.Sum(c => (long)c.ProducedQuantity) : null))
            .OrderBy(d => d.ProductionDate).ThenBy(d => d.ShiftCode, StringComparer.Ordinal).ToList();
        // Names: the CURRENT master names (deterministic; entry snapshots may differ after a rename), else the snapshot.
        var machineNames = (await _master.GetMachinesAsync(null, null, null)).ToDictionary(m => m.Id);
        var phenNames = (await _master.GetPhenomenaAsync(null, null, null)).ToDictionary(p => p.Id, p => p.PhenomenonName);
        var allPhen = rows.GroupBy(r => r.Row.PhenomenonId).ToList();
        var phen = allPhen
            .Select(g => new PhenomenonTotalDto(g.Key, phenNames.GetValueOrDefault(g.Key) ?? g.First().Row.PhenomenonName, g.First().Shop.ShopCode, g.Sum(r => r.Row.Quantity), g.Sum(r => r.Row.Entries),
                prevPhen is null ? null : prevPhen.GetValueOrDefault(g.Key)))
            .OrderByDescending(p => p.Quantity).ThenBy(p => p.PhenomenonName, StringComparer.Ordinal).Take(top).ToList();
        var machines = rows.GroupBy(r => r.Row.MachineId)
            .Select(g => new MachineTotalDto(g.Key, machineNames.GetValueOrDefault(g.Key)?.MachineCode ?? g.First().Row.MachineCode,
                machineNames.GetValueOrDefault(g.Key)?.MachineName ?? g.First().Row.MachineName, g.First().Shop.ShopCode, g.Sum(r => r.Row.Quantity), g.Sum(r => r.Row.Entries),
                prevMachine is null ? null : prevMachine.GetValueOrDefault(g.Key), Rate(g, c => c.MachineId == g.Key)))
            .OrderByDescending(m => m.Quantity).ThenBy(m => m.MachineCode, StringComparer.Ordinal).Take(top).ToList();
        var total = new ReportTotal(rows.Sum(r => r.Row.Quantity), rows.Sum(r => r.Row.Entries), prevQty, prevEntries, Rate(rows, _ => true));
        return ServiceResult<ReportDto>.Ok(new ReportDto(from, to, shift, total, shopTotals, byDay, phen, machines, unavailable,
            prevFrom, prevTo, allPhen.Count, q.MachineId, q.PhenomenonId));
    }

    /// <summary>Round 3 drill-down: the entries behind a report selection (same filters, reversed excluded), newest first.</summary>
    public async Task<ServiceResult<ReportEntriesDto>> EntriesAsync(CurrentUser user, ReportQuery q, int? limit)
    {
        var (scope, err) = await ScopeAsync(user, q);
        if (err is not null) return ServiceResult<ReportEntriesDto>.Fail(err);
        var max = Math.Clamp(limit ?? 100, 1, MaxEntries);
        var all = new List<QualityRejectionEntry>(); var unavailable = new List<string>();
        foreach (var s in scope!.Shops)
        {
            try { all.AddRange(await _router.For(s.EntryStoreKey).ListForReportAsync(s.Id, scope.From, scope.To, scope.Shift, q.MachineId, q.PhenomenonId, max + 1)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { unavailable.Add(s.ShopCode); }
        }
        var ordered = all.OrderByDescending(e => e.ServerCreatedAt).ThenByDescending(e => e.ShopId).ThenByDescending(e => e.Id).ToList();
        return ServiceResult<ReportEntriesDto>.Ok(new ReportEntriesDto(ordered.Take(max).Select(Mapping.ToDto).ToList(), ordered.Count > max, unavailable));
    }
}

/// <summary>
/// Round 3, #8: administrators upload production counts (parts produced per machine, production day and shift) so the
/// dashboard can show a rejection rate. All-or-nothing: every row is validated first; a dry run reports what would change.
/// </summary>
public sealed class ProductionCountService
{
    public const int MaxRows = 5000, MaxProduced = 1_000_000;
    private readonly IMasterData _master; private readonly TimeProvider _time; private readonly IShiftCalendarProvider _calendar;
    public ProductionCountService(IMasterData master, TimeProvider time, IShiftCalendarProvider calendar) => (_master, _time, _calendar) = (master, time, calendar);

    public async Task<ServiceResult<ProductionCountResult>> UploadAsync(CurrentUser user, ProductionCountUpload req)
    {
        if (!user.IsAdministrator) return ServiceResult<ProductionCountResult>.Fail(ErrorKind.Forbidden, "forbidden", "Administrator access is required.");
        if (req?.Rows is null || req.Rows.Count == 0) return ServiceResult<ProductionCountResult>.Fail(ErrorKind.Validation, "rows_required", "The file has no data rows.");
        if (req.Rows.Count > MaxRows) return ServiceResult<ProductionCountResult>.Fail(ErrorKind.Validation, "too_many_rows", $"Upload at most {MaxRows} rows at a time.");
        var cal = await _calendar.GetAsync();
        var today = cal.Resolve(_time.GetUtcNow().UtcDateTime).ProductionDate;
        var shops = (await _master.GetAllShopsAsync()).ToDictionary(s => s.ShopCode, StringComparer.OrdinalIgnoreCase);
        var machines = (await _master.GetMachinesAsync(null, null, null)).ToLookup(m => m.ShopId);

        var problems = new List<ProductionCountProblem>();
        var parsed = new List<(int Row, Shop Shop, Machine Machine, DateOnly Day, string Shift, int Qty)>();
        var seen = new HashSet<(int, DateOnly, string)>();
        for (var i = 0; i < req.Rows.Count; i++)
        {
            var r = req.Rows[i]; var n = i + 1;
            if (r is null) { problems.Add(new(n, "Empty row.")); continue; }
            if (!shops.TryGetValue((r.ShopCode ?? "").Trim(), out var shop)) { problems.Add(new(n, $"Unknown shop '{r.ShopCode}'.")); continue; }
            var m = machines[shop.Id].FirstOrDefault(x => string.Equals(x.MachineCode, (r.MachineCode ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (m is null) { problems.Add(new(n, $"Machine '{r.MachineCode}' is not a machine of {shop.ShopCode}.")); continue; }
            var shift = (r.ShiftCode ?? "").Trim();
            if (!cal.IsKnownShift(shift)) { problems.Add(new(n, $"Unknown shift '{r.ShiftCode}'.")); continue; }
            if (r.ProductionDate > today) { problems.Add(new(n, "Production date is in the future.")); continue; }
            if (r.ProductionDate < today.AddDays(-400)) { problems.Add(new(n, "Production date is more than 400 days ago.")); continue; }
            if (r.ProducedQuantity < 0 || r.ProducedQuantity > MaxProduced) { problems.Add(new(n, $"Produced quantity must be 0 to {MaxProduced}.")); continue; }
            if (!seen.Add((m.Id, r.ProductionDate, shift))) { problems.Add(new(n, "The same machine, day and shift appears more than once in the file.")); continue; }
            parsed.Add((n, shop, m, r.ProductionDate, shift, r.ProducedQuantity));
        }
        if (problems.Count > 0) return ServiceResult<ProductionCountResult>.Ok(new ProductionCountResult(false, req.Rows.Count, 0, 0, 0, problems));

        int inserted = 0, updated = 0, unchanged = 0;
        var source = string.IsNullOrWhiteSpace(req.Source) ? "upload" : req.Source.Trim()[..Math.Min(50, req.Source.Trim().Length)];
        await _master.InTransactionAsync(async () =>
        {
            var existing = (await _master.GetProductionCountsForUpdateAsync(parsed.Select(p => p.Machine.Id).Distinct().ToList(), parsed.Min(p => p.Day), parsed.Max(p => p.Day)))
                .ToDictionary(c => (c.MachineId, c.ProductionDate, c.ShiftCode));
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var p in parsed)
            {
                if (existing.TryGetValue((p.Machine.Id, p.Day, p.Shift), out var c))
                {
                    if (c.ProducedQuantity == p.Qty) { unchanged++; continue; }
                    updated++;
                    if (!req.DryRun) { c.ProducedQuantity = p.Qty; c.Source = source; c.UpdatedAt = now; c.UpdatedBy = user.Id; }
                }
                else
                {
                    inserted++;
                    if (!req.DryRun) _master.AddProductionCount(new ProductionCount { ShopId = p.Shop.Id, MachineId = p.Machine.Id, ProductionDate = p.Day, ShiftCode = p.Shift, ProducedQuantity = p.Qty, Source = source, UpdatedAt = now, UpdatedBy = user.Id });
                }
            }
            if (req.DryRun) return;
            _master.AddAudit(new AuditLogEntry
            {
                UserId = user.Id, Action = AuditActions.ProductionCountsImported, EntityType = "ProductionCounts", Timestamp = now,
                NewValue = System.Text.Json.JsonSerializer.Serialize(new { Rows = parsed.Count, Inserted = inserted, Updated = updated, Unchanged = unchanged, From = parsed.Min(p => p.Day), To = parsed.Max(p => p.Day), Source = source }),
            });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<ProductionCountResult>.Ok(new ProductionCountResult(!req.DryRun, parsed.Count, inserted, updated, unchanged, Array.Empty<ProductionCountProblem>()));
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
