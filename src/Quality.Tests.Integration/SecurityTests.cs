using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quality.Core.Dtos;

namespace Quality.Tests.Integration;

public sealed class SecurityTests(SqlFixture f) : SqlTest(f)
{
    private static async Task<string?> Code(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; } catch (JsonException) { return null; }
    }

    // ---------------- Q2: revocation ----------------
    [Fact]
    public async Task Q2_password_reset_revokes_existing_tokens()
    {
        var (id, user, pw) = await F.CreateUserAsync("QualityInspector", "GDC");
        var old = await F.LoginAsync(user, pw);
        Assert.Equal(HttpStatusCode.OK, (await F.Client(old).GetAsync("/api/shops")).StatusCode);

        var admin = F.Client(await F.AdminTokenAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/users/{id}/reset-password", new ResetPasswordRequest("Brand-New-Pass-99"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await F.Client(old).GetAsync("/api/shops")).StatusCode);
        var fresh = await F.LoginAsync(user, "Brand-New-Pass-99");
        Assert.Equal(HttpStatusCode.OK, (await F.Client(fresh).GetAsync("/api/shops")).StatusCode);
    }

    [Fact]
    public async Task Q2_deactivate_then_reactivate_does_not_revive_old_tokens()
    {
        var (id, user, pw) = await F.CreateUserAsync("Supervisor", "CNC");
        var old = await F.LoginAsync(user, pw);
        var admin = F.Client(await F.AdminTokenAsync());
        (await admin.PatchAsJsonAsync($"/api/admin/users/{id}/status", new SetStatusRequest(false))).EnsureSuccessStatusCode();
        (await admin.PatchAsJsonAsync($"/api/admin/users/{id}/status", new SetStatusRequest(true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.Client(old).GetAsync("/api/shops")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await F.Client(await F.LoginAsync(user, pw)).GetAsync("/api/shops")).StatusCode);
    }

    [Fact]
    public async Task Q2_role_change_and_back_does_not_revive_old_tokens()
    {
        var (id, user, pw) = await F.CreateUserAsync("QualityInspector", "PRE");
        var old = await F.LoginAsync(user, pw);
        var admin = F.Client(await F.AdminTokenAsync());
        var shops = await F.ShopsAsync();
        (await admin.PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserRequest("Changed", "Supervisor", new[] { shops["PRE"].Id }))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserRequest("Changed", "QualityInspector", new[] { shops["PRE"].Id }))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.Client(old).GetAsync("/api/shops")).StatusCode);
    }

    [Fact]
    public async Task Malformed_or_foreign_tokens_get_401_not_500()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.Client("not-a-jwt").GetAsync("/api/shops")).StatusCode);
        // Correct structure, wrong signature.
        const string forged = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJhYmMiLCJyb2xlIjoiQWRtaW5pc3RyYXRvciJ9.c2lnbmF0dXJl";
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.Client(forged).GetAsync("/api/shops")).StatusCode);
    }

    // ---------------- Q4: login throttling ----------------
    [Fact]
    public async Task Q4_one_clients_failures_lock_only_that_login_not_everyone()
    {
        var (_, victim, _) = await F.CreateUserAsync("QualityInspector", "GDC");
        var (_, other, otherPw) = await F.CreateUserAsync("QualityInspector", "GDC");
        var http = F.Client();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(victim, "wrong-password-" + i))).StatusCode);

        var blocked = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(victim, "wrong-password-x"));
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Equal("too_many_attempts", await Code(blocked));
        Assert.True(blocked.Headers.RetryAfter is not null);

        // A different person on the same client/network still signs in (the old single global quota blocked everybody).
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(other, otherPw))).StatusCode);
    }

    // ---------------- Q5 / Q6: identifiers ----------------
    [Fact]
    public async Task Q5_username_may_not_equal_another_users_employee_code()
    {
        var admin = F.Client(await F.AdminTokenAsync());
        var shops = await F.ShopsAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("Q5X01", "adm001", "Clash", "QualityInspector", "Some-Password-1", new[] { shops["GDC"].Id }));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);   // "adm001" is the bootstrap admin's employee code (case-insensitive)
        Assert.Equal("duplicate_user", await Code(r));
    }

    [Fact]
    public async Task Q6_employee_code_limits_are_validated_not_reported_as_database_errors()
    {
        var admin = F.Client(await F.AdminTokenAsync());
        var shops = await F.ShopsAsync();
        var thirty = "Q6" + new string('A', 28); var thirtyOne = thirty + "B";
        var ok = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(thirty, "q6.thirty", "Thirty", "QualityInspector", "Some-Password-1", new[] { shops["GDC"].Id }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var tooLong = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(thirtyOne, "q6.thirtyone", "Thirty-one", "QualityInspector", "Some-Password-1", new[] { shops["GDC"].Id }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("employee_code_invalid", await Code(tooLong));
        // The 30-character code also works as a login.
        Assert.False(string.IsNullOrEmpty(await F.LoginAsync(thirty.ToLowerInvariant(), "Some-Password-1")));
    }

    // ---------------- Roles and shop scope (brief §6) ----------------
    [Fact]
    public async Task Server_enforces_roles_and_shop_assignment()
    {
        var (_, insp, ipw) = await F.CreateUserAsync("QualityInspector", "GDC");
        var (_, sup, spw) = await F.CreateUserAsync("Supervisor", "GDC");
        var inspector = F.Client(await F.LoginAsync(insp, ipw));
        var supervisor = F.Client(await F.LoginAsync(sup, spw));
        var shops = await F.ShopsAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.GetAsync("/api/admin/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync($"/api/shops/{shops["CNC"].Id}/stages")).StatusCode);   // not assigned
        var entry = new CreateEntryRequest(Guid.NewGuid(), shops["GDC"].Id, 1, 1, 1, 1, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsJsonAsync("/api/rejection-entries", entry)).StatusCode);
        var adminEntry = await F.Client(await F.AdminTokenAsync()).PostAsJsonAsync("/api/rejection-entries", entry);
        Assert.Equal(HttpStatusCode.Forbidden, adminEntry.StatusCode);   // supplied rule: inspectors only
        Assert.Single((await inspector.GetFromJsonAsync<List<ShopDto>>("/api/shops"))!);
    }

    [Fact]
    public async Task Removing_a_shop_assignment_takes_effect_on_the_next_request()
    {
        var (id, insp, ipw) = await F.CreateUserAsync("QualityInspector", "GDC", "CNC");
        var inspector = F.Client(await F.LoginAsync(insp, ipw));
        var shops = await F.ShopsAsync();
        Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync($"/api/shops/{shops["CNC"].Id}/stages")).StatusCode);
        var admin = F.Client(await F.AdminTokenAsync());
        (await admin.PutAsJsonAsync($"/api/admin/users/{id}", new UpdateUserRequest("Test", "QualityInspector", new[] { shops["GDC"].Id }))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync($"/api/shops/{shops["CNC"].Id}/stages")).StatusCode);
    }

    // ---------------- Health / readiness ----------------
    [Fact]
    public async Task Readiness_checks_every_database()
    {
        var r = await F.Client().GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var dto = await r.Content.ReadFromJsonAsync<ReadinessDto>();
        Assert.Equal(new[] { "entries:CNC", "entries:FINAL", "entries:GDC", "entries:PRE", "master", "shifts" }, dto!.Components.Keys.OrderBy(k => k));
        Assert.All(dto.Components.Values, v => Assert.Equal("ok", v));
    }

    [Fact]
    public async Task Readiness_reports_an_unreachable_entry_database_with_503()
    {
        var bad = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(F.CncCs) { InitialCatalog = "QR_DOES_NOT_EXIST", ConnectTimeout = 3 }.ConnectionString;
        await using var factory = F.CreateFactory(new Dictionary<string, string?> { ["ConnectionStrings:CncEntries"] = bad });
        var r = await factory.CreateClient().GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var dto = await r.Content.ReadFromJsonAsync<ReadinessDto>();
        Assert.Equal("unavailable", dto!.Components["entries:CNC"]);
        Assert.Equal("ok", dto.Components["master"]);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/health")).StatusCode);   // liveness unaffected
    }
}
