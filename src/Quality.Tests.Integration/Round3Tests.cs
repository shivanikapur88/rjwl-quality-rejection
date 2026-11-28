using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Quality.Core.Abstractions;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Tests.Integration;

/// <summary>Round 3: dashboard drill-down, previous-period comparison, entries behind a selection, production counts and rejection rate.</summary>
public sealed class Round3Tests(SqlFixture f) : SqlTest(f)
{
    private static async Task<string?> Code(HttpResponseMessage r)
    {
        try { return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Two machines of GDC in a far-future window so only this test's rows count. Returns their ids and the supervisor client.</summary>
    private async Task<(int M1, int M2, HttpClient Sup, HttpClient Admin, ShopDto Shop)> SeedAsync(string tag, DateOnly day)
    {
        var shops = await F.ShopsAsync(); var gdc = shops["GDC"];
        var admin = F.Client(await F.AdminTokenAsync());
        async Task<int> Machine(string code) => (await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(gdc.Id, code, code + " name", true))).Content.ReadFromJsonAsync<AdminMachineDto>())!.Id;
        var m1 = await Machine($"R3{tag}-1"); var m2 = await Machine($"R3{tag}-2");
        async Task Insert(DateOnly d, string shift, int machine, int phen, string phenName, int qty, bool reversed = false) =>
            await SqlFixture.Scalar<int>(F.GdcCs, $"""
                INSERT INTO qa.QualityRejectionEntries (SubmissionId, ShopId, ShopCode, InspectionStageId, StageCode, StageName, MachineId, MachineCode, MachineName,
                  RejectionPhenomenonId, PhenomenonNameSnapshot, Quantity, InspectorId, InspectorEmployeeCode, ServerCreatedAt, ProductionDate, ShiftCode, ReversedAt, ReversedByCorrectionId)
                VALUES (NEWID(), {gdc.Id}, 'GDC', 1, 'GDC_SHOP', 'GDC Shop', {machine}, 'R3{tag}-{(machine == m1 ? 1 : 2)}', 'n', {phen}, '{phenName}', {qty}, 1, 'E', SYSUTCDATETIME(), '{d:yyyy-MM-dd}', '{shift}',
                        {(reversed ? "SYSUTCDATETIME(), 1" : "NULL, NULL")}); SELECT 1
                """);
        // current period: day and day+1
        await Insert(day, "A", m1, 7001, "Cold Shot", 6);
        await Insert(day, "B", m1, 7002, "Pin Deep", 2);
        await Insert(day.AddDays(1), "A", m2, 7001, "Cold Shot", 4);
        await Insert(day.AddDays(1), "A", m2, 7001, "Cold Shot", 40, reversed: true);   // never counted
        // previous period: day-2 and day-1
        await Insert(day.AddDays(-2), "A", m1, 7001, "Cold Shot", 3);
        await Insert(day.AddDays(-1), "C", m2, 7002, "Pin Deep", 1);
        var (_, su, spw) = await F.CreateUserAsync("Supervisor", "GDC");
        return (m1, m2, F.Client(await F.LoginAsync(su, spw)), admin, gdc);
    }

    [Fact]
    public async Task Comparison_and_drill_down_by_machine_and_phenomenon()
    {
        var day = new DateOnly(2032, 6, 10);
        var (m1, m2, sup, _, gdc) = await SeedAsync("a", day);
        var range = $"fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&shopId={gdc.Id}";

        var all = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?{range}&compare=true"))!;
        Assert.Equal((12, 3), (all.Total.Quantity, all.Total.Entries));
        Assert.Equal((4, 2), (all.Total.PreviousQuantity!.Value, all.Total.PreviousEntries!.Value));
        Assert.Equal((day.AddDays(-2), day.AddDays(-1)), (all.PreviousFromDate!.Value, all.PreviousToDate!.Value));
        Assert.Equal(2, all.PhenomenaCount);
        Assert.Equal((10, 3), (all.TopPhenomena[0].Quantity, all.TopPhenomena[0].PreviousQuantity!.Value));   // Cold Shot
        Assert.Null(all.Total.Rate);   // no production counts at all for this scope: no rate (not 0%)

        var byMachine = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?{range}&machineId={m1}&compare=true"))!;
        Assert.Equal((8, 3, m1), (byMachine.Total.Quantity, byMachine.Total.PreviousQuantity!.Value, byMachine.MachineId!.Value));
        Assert.Single(byMachine.TopMachines);
        var byPhen = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?{range}&phenomenonId=7001"))!;
        Assert.Equal(10, byPhen.Total.Quantity);

        var entries = (await sup.GetFromJsonAsync<ReportEntriesDto>($"/api/reports/entries?{range}&machineId={m2}"))!;
        var e = Assert.Single(entries.Items);   // the reversed row is excluded
        Assert.Equal((4, "Cold Shot"), (e.Quantity, e.PhenomenonName));
        Assert.False(entries.HasMore);
        var limited = (await sup.GetFromJsonAsync<ReportEntriesDto>($"/api/reports/entries?{range}&limit=2"))!;
        Assert.Equal((2, true), (limited.Items.Count, limited.HasMore));

        // Scope: a supervisor of another shop gets nothing for these machines; inspectors get 403.
        var (_, pu, ppw) = await F.CreateUserAsync("Supervisor", "PRE");
        var pre = F.Client(await F.LoginAsync(pu, ppw));
        Assert.Equal(0, (await pre.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&machineId={m1}"))!.Total.Quantity);
        Assert.Empty((await pre.GetFromJsonAsync<ReportEntriesDto>($"/api/reports/entries?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&machineId={m1}"))!.Items);
        var (_, iu, ipw) = await F.CreateUserAsync("QualityInspector", "GDC");
        Assert.Equal(HttpStatusCode.Forbidden, (await F.Client(await F.LoginAsync(iu, ipw)).GetAsync($"/api/reports/entries?{range}")).StatusCode);
    }

    [Fact]
    public async Task Production_counts_upload_is_validated_all_or_nothing_and_drives_the_rate()
    {
        // In the past (future days are refused) but within the 400-day window, relative to today so the test does not expire.
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        var (m1, m2, sup, admin, gdc) = await SeedAsync("b", day);
        string Code1(int m) => m == m1 ? "R3b-1" : "R3b-2";
        ProductionCountRow Row(int m, DateOnly d, string s, int q) => new("GDC", Code1(m), d, s, q);

        // Invalid file: nothing saved, every problem listed by row.
        var bad = await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(new[]
        {
            Row(m1, day, "A", 1000), new ProductionCountRow("XYZ", "R3b-1", day, "A", 5), new ProductionCountRow("GDC", "NOPE", day, "A", 5),
            Row(m1, day, "Z", 5), Row(m1, day, "A", 7), new ProductionCountRow("GDC", "R3b-1", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), "A", 5),
        }, DryRun: false));
        var badRes = (await bad.Content.ReadFromJsonAsync<ProductionCountResult>())!;
        Assert.False(badRes.Applied);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, badRes.Problems.Select(p => p.Row));
        Assert.Equal(0, await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT COUNT(*) FROM qa.ProductionCounts WHERE MachineId IN ({m1},{m2})"));

        // Dry run, then apply.
        var rows = new[] { Row(m1, day, "A", 1000), Row(m1, day, "B", 500), Row(m2, day.AddDays(1), "A", 400) };
        var dry = (await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(rows))).Content.ReadFromJsonAsync<ProductionCountResult>())!;
        Assert.Equal((false, 3, 3), (dry.Applied, dry.Rows, dry.Inserted));
        Assert.Equal(0, await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT COUNT(*) FROM qa.ProductionCounts WHERE MachineId IN ({m1},{m2})"));
        var applied = (await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(rows, DryRun: false))).Content.ReadFromJsonAsync<ProductionCountResult>())!;
        Assert.Equal((true, 3), (applied.Applied, applied.Inserted));
        // Re-upload with one change: upsert, not duplicate.
        var again = (await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(new[] { Row(m1, day, "A", 1000), Row(m1, day, "B", 800) }, DryRun: false))).Content.ReadFromJsonAsync<ProductionCountResult>())!;
        Assert.Equal((0, 1, 1), (again.Inserted, again.Updated, again.Unchanged));
        Assert.Equal("PRODUCTION_COUNTS_IMPORTED", await SqlFixture.Scalar<string>(F.MasterCs, "SELECT TOP 1 Action FROM qa.AuditLog WHERE EntityType = 'ProductionCounts' ORDER BY Id DESC"));

        // Rate: rejected 6 (m1 A) + 2 (m1 B) + 4 (m2 A next day) = 12, all with counts; produced 1000 + 800 + 400 = 2200.
        var r = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&shopId={gdc.Id}"))!;
        Assert.Equal((2200, 12), (r.Total.Rate!.Produced, r.Total.Rate.RejectedWithProduction));
        Assert.Equal(Math.Round(12m / 2200, 6), r.Total.Rate.Rate);
        Assert.Equal(1m, r.Total.Rate.Coverage);
        var m1Rate = r.TopMachines.Single(m => m.MachineId == m1).Rate!;
        Assert.Equal((1800, 8), (m1Rate.Produced, m1Rate.RejectedWithProduction));
        // Shift filter: only shift B counts on both sides.
        var b = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&shopId={gdc.Id}&shiftCode=B"))!;
        Assert.Equal((800, 2), (b.Total.Rate!.Produced, b.Total.Rate.RejectedWithProduction));

        // Partial coverage: m2 produced 0 on that shift -> its 4 rejected parts are outside the rate (they would otherwise
        // raise it with nothing added below the line) and coverage drops.
        Assert.True((await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(new[] { Row(m2, day.AddDays(1), "A", 0) }, DryRun: false))).Content.ReadFromJsonAsync<ProductionCountResult>())!.Applied);
        var partial = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&shopId={gdc.Id}"))!;
        Assert.Equal((1800, 8), (partial.Total.Rate!.Produced, partial.Total.Rate.RejectedWithProduction));
        Assert.Equal(Math.Round(8m / 12, 6), partial.Total.Rate.Coverage);

        // UI review F1: with a phenomenon selected, only the shops where it occurs count production. A CNC machine's
        // production on the same day must not dilute the rate of a GDC phenomenon (7001 occurs only on GDC here).
        var shopsAll = await F.ShopsAsync();
        var cnc = (await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(shopsAll["CNC"].Id, "R3b-CNC", "R3b-CNC name", true))).Content.ReadFromJsonAsync<AdminMachineDto>())!;
        Assert.True((await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(new[] { new ProductionCountRow("CNC", "R3b-CNC", day, "A", 50_000) }, DryRun: false))).Content.ReadFromJsonAsync<ProductionCountResult>())!.Applied);
        var allShops = F.Client(await F.AdminTokenAsync());
        // A real GDC phenomenon (its stage is mapped to GDC only) with no rejections in this window: the rate uses GDC's
        // production only, and with 0 rejections it is 0% - not "no production data" (fourth review S2).
        var gdcPhen = await SqlFixture.Scalar<int>(F.MasterCs, "SELECT TOP 1 p.Id FROM qa.RejectionPhenomena p JOIN qa.InspectionStages s ON s.Id = p.InspectionStageId WHERE s.StageCode = 'GDC_SHOP' ORDER BY p.Id");
        var byPhen = (await allShops.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&phenomenonId={gdcPhen}"))!;
        Assert.NotNull(byPhen.Total.Rate);
        Assert.Equal(1800, byPhen.Total.Rate!.Produced);                    // GDC only (m1 1000 + 800, m2 0), not + 50,000 CNC
        Assert.Equal(0m, byPhen.Total.Rate.Rate);
        await SqlFixture.Scalar<int>(F.MasterCs, $"DELETE FROM qa.ProductionCounts WHERE MachineId = {cnc.Id}; SELECT 1");

        // Only administrators upload.
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(rows))).StatusCode);
        Assert.Equal("rows_required", await Code(await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(Array.Empty<ProductionCountRow>()))));
    }

    [Fact]
    public async Task A_shop_unreadable_in_one_period_is_left_out_of_both_periods()
    {
        var day = new DateOnly(2032, 7, 20);
        var (_, _, _, _, gdc) = await SeedAsync("c", day);
        // The GDC database fails only for the PREVIOUS period (reads that start before 'day').
        await using var factory = F.CreateFactory(new Dictionary<string, string?>()).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Last(d => d.ServiceType == typeof(IEntryStoreRouter));
            services.Remove(original);
            services.AddScoped<IEntryStoreRouter>(sp => new FailingBeforeRouter((IEntryStoreRouter)original.ImplementationFactory!(sp), "GDC", day));
        }));
        var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        http.DefaultRequestHeaders.Authorization = new("Bearer", await F.AdminTokenAsync());
        var r = (await http.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&compare=true"))!;
        Assert.Contains("GDC", r.UnavailableShops);
        // Before the fix GDC's current rows (10 parts) stayed in the totals while the previous period lacked GDC.
        Assert.DoesNotContain(r.TopMachines, m => m.ShopCode == "GDC");
        Assert.DoesNotContain(r.Shops, s => s.ShopId == gdc.Id);
        Assert.Equal(r.ByDayShift.Sum(d => d.Quantity), r.Total.Quantity);
        Assert.All(r.TopMachines, m => Assert.NotEqual("GDC", m.ShopCode));
    }

    [Fact]
    public async Task Report_and_upload_limits_are_enforced()
    {
        var admin = F.Client(await F.AdminTokenAsync());
        // Dates before 2000-01-01 are refused (the previous-period arithmetic used to throw a 500 near DateOnly.MinValue).
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/reports/totals?fromDate=0001-01-01&toDate=0001-01-02&compare=true")).StatusCode);
        // Entries for a shop outside the supervisor's scope: 403, not an empty list.
        var shops = await F.ShopsAsync();
        var (_, su, spw) = await F.CreateUserAsync("Supervisor", "PRE");
        var sup = F.Client(await F.LoginAsync(su, spw));
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.GetAsync($"/api/reports/entries?shopId={shops["GDC"].Id}")).StatusCode);
        // Upload limits: more than 5000 rows, and a day more than 400 days ago.
        var many = Enumerable.Range(0, Quality.Core.Services.ProductionCountService.MaxRows + 1).Select(i => new ProductionCountRow("GDC", "X", new DateOnly(2026, 1, 1), "A", 1)).ToArray();
        Assert.Equal("too_many_rows", await Code(await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(many))));
        var old = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-401);
        var gdcMachine = (await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(shops["GDC"].Id, "R3L-1", "R3L-1 name", true))).Content.ReadFromJsonAsync<AdminMachineDto>())!;
        var res = (await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(new[] { new ProductionCountRow("GDC", gdcMachine.MachineCode, old, "A", 1) }))).Content.ReadFromJsonAsync<ProductionCountResult>())!;
        Assert.Contains("400 days", Assert.Single(res.Problems).Problem);
        // A machine with production counts (but no entries) cannot be deleted: a clear 409, not a foreign-key error.
        var recent = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);
        var saved = (await (await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(new[] { new ProductionCountRow("GDC", gdcMachine.MachineCode, recent, "A", 10) }, DryRun: false))).Content.ReadFromJsonAsync<ProductionCountResult>())!;
        Assert.True(saved.Applied);
        var del = await admin.DeleteAsync($"/api/admin/machines/{gdcMachine.Id}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Equal("machine_in_use", await Code(del));
    }

    /// <summary>Wraps the real router; the named store's report reads that start before <c>cutoff</c> fail like an unreachable database.</summary>
    private sealed class FailingBeforeRouter(IEntryStoreRouter inner, string key, DateOnly cutoff) : IEntryStoreRouter
    {
        public IEntryStore For(string entryStoreKey) => entryStoreKey == key ? new Store(inner.For(entryStoreKey), cutoff) : inner.For(entryStoreKey);
        public IReadOnlyList<IEntryStore> All() => inner.All();
        public IReadOnlyCollection<string> Keys => inner.Keys;
        public Task<bool> CanConnectAsync(string k, CancellationToken ct) => inner.CanConnectAsync(k, ct);

        private sealed class Store(IEntryStore s, DateOnly cutoff) : IEntryStore
        {
            public Task<QualityRejectionEntry?> FindBySubmissionIdAsync(Guid id) => s.FindBySubmissionIdAsync(id);
            public Task<QualityRejectionEntry> InsertAsync(QualityRejectionEntry e) => s.InsertAsync(e);
            public Task<QualityRejectionEntry?> GetByIdAsync(long id) => s.GetByIdAsync(id);
            public Task<IReadOnlyList<QualityRejectionEntry>> GetByIdsAsync(IReadOnlyCollection<long> ids) => s.GetByIdsAsync(ids);
            public Task<IReadOnlyList<QualityRejectionEntry>> ListAsync(EntryFilter f) => s.ListAsync(f);
            public Task<bool> IsPhenomenonUsedAsync(int id) => s.IsPhenomenonUsedAsync(id);
            public Task<bool> IsMachineUsedAsync(int id) => s.IsMachineUsedAsync(id);
            public Task<int> CountWithoutProductionDateAsync(CancellationToken ct) => s.CountWithoutProductionDateAsync(ct);
            public Task<IReadOnlyList<EntryAggregateRow>> AggregateAsync(int shopId, DateOnly from, DateOnly to, string? shift) =>
                from < cutoff ? throw new InvalidOperationException("simulated unreachable database") : s.AggregateAsync(shopId, from, to, shift);
            public Task<QualityRejectionEntry?> ApplyCorrectionAsync(int shopId, long entryId, long correctionId, DateTime nowUtc, QualityRejectionEntry? r) =>
                s.ApplyCorrectionAsync(shopId, entryId, correctionId, nowUtc, r);
            public Task<IReadOnlyList<QualityRejectionEntry>> ListForReportAsync(int shopId, DateOnly from, DateOnly to, string? shift, int? m, int? p, int limit) =>
                s.ListForReportAsync(shopId, from, to, shift, m, p, limit);
        }
    }
}
