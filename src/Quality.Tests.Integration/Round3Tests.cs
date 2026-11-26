using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quality.Core.Dtos;

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
        var day = new DateOnly(2026, 1, 12);   // in the past: uploads for future days are refused
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

        // Partial coverage: delete m2's count -> its 4 rejected parts are outside the rate and coverage drops.
        await SqlFixture.Scalar<int>(F.MasterCs, $"DELETE FROM qa.ProductionCounts WHERE MachineId = {m2}; SELECT 1");
        var partial = (await sup.GetFromJsonAsync<ReportDto>($"/api/reports/totals?fromDate={day:yyyy-MM-dd}&toDate={day.AddDays(1):yyyy-MM-dd}&shopId={gdc.Id}"))!;
        Assert.Equal((1800, 8), (partial.Total.Rate!.Produced, partial.Total.Rate.RejectedWithProduction));
        Assert.Equal(Math.Round(8m / 12, 4), partial.Total.Rate.Coverage);

        // Only administrators upload.
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(rows))).StatusCode);
        Assert.Equal("rows_required", await Code(await admin.PostAsJsonAsync("/api/admin/production-counts", new ProductionCountUpload(Array.Empty<ProductionCountRow>()))));
    }
}
