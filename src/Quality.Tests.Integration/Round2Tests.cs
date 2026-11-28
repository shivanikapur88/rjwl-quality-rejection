using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Services;

namespace Quality.Tests.Integration;

/// <summary>Features #1 corrections, #2 shifts, #4/#6 reports, #8 QR lookup, dashboard hosting - against SQL Server.</summary>
public sealed class Round2Tests(SqlFixture f) : SqlTest(f)
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        try { return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private sealed record Ctx(HttpClient Inspector, int InspectorId, HttpClient Supervisor, ShopDto Shop, StageDto Stage, List<MachineDto> Machines, List<PhenomenonDto> Phenomena);

    private async Task<Ctx> SetupAsync(string shop)
    {
        var shops = await F.ShopsAsync();
        var (iid, iu, ipw) = await F.CreateUserAsync("QualityInspector", shop);
        var (_, su, spw) = await F.CreateUserAsync("Supervisor", shop);
        var insp = F.Client(await F.LoginAsync(iu, ipw));
        var sup = F.Client(await F.LoginAsync(su, spw));
        var s = shops[shop];
        var stage = (await insp.GetFromJsonAsync<List<StageDto>>($"/api/shops/{s.Id}/stages"))!.Single();
        var machines = (await insp.GetFromJsonAsync<List<MachineDto>>($"/api/shops/{s.Id}/stages/{stage.Id}/machines"))!;
        var phen = (await insp.GetFromJsonAsync<List<PhenomenonDto>>($"/api/shops/{s.Id}/stages/{stage.Id}/phenomena"))!;
        return new Ctx(insp, iid, sup, s, stage, machines, phen);
    }

    private static async Task<EntryDto> PostEntry(Ctx c, int qty = 4, int machine = 0, int phen = 0)
    {
        var r = await c.Inspector.PostAsJsonAsync("/api/rejection-entries", new CreateEntryRequest(Guid.NewGuid(), c.Shop.Id, c.Stage.Id, c.Machines[machine].Id, c.Phenomena[phen].Id, qty, "orig"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<CreateEntryResult>())!.Entry;
    }

    // ---------------- #2 shifts ----------------
    [Fact]
    public async Task New_entries_carry_the_plant_production_date_and_shift()
    {
        var c = await SetupAsync("GDC");
        var e = await PostEntry(c);
        var expected = ShiftCalendar.Default(Ist).Resolve(e.ServerCreatedAtUtc);
        Assert.Equal(expected.ProductionDate, e.ProductionDate);
        Assert.Equal(expected.ShiftCode, e.ShiftCode);
        var cal = await c.Inspector.GetFromJsonAsync<CalendarDto>("/api/reports/calendar");
        Assert.Equal(new[] { "A|06:00", "B|14:00", "C|22:00" }, cal!.Shifts.Select(s => $"{s.ShiftCode}|{s.StartTime}"));
    }

    [Fact]
    public async Task Script_12_backfills_old_rows_with_the_night_shift_on_the_previous_production_day()
    {
        // 2026-11-26 20:30 UTC = 27 Nov 02:00 IST -> shift C of production day 26 Nov.  2026-11-27 01:00 UTC = 06:30 IST -> A, 27 Nov.
        // 2026-11-27 00:29:59.700 UTC = 05:59:59.700 IST -> still shift C of 26 Nov (a cast to time(0) used to round it up to 06:00 = A).
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid(); var id3 = Guid.NewGuid();
        foreach (var (id, t) in new[] { (id1, new DateTime(2026, 11, 26, 20, 30, 0, DateTimeKind.Utc)), (id2, new DateTime(2026, 11, 27, 1, 0, 0, DateTimeKind.Utc)),
                                        (id3, new DateTime(2026, 11, 27, 0, 29, 59, 700, DateTimeKind.Utc)) })
            await SqlFixture.Scalar<int>(F.GdcCs, """
                INSERT INTO qa.QualityRejectionEntries (SubmissionId, ShopId, ShopCode, InspectionStageId, StageCode, StageName, MachineId, MachineCode, MachineName,
                  RejectionPhenomenonId, PhenomenonNameSnapshot, Quantity, InspectorId, InspectorEmployeeCode, ServerCreatedAt)
                VALUES (@s, 1, 'GDC', 1, 'GDC_SHOP', 'GDC Shop', 1, 'M', 'M', 1, 'P', 1, 1, 'E', @t); SELECT 1
                """, ("@s", id), ("@t", t));
        await SqlFixture.RunScripts(F.GdcCs, "12_upgrade_entries_shift_reversal");
        var rows = await SqlFixture.Rows(F.GdcCs, $"SELECT CONVERT(varchar(10), ProductionDate, 23) + '|' + ShiftCode FROM qa.QualityRejectionEntries WHERE SubmissionId IN ('{id1}','{id2}','{id3}') ORDER BY ServerCreatedAt");
        Assert.Equal(new[] { "2026-11-26|C", "2026-11-26|C", "2026-11-27|A" }, rows.Select(r => (string)r[0]));
        Assert.Equal((new DateOnly(2026, 11, 26), "C"), ShiftCalendar.Default(Ist).Resolve(new DateTime(2026, 11, 27, 0, 29, 59, 700, DateTimeKind.Utc)));
        // The SQL backfill and the API's calendar agree.
        Assert.Equal((new DateOnly(2026, 11, 26), "C"), ShiftCalendar.Default(Ist).Resolve(new DateTime(2026, 11, 26, 20, 30, 0, DateTimeKind.Utc)));
    }

    // ---------------- #1 corrections ----------------
    [Fact]
    public async Task Approved_correction_reverses_the_entry_and_adds_a_linked_replacement()
    {
        var c = await SetupAsync("CNC");
        var e = await PostEntry(c, qty: 4);
        var req = await c.Inspector.PostAsJsonAsync($"/api/rejection-entries/{c.Shop.Id}/{e.Id}/corrections",
            new CorrectionRequest("Typed 4, was 2", PhenomenonId: c.Phenomena[1].Id, Quantity: 2));
        Assert.Equal(HttpStatusCode.Created, req.StatusCode);
        var corr = (await req.Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.Equal("Pending", corr.Status);
        Assert.False(corr.IsReverseOnly);

        var pending = await c.Supervisor.GetFromJsonAsync<List<CorrectionDto>>($"/api/corrections?shopId={c.Shop.Id}");
        Assert.Contains(pending!, x => x.Id == corr.Id && x.Entry!.Id == e.Id);
        Assert.Equal("Pending", (await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{e.Id}"))!.CorrectionStatus);

        var ok = await c.Supervisor.PostAsJsonAsync($"/api/corrections/{corr.Id}/approve", new CorrectionDecisionRequest("checked at the bench"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var done = (await ok.Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.Equal("Approved", done.Status);
        Assert.NotNull(done.ReplacementEntryId);

        var original = (await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{e.Id}"))!;
        var replacement = (await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{done.ReplacementEntryId}"))!;
        Assert.True(original.IsReversed);
        Assert.Equal(4, original.Quantity);   // the original row is not edited
        Assert.Equal((e.Id, 2, c.Phenomena[1].Id, "orig", e.ProductionDate, e.ShiftCode, c.InspectorId),
            (replacement.CorrectsEntryId!.Value, replacement.Quantity, replacement.PhenomenonId, replacement.Remarks, replacement.ProductionDate, replacement.ShiftCode, replacement.InspectorId));
        // Audit: requested + approved.
        var actions = await SqlFixture.Rows(F.MasterCs, $"SELECT Action FROM qa.AuditLog WHERE EntityType = 'EntryCorrection' AND EntityId = '{corr.Id}' ORDER BY Id");
        Assert.Equal(new[] { "CORRECTION_REQUESTED", "CORRECTION_APPROVED" }, actions.Select(r => (string)r[0]));
    }

    [Fact]
    public async Task Reverse_only_reject_withdraw_and_rules()
    {
        var c = await SetupAsync("PRE");
        var e1 = await PostEntry(c); var e2 = await PostEntry(c); var e3 = await PostEntry(c);
        string url(EntryDto e) => $"/api/rejection-entries/{c.Shop.Id}/{e.Id}/corrections";

        // Reverse only.
        var r1 = (await (await c.Inspector.PostAsJsonAsync(url(e1), new CorrectionRequest("Entered twice"))).Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.True(r1.IsReverseOnly);
        Assert.Equal("correction_pending", await Code(await c.Inspector.PostAsJsonAsync(url(e1), new CorrectionRequest("again"))));
        var a1 = (await (await c.Supervisor.PostAsJsonAsync($"/api/corrections/{r1.Id}/approve", new CorrectionDecisionRequest(null))).Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.Null(a1.ReplacementEntryId);
        Assert.Equal("entry_reversed", await Code(await c.Inspector.PostAsJsonAsync(url(e1), new CorrectionRequest("once more"))));

        // Reject needs a note and leaves the entry alone.
        var r2 = (await (await c.Inspector.PostAsJsonAsync(url(e2), new CorrectionRequest("wrong machine?", Quantity: 9))).Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.Equal("note_required", await Code(await c.Supervisor.PostAsJsonAsync($"/api/corrections/{r2.Id}/reject", new CorrectionDecisionRequest(" "))));
        Assert.Equal(HttpStatusCode.OK, (await c.Supervisor.PostAsJsonAsync($"/api/corrections/{r2.Id}/reject", new CorrectionDecisionRequest("Machine was right"))).StatusCode);
        Assert.False((await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{e2.Id}"))!.IsReversed);
        Assert.Equal("correction_not_pending", await Code(await c.Supervisor.PostAsJsonAsync($"/api/corrections/{r2.Id}/approve", new CorrectionDecisionRequest(null))));

        // Withdraw.
        var r3 = (await (await c.Inspector.PostAsJsonAsync(url(e3), new CorrectionRequest("my mistake", Quantity: 1))).Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.Equal("Withdrawn", (await (await c.Inspector.PostAsync($"/api/corrections/{r3.Id}/withdraw", null)).Content.ReadFromJsonAsync<CorrectionDto>())!.Status);

        // Rules.
        Assert.Equal("no_change", await Code(await c.Inspector.PostAsJsonAsync(url(e3), new CorrectionRequest("same", Quantity: e3.Quantity))));
        Assert.Equal("reason_invalid", await Code(await c.Inspector.PostAsJsonAsync(url(e3), new CorrectionRequest("  "))));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Supervisor.PostAsJsonAsync(url(e3), new CorrectionRequest("supervisors do not request"))).StatusCode);
        var other = await SetupAsync("PRE");   // another inspector of the same shop
        Assert.Equal(HttpStatusCode.NotFound, (await other.Inspector.PostAsJsonAsync(url(e3), new CorrectionRequest("not mine"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Inspector.PostAsJsonAsync($"/api/corrections/{r2.Id}/approve", new CorrectionDecisionRequest(null))).StatusCode);
        // A supervisor of another shop cannot see or decide this shop's requests.
        var gdc = await SetupAsync("GDC");
        var r4 = (await (await c.Inspector.PostAsJsonAsync(url(e3), new CorrectionRequest("for the GDC supervisor?", Quantity: 7))).Content.ReadFromJsonAsync<CorrectionDto>())!;
        Assert.DoesNotContain((await gdc.Supervisor.GetFromJsonAsync<List<CorrectionDto>>("/api/corrections"))!, x => x.Id == r4.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await gdc.Supervisor.PostAsJsonAsync($"/api/corrections/{r4.Id}/approve", new CorrectionDecisionRequest(null))).StatusCode);
        Assert.Contains((await c.Inspector.GetFromJsonAsync<List<CorrectionDto>>("/api/corrections?status=all"))!, x => x.Id == r3.Id && x.Status == "Withdrawn");
    }

    [Fact]
    public async Task Two_supervisors_approving_at_once_apply_the_correction_once()
    {
        var c = await SetupAsync("FINAL");
        var (_, s2u, s2p) = await F.CreateUserAsync("Supervisor", "FINAL");
        var sup2 = F.Client(await F.LoginAsync(s2u, s2p));
        for (var round = 0; round < 5; round++)
        {
            var e = await PostEntry(c, qty: 5);
            var corr = (await (await c.Inspector.PostAsJsonAsync($"/api/rejection-entries/{c.Shop.Id}/{e.Id}/corrections", new CorrectionRequest("race", Quantity: 3))).Content.ReadFromJsonAsync<CorrectionDto>())!;
            var rs = await Task.WhenAll(c.Supervisor.PostAsJsonAsync($"/api/corrections/{corr.Id}/approve", new CorrectionDecisionRequest(null)),
                                        sup2.PostAsJsonAsync($"/api/corrections/{corr.Id}/approve", new CorrectionDecisionRequest(null)));
            Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal("correction_not_pending", await Code(rs.Single(r => r.StatusCode != HttpStatusCode.OK)));
            Assert.Equal(1, await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE CorrectsEntryId = {e.Id} AND ShopId = {c.Shop.Id}"));
        }
    }

    [Fact]
    public async Task Applying_the_same_correction_twice_in_the_entry_database_is_idempotent()
    {
        var c = await SetupAsync("GDC");
        var e = await PostEntry(c);
        var (scope, router) = F.Scoped<IEntryStoreRouter>();
        using (scope)
        {
            var store = router.For("GDC");
            var orig = (await store.GetByIdAsync(e.Id))!;
            var replacementId = Guid.NewGuid();
            Quality.Core.Entities.QualityRejectionEntry Make() => new()
            {
                SubmissionId = replacementId, ShopId = orig.ShopId, ShopCode = orig.ShopCode, InspectionStageId = orig.InspectionStageId,
                StageCode = orig.StageCode, StageName = orig.StageName, MachineId = orig.MachineId, MachineCode = orig.MachineCode, MachineName = orig.MachineName,
                RejectionPhenomenonId = orig.RejectionPhenomenonId, PhenomenonNameSnapshot = orig.PhenomenonNameSnapshot, Quantity = 1,
                InspectorId = orig.InspectorId, InspectorEmployeeCode = orig.InspectorEmployeeCode, ServerCreatedAt = DateTime.UtcNow,
                ProductionDate = orig.ProductionDate, ShiftCode = orig.ShiftCode, CorrectsEntryId = orig.Id,
            };
            var a = await store.ApplyCorrectionAsync(orig.ShopId, orig.Id, 999_000 + e.Id, DateTime.UtcNow, Make());
            var b = await store.ApplyCorrectionAsync(orig.ShopId, orig.Id, 999_000 + e.Id, DateTime.UtcNow, Make());   // retry after a failure
            Assert.Equal(a!.Id, b!.Id);
            await Assert.ThrowsAsync<CorrectionConflictException>(() => store.ApplyCorrectionAsync(orig.ShopId, orig.Id, 123_456_789, DateTime.UtcNow, null));
        }
    }

    // ---------------- independent-review fixes ----------------
    /// <summary>Review #1: the entry-database half of an approval committed, the master half did not. Reject/withdraw must not win.</summary>
    [Fact]
    public async Task Half_applied_approval_is_completed_as_approved_not_rejected_or_withdrawn()
    {
        var c = await SetupAsync("GDC");
        foreach (var how in new[] { "reject", "withdraw" })
        {
            var e = await PostEntry(c, qty: 6);
            var corr = (await (await c.Inspector.PostAsJsonAsync($"/api/rejection-entries/{c.Shop.Id}/{e.Id}/corrections", new CorrectionRequest("was 3", Quantity: 3))).Content.ReadFromJsonAsync<CorrectionDto>())!;
            var repId = await SqlFixture.Scalar<Guid>(F.MasterCs, $"SELECT ReplacementSubmissionId FROM qa.EntryCorrections WHERE Id = {corr.Id}");
            // Simulate the failure: apply in the entry database only.
            var (scope, router) = F.Scoped<IEntryStoreRouter>();
            using (scope)
            {
                var orig = (await router.For("GDC").GetByIdAsync(e.Id))!;
                orig.Id = 0; orig.SubmissionId = repId; orig.Quantity = 3; orig.CorrectsEntryId = e.Id; orig.ReversedAt = null; orig.ReversedByCorrectionId = null;
                await router.For("GDC").ApplyCorrectionAsync(c.Shop.Id, e.Id, corr.Id, DateTime.UtcNow, orig);
            }
            var r = how == "reject"
                ? await c.Supervisor.PostAsJsonAsync($"/api/corrections/{corr.Id}/reject", new CorrectionDecisionRequest("no"))
                : await c.Inspector.PostAsync($"/api/corrections/{corr.Id}/withdraw", null);
            Assert.Equal("correction_already_applied", await Code(r));
            var row = await SqlFixture.Rows(F.MasterCs, $"SELECT Status, ReplacementEntryId FROM qa.EntryCorrections WHERE Id = {corr.Id}");
            Assert.Equal("Approved", (string)row[0][0]);
            Assert.False(row[0][1] is DBNull);
        }
    }

    /// <summary>Review #6: an ordinary entry carrying the replacement's SubmissionId must never be adopted as the replacement.</summary>
    [Fact]
    public async Task A_pre_claimed_replacement_id_is_refused_and_nothing_is_reversed()
    {
        var c = await SetupAsync("CNC");
        var e = await PostEntry(c, qty: 5);
        var corr = (await (await c.Inspector.PostAsJsonAsync($"/api/rejection-entries/{c.Shop.Id}/{e.Id}/corrections", new CorrectionRequest("was 1", Quantity: 1))).Content.ReadFromJsonAsync<CorrectionDto>())!;
        var repId = await SqlFixture.Scalar<Guid>(F.MasterCs, $"SELECT ReplacementSubmissionId FROM qa.EntryCorrections WHERE Id = {corr.Id}");
        Assert.Equal(HttpStatusCode.Created, (await c.Inspector.PostAsJsonAsync("/api/rejection-entries",
            new CreateEntryRequest(repId, c.Shop.Id, c.Stage.Id, c.Machines[0].Id, c.Phenomena[0].Id, 9, "squatter"))).StatusCode);
        var r = await c.Supervisor.PostAsJsonAsync($"/api/corrections/{corr.Id}/approve", new CorrectionDecisionRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.False((await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{e.Id}"))!.IsReversed);   // rolled back
        Assert.Equal("Pending", await SqlFixture.Scalar<string>(F.MasterCs, $"SELECT Status FROM qa.EntryCorrections WHERE Id = {corr.Id}"));
        // The replacement id is never sent to clients.
        Assert.DoesNotContain(repId.ToString(), await (await c.Supervisor.GetAsync("/api/corrections?status=all")).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Count_endpoint_matches_the_list_in_scope()
    {
        var c = await SetupAsync("FINAL");
        var e = await PostEntry(c);
        await c.Inspector.PostAsJsonAsync($"/api/rejection-entries/{c.Shop.Id}/{e.Id}/corrections", new CorrectionRequest("count me"));
        Assert.Equal(1, await c.Inspector.GetFromJsonAsync<int>("/api/corrections/count"));   // inspector: own only
        var listed = (await c.Supervisor.GetFromJsonAsync<List<CorrectionDto>>("/api/corrections"))!.Count;
        Assert.Equal(listed, await c.Supervisor.GetFromJsonAsync<int>("/api/corrections/count"));
    }

    // ---------------- #4 / #6 reports ----------------
    [Fact]
    public async Task Totals_group_by_day_shift_phenomenon_machine_and_exclude_reversed_entries()
    {
        var shops = await F.ShopsAsync();
        var gdc = shops["GDC"]; var cnc = shops["CNC"];
        var day = new DateOnly(2031, 3, 5);   // far-future production day: only this test's rows
        async Task Insert(string cs, ShopDto shop, string shift, int machineId, string machine, int phenId, string phen, int qty, bool reversed = false) =>
            await SqlFixture.Scalar<int>(cs, $"""
                INSERT INTO qa.QualityRejectionEntries (SubmissionId, ShopId, ShopCode, InspectionStageId, StageCode, StageName, MachineId, MachineCode, MachineName,
                  RejectionPhenomenonId, PhenomenonNameSnapshot, Quantity, InspectorId, InspectorEmployeeCode, ServerCreatedAt, ProductionDate, ShiftCode, ReversedAt, ReversedByCorrectionId)
                VALUES (NEWID(), {shop.Id}, '{shop.ShopCode}', 1, 'S', 'S', {machineId}, '{machine}', '{machine} name', {phenId}, '{phen}', {qty}, 1, 'E', SYSUTCDATETIME(), '2031-03-05', '{shift}',
                        {(reversed ? "SYSUTCDATETIME(), 1" : "NULL, NULL")}); SELECT 1
                """);
        await Insert(F.GdcCs, gdc, "A", 9001, "RPT-G1", 8001, "Cold Shot", 5);
        await Insert(F.GdcCs, gdc, "A", 9001, "RPT-G1", 8002, "Pin Deep", 2);
        await Insert(F.GdcCs, gdc, "C", 9002, "RPT-G2", 8001, "Cold Shot", 3);
        await Insert(F.GdcCs, gdc, "C", 9002, "RPT-G2", 8001, "Cold Shot", 50, reversed: true);   // must not count
        await Insert(F.CncCs, cnc, "B", 9003, "RPT-C1", 8003, "DENT", 4);

        var (_, su, spw) = await F.CreateUserAsync("Supervisor", "GDC", "CNC");
        var sup = F.Client(await F.LoginAsync(su, spw));
        var r = (await sup.GetFromJsonAsync<ReportDto>("/api/reports/totals?fromDate=2031-03-05&toDate=2031-03-05"))!;
        Assert.True(r.IsComplete);
        Assert.Equal((14, 4), (r.Total.Quantity, r.Total.Entries));
        Assert.Equal(new[] { "CNC:4", "GDC:10" }, r.Shops.Select(s => $"{s.ShopCode}:{s.Quantity}").OrderBy(x => x));
        Assert.Equal(new[] { "A:7", "B:4", "C:3" }, r.ByDayShift.Select(d => $"{d.ShiftCode}:{d.Quantity}"));
        Assert.Equal(new[] { "Cold Shot:8", "DENT:4", "Pin Deep:2" }, r.TopPhenomena.Select(p => $"{p.PhenomenonName}:{p.Quantity}"));
        Assert.Equal("RPT-G1", r.TopMachines[0].MachineCode);
        var nightOnly = (await sup.GetFromJsonAsync<ReportDto>("/api/reports/totals?fromDate=2031-03-05&toDate=2031-03-05&shiftCode=C"))!;
        Assert.Equal(3, nightOnly.Total.Quantity);

        // Scope and validation.
        var (_, iu, ipw) = await F.CreateUserAsync("QualityInspector", "GDC");
        Assert.Equal(HttpStatusCode.Forbidden, (await F.Client(await F.LoginAsync(iu, ipw)).GetAsync("/api/reports/totals")).StatusCode);
        var (_, pu, ppw) = await F.CreateUserAsync("Supervisor", "PRE");
        var preSup = F.Client(await F.LoginAsync(pu, ppw));
        Assert.Equal(0, (await preSup.GetFromJsonAsync<ReportDto>("/api/reports/totals?fromDate=2031-03-05&toDate=2031-03-05"))!.Total.Quantity);
        Assert.Equal(HttpStatusCode.Forbidden, (await preSup.GetAsync($"/api/reports/totals?shopId={gdc.Id}")).StatusCode);
        Assert.Equal("date_range_too_long", await Code(await sup.GetAsync("/api/reports/totals?fromDate=2031-01-01&toDate=2031-12-31")));
        Assert.Equal("shift_invalid", await Code(await sup.GetAsync("/api/reports/totals?shiftCode=Z")));
    }

    // ---------------- #8 QR ----------------
    [Fact]
    public async Task Qr_label_and_lookup()
    {
        var c = await SetupAsync("CNC");
        var admin = F.Client(await F.AdminTokenAsync());
        var m = c.Machines[0];
        var svg = await admin.GetAsync($"/api/admin/machines/{m.Id}/qr.svg");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Supervisor.GetAsync($"/api/admin/machines/{m.Id}/qr.svg")).StatusCode);

        var found = await c.Inspector.GetFromJsonAsync<MachineLookupDto>($"/api/machines/lookup?code={Uri.EscapeDataString(MachineLookupService.LabelPayload(m.Id))}");
        Assert.Equal((c.Shop.Id, m.Id, 1), (found!.Shop.Id, found.Machine.Id, found.Stages.Count));
        Assert.Equal("qr_invalid", await Code(await c.Inspector.GetAsync("/api/machines/lookup?code=hello")));

        var gdcMachine = (await admin.GetFromJsonAsync<List<AdminMachineDto>>($"/api/admin/machines?shopId={(await F.ShopsAsync())["GDC"].Id}"))!.First();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Inspector.GetAsync($"/api/machines/lookup?code=RJWLQR:M:{gdcMachine.Id}")).StatusCode);   // not assigned to GDC

        var inactive = await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(c.Shop.Id, "QR-OFF-" + F.Prefix[^5..], "off", false))).Content.ReadFromJsonAsync<AdminMachineDto>();
        Assert.Equal("machine_inactive", await Code(await c.Inspector.GetAsync($"/api/machines/lookup?code=RJWLQR:M:{inactive!.Id}")));
    }

    // ---------------- dashboard hosting ----------------
    [Fact]
    public async Task Dashboard_is_served_with_a_strict_content_security_policy()
    {
        foreach (var path in new[] { "/dashboard/", "/dashboard/labels.html", "/dashboard/dashboard.js" })
        {
            var r = await F.Client().GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var csp = string.Join(";", r.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("script-src 'self'", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
        }
        var html = await F.Client().GetStringAsync("/dashboard/");
        Assert.DoesNotContain("<script>", html);   // no inline script (CSP would block it anyway)
    }

    // ---------------- desktop app (installable dashboard) ----------------
    [Fact]
    public async Task Desktop_app_manifest_icons_and_service_worker_are_served()
    {
        var m = await F.Client().GetAsync("/dashboard/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, m.StatusCode);
        Assert.Equal("application/manifest+json", m.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", string.Join(";", m.Headers.GetValues("Content-Security-Policy")));
        using var doc = JsonDocument.Parse(await m.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("/dashboard/", root.GetProperty("start_url").GetString());
        Assert.Equal("/dashboard/", root.GetProperty("scope").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        foreach (var icon in root.GetProperty("icons").EnumerateArray())
        {
            var r = await F.Client().GetAsync("/dashboard/" + icon.GetProperty("src").GetString());
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.Contains(root.GetProperty("icons").EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");

        var sw = await F.Client().GetAsync("/dashboard/sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Equal("text/javascript", sw.Content.Headers.ContentType?.MediaType);
        var swText = await sw.Content.ReadAsStringAsync();
        Assert.Contains("url.pathname.startsWith('/api/')", swText);   // report data is never cached

        var html = await F.Client().GetStringAsync("/dashboard/");
        Assert.Contains("rel=\"manifest\"", html);
        Assert.Contains("id=\"install\"", html);
    }
}
