using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quality.Core.Dtos;

namespace Quality.Tests.Integration;

public sealed class EntryTests(SqlFixture f) : SqlTest(f)
{
    private static async Task<string?> Code(HttpResponseMessage r)
    {
        try { return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private sealed record Ctx(HttpClient Inspector, ShopDto Shop, StageDto Stage, MachineDto Machine, PhenomenonDto Phenomenon);

    private async Task<Ctx> InspectorIn(string shopCode)
    {
        var shops = await F.ShopsAsync();
        var (_, u, pw) = await F.CreateUserAsync("QualityInspector", shopCode);
        var http = F.Client(await F.LoginAsync(u, pw));
        var shop = shops[shopCode];
        var stage = (await http.GetFromJsonAsync<List<StageDto>>($"/api/shops/{shop.Id}/stages"))!.Single();   // one stage -> app skips the stage screen
        var machine = (await http.GetFromJsonAsync<List<MachineDto>>($"/api/shops/{shop.Id}/stages/{stage.Id}/machines"))!.First();
        var phen = (await http.GetFromJsonAsync<List<PhenomenonDto>>($"/api/shops/{shop.Id}/stages/{stage.Id}/phenomena"))!.First();
        return new Ctx(http, shop, stage, machine, phen);
    }

    private static CreateEntryRequest Req(Ctx c, Guid id, int qty = 2, string? remarks = "first") => new(id, c.Shop.Id, c.Stage.Id, c.Machine.Id, c.Phenomenon.Id, qty, remarks);

    [Theory]
    [InlineData("GDC")] [InlineData("CNC")] [InlineData("PRE")] [InlineData("FINAL")]
    public async Task Entry_flow_works_for_every_shop_and_lands_in_the_right_database(string shopCode)
    {
        var c = await InspectorIn(shopCode);
        var r = await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, Guid.NewGuid(), 3, "  note  "));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var e = (await r.Content.ReadFromJsonAsync<CreateEntryResult>())!.Entry;
        Assert.Equal((shopCode, 3, "note", c.Machine.MachineCode, c.Phenomenon.PhenomenonName), (e.ShopCode, e.Quantity, e.Remarks, e.MachineCode, e.PhenomenonName));
        Assert.Equal(DateTimeKind.Utc, e.ServerCreatedAtUtc.Kind);

        var cs = shopCode switch { "GDC" => F.GdcCs, "CNC" => F.CncCs, _ => F.MasterCs };
        Assert.Equal(1, await SqlFixture.Scalar<int>(cs, "SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE SubmissionId = @s", ("@s", e.SubmissionId)));
        var got = await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{e.Id}");
        Assert.Equal(e.SubmissionId, got!.SubmissionId);
    }

    [Fact]
    public async Task Pre_and_final_share_a_table_but_not_their_entries()
    {
        var pre = await InspectorIn("PRE");
        var e = (await (await pre.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(pre, Guid.NewGuid()))).Content.ReadFromJsonAsync<CreateEntryResult>())!.Entry;
        var shops = await F.ShopsAsync();
        var admin = F.Client(await F.AdminTokenAsync());
        // Same table, same id - but asked for under FINAL it does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/rejection-entries/{shops["FINAL"].Id}/{e.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/rejection-entries/{shops["PRE"].Id}/{e.Id}")).StatusCode);
    }

    // ---------------- Q7 ----------------
    [Fact]
    public async Task Q7_retry_with_changed_remarks_is_a_conflict_identical_retry_is_a_replay()
    {
        var c = await InspectorIn("GDC");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, id, 2, "first"))).StatusCode);

        var same = await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, id, 2, "  first "));   // same after clean-up
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.True((await same.Content.ReadFromJsonAsync<CreateEntryResult>())!.AlreadySubmitted);

        var changed = await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, id, 2, "different"));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("submission_payload_mismatch", await Code(changed));
        var removed = await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, id, 2, null));
        Assert.Equal("submission_payload_mismatch", await Code(removed));
        Assert.Equal(1, await SqlFixture.Scalar<int>(F.GdcCs, "SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE SubmissionId = @s", ("@s", id)));
    }

    [Fact]
    public async Task Simultaneous_double_submit_saves_once()
    {
        var c = await InspectorIn("CNC");
        var id = Guid.NewGuid();
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, id))));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(rs, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Equal(1, await SqlFixture.Scalar<int>(F.CncCs, "SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE SubmissionId = @s", ("@s", id)));
    }

    [Fact]
    public async Task Validation_boundaries()
    {
        var c = await InspectorIn("GDC");
        async Task<string?> Post(CreateEntryRequest r) => await Code(await c.Inspector.PostAsJsonAsync("/api/rejection-entries", r));
        Assert.Equal("quantity_invalid", await Post(Req(c, Guid.NewGuid(), 0)));
        Assert.Equal("quantity_invalid", await Post(Req(c, Guid.NewGuid(), 10000)));
        Assert.Equal("remarks_too_long", await Post(Req(c, Guid.NewGuid(), 1, new string('x', 501))));
        Assert.Equal("submission_id_required", await Post(Req(c, Guid.Empty)));
        Assert.Equal(HttpStatusCode.Created, (await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, Guid.NewGuid(), 9999, new string('x', 500)))).StatusCode);
    }

    [Fact]
    public async Task Inactive_machine_gets_its_own_code_so_the_app_can_refresh()
    {
        var c = await InspectorIn("CNC");
        var admin = F.Client(await F.AdminTokenAsync());
        var m = await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(c.Shop.Id, "INACT-" + F.Prefix[^5..], "soon inactive", true))).Content.ReadFromJsonAsync<AdminMachineDto>();
        (await admin.PatchAsJsonAsync($"/api/admin/machines/{m!.Id}/status", new SetStatusRequest(false))).EnsureSuccessStatusCode();
        var withInactive = c with { Machine = new MachineDto(m.Id, m.MachineCode, m.MachineName) };
        var r = await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(withInactive, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("machine_inactive", await Code(r));
    }

    [Fact]
    public async Task History_keeps_snapshots_after_master_data_changes()
    {
        var c = await InspectorIn("GDC");
        var admin = F.Client(await F.AdminTokenAsync());
        var m = await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(c.Shop.Id, "SNAP-" + F.Prefix[^5..], "Old name", true))).Content.ReadFromJsonAsync<AdminMachineDto>();
        var e = (await (await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c with { Machine = new MachineDto(m!.Id, m.MachineCode, m.MachineName) }, Guid.NewGuid()))).Content.ReadFromJsonAsync<CreateEntryResult>())!.Entry;
        (await admin.PutAsJsonAsync($"/api/admin/machines/{m.Id}", new UpdateMachineRequest(m.MachineCode, "New name"))).EnsureSuccessStatusCode();
        var again = await c.Inspector.GetFromJsonAsync<EntryDto>($"/api/rejection-entries/{c.Shop.Id}/{e.Id}");
        Assert.Equal("Old name", again!.MachineName);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/machines/{m.Id}")).StatusCode);   // used -> deactivate instead
    }

    // ---------------- History paging ----------------
    [Fact]
    public async Task Paging_returns_every_entry_once_in_a_stable_order_even_with_equal_timestamps()
    {
        var shops = await F.ShopsAsync();
        var (_, sup, spw) = await F.CreateUserAsync("Supervisor", "PRE", "FINAL");
        var supervisor = F.Client(await F.LoginAsync(sup, spw));
        // 7 PRE + 7 FINAL rows with IDENTICAL timestamps, inserted directly (same table, different shops).
        var t = new DateTime(2030, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
        var stageIds = new Dictionary<string, int>
        {
            ["PRE"] = await SqlFixture.Scalar<int>(F.MasterCs, "SELECT Id FROM qa.InspectionStages WHERE StageCode='PRE_TREATMENT'"),
            ["FINAL"] = await SqlFixture.Scalar<int>(F.MasterCs, "SELECT Id FROM qa.InspectionStages WHERE StageCode='FINAL'"),
        };
        foreach (var code in new[] { "PRE", "FINAL" })
            for (var i = 0; i < 7; i++)
                await SqlFixture.Scalar<int>(F.MasterCs, $"""
                    INSERT INTO qa.QualityRejectionEntries (SubmissionId, ShopId, ShopCode, InspectionStageId, StageCode, StageName, MachineId, MachineCode, MachineName,
                      RejectionPhenomenonId, PhenomenonNameSnapshot, Quantity, InspectorId, InspectorEmployeeCode, Remarks, ServerCreatedAt)
                    VALUES (NEWID(), {shops[code].Id}, '{code}', {stageIds[code]}, 'S', 'S', 1, 'M', 'M', 1, 'P', 1, 1, 'E', 'paging', @t); SELECT 1
                    """, ("@t", t));

        var seen = new List<EntryDto>(); string? cursor = null; var pages = 0;
        var from = Uri.EscapeDataString(t.AddSeconds(-1).ToString("O")); var to = Uri.EscapeDataString(t.AddSeconds(1).ToString("O"));
        do
        {
            var page = await supervisor.GetFromJsonAsync<EntryPage>($"/api/rejection-entries/page?limit=3&fromUtc={from}&toUtc={to}" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.True(page!.IsComplete);
            seen.AddRange(page.Items); cursor = page.NextCursor; pages++;
        } while (cursor is not null && pages < 20);

        Assert.Equal(14, seen.Count);
        Assert.Equal(14, seen.Select(e => (e.ShopId, e.Id)).Distinct().Count());
        Assert.Equal(seen.OrderByDescending(e => e.ServerCreatedAtUtc).ThenByDescending(e => e.ShopId).ThenByDescending(e => e.Id).Select(e => e.Id), seen.Select(e => e.Id));
        Assert.Equal(5, pages);
        // toUtc is exclusive.
        var exclusive = await supervisor.GetFromJsonAsync<EntryPage>($"/api/rejection-entries/page?fromUtc={from}&toUtc={Uri.EscapeDataString(t.ToString("O"))}");
        Assert.Empty(exclusive!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await supervisor.GetAsync("/api/rejection-entries/page?cursor=garbage!")).StatusCode);
    }

    [Fact]
    public async Task Paged_history_discloses_an_unreachable_store_instead_of_looking_complete()
    {
        var bad = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(F.CncCs) { InitialCatalog = "QR_DOES_NOT_EXIST", ConnectTimeout = 3 }.ConnectionString;
        await using var factory = F.CreateFactory(new Dictionary<string, string?> { ["ConnectionStrings:CncEntries"] = bad });
        var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var token = (await (await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(SqlFixture.AdminUser, SqlFixture.AdminPassword))).Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var page = await http.GetFromJsonAsync<EntryPage>("/api/rejection-entries/page");
        Assert.False(page!.IsComplete);
        Assert.Equal(new[] { "CNC" }, page.UnavailableShops);
    }

    [Fact]
    public async Task Date_filters_accept_utc_and_offset_times()
    {
        var c = await InspectorIn("FINAL");
        var e = (await (await c.Inspector.PostAsJsonAsync("/api/rejection-entries", Req(c, Guid.NewGuid()))).Content.ReadFromJsonAsync<CreateEntryResult>())!.Entry;
        var ist = e.ServerCreatedAtUtc.AddHours(5.5);   // the same instant written as India Standard Time
        var from = Uri.EscapeDataString(new DateTimeOffset(ist.AddMinutes(-1).Ticks, TimeSpan.FromHours(5.5)).ToString("O"));
        var to = Uri.EscapeDataString(new DateTimeOffset(ist.AddMinutes(1).Ticks, TimeSpan.FromHours(5.5)).ToString("O"));
        var page = await c.Inspector.GetFromJsonAsync<EntryPage>($"/api/rejection-entries/page?fromUtc={from}&toUtc={to}");
        Assert.Contains(page!.Items, x => x.SubmissionId == e.SubmissionId);
    }

    // ---------------- Stale screens ----------------
    [Fact]
    public async Task Saving_from_a_stale_screen_is_refused()
    {
        var shops = await F.ShopsAsync();
        var admin = F.Client(await F.AdminTokenAsync());
        var m = await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(shops["GDC"].Id, "STALE-" + F.Prefix[^5..], "v1", true))).Content.ReadFromJsonAsync<AdminMachineDto>();
        Assert.False(string.IsNullOrEmpty(m!.Version));
        var first = await admin.PutAsJsonAsync($"/api/admin/machines/{m.Id}", new UpdateMachineRequest(m.MachineCode, "v2 by admin A", m.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await admin.PutAsJsonAsync($"/api/admin/machines/{m.Id}", new UpdateMachineRequest(m.MachineCode, "v2 by admin B (old screen)", m.Version));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrent_edit", await Code(second));

        // Users: a shop-only change also moves the version.
        var (uid, _, _) = await F.CreateUserAsync("QualityInspector", "GDC");
        var u = (await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users"))!.Single(x => x.Id == uid);
        (await admin.PutAsJsonAsync($"/api/admin/users/{uid}", new UpdateUserRequest(u.DisplayName, u.Role, new[] { shops["CNC"].Id }, u.Version))).EnsureSuccessStatusCode();
        var stale = await admin.PutAsJsonAsync($"/api/admin/users/{uid}", new UpdateUserRequest(u.DisplayName, u.Role, new[] { shops["GDC"].Id }, u.Version));
        Assert.Equal("concurrent_edit", await Code(stale));

        // Phenomena (a new one, so parallel tests that use the seeded list are not affected).
        var stage = (await admin.GetFromJsonAsync<List<StageDto>>($"/api/shops/{shops["GDC"].Id}/stages"))!.Single();
        var p = await (await admin.PostAsJsonAsync("/api/admin/phenomena", new CreatePhenomenonRequest(shops["GDC"].Id, stage.Id, "Stale check " + F.Prefix, true))).Content.ReadFromJsonAsync<AdminPhenomenonDto>();
        (await admin.PatchAsJsonAsync($"/api/admin/phenomena/{p!.Id}/status", new SetStatusRequest(false, p.Version))).EnsureSuccessStatusCode();
        Assert.Equal("concurrent_edit", await Code(await admin.PutAsJsonAsync($"/api/admin/phenomena/{p.Id}", new UpdatePhenomenonRequest(p.PhenomenonName + " x", p.Version))));
    }

    [Fact]
    public async Task Every_admin_change_is_audited_without_passwords()
    {
        var (uid, _, _) = await F.CreateUserAsync("Supervisor", "CNC");
        var admin = F.Client(await F.AdminTokenAsync());
        (await admin.PostAsJsonAsync($"/api/admin/users/{uid}/reset-password", new ResetPasswordRequest("Another-Pass-777"))).EnsureSuccessStatusCode();
        var rows = await SqlFixture.Rows(F.MasterCs, $"SELECT Action, ISNULL(OldValue,''), ISNULL(NewValue,'') FROM qa.AuditLog WHERE EntityType = 'User' AND EntityId = '{uid}' ORDER BY Id");
        Assert.Equal(new[] { "USER_CREATED", "USER_PASSWORD_RESET" }, rows.Select(r => (string)r[0]));
        Assert.All(rows, r => { Assert.DoesNotContain("Pass", (string)r[1] + (string)r[2]); Assert.DoesNotContain("AQAAAA", (string)r[1] + (string)r[2]); });
    }
}
