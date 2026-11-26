using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Services;

namespace Quality.Tests.Integration;

/// <summary>Races from the review, run against SQL Server with READ_COMMITTED_SNAPSHOT ON. Each racer uses its own DI scope (own connection).</summary>
public sealed class ConcurrencyTests(SqlFixture f) : SqlTest(f)
{
    // ---------------- Q5 ----------------
    [Fact]
    public async Task Q5_concurrent_cross_column_collision_creates_only_one_user()
    {
        var shops = await F.ShopsAsync();
        var adminId = await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT Id FROM qa.Users WHERE Username = '{SqlFixture.AdminUser}'");
        var admin = SqlFixture.Actor(adminId, RoleNames.Administrator);
        for (var round = 0; round < 10; round++)
        {
            var x = $"Q5R{round}{F.Prefix[^4..]}";
            // User 1: employee code X. User 2: username X. Login accepts either, so only one may exist.
            var (s1, svc1) = F.Scoped<UserAdminService>(); var (s2, svc2) = F.Scoped<UserAdminService>();
            using (s1) using (s2)
            {
                var gate = new Barrier(2);
                var r1 = Task.Run(async () => { gate.SignalAndWait(); return await svc1.CreateAsync(admin, new CreateUserRequest(x, x + "u", "One", RoleNames.QualityInspector, "Some-Password-1", new[] { shops["GDC"].Id })); });
                var r2 = Task.Run(async () => { gate.SignalAndWait(); return await svc2.CreateAsync(admin, new CreateUserRequest(x + "e", x.ToLowerInvariant(), "Two", RoleNames.QualityInspector, "Some-Password-1", new[] { shops["GDC"].Id })); });
                var results = await Task.WhenAll(r1, r2);
                Assert.Equal(1, results.Count(r => r.IsSuccess));
                Assert.Equal("duplicate_user", Assert.Single(results, r => !r.IsSuccess).Error!.Code);
            }
            Assert.Equal(1, await SqlFixture.Scalar<int>(F.MasterCs, "SELECT COUNT(*) FROM qa.Users WHERE UPPER(EmployeeCode) = UPPER(@x) OR UPPER(Username) = UPPER(@x)", ("@x", x)));
        }
    }

    // ---------------- Q8 ----------------
    [Fact]
    public async Task Q8_concurrent_phenomenon_creates_get_distinct_codes()
    {
        var shops = await F.ShopsAsync();
        var adminId = await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT Id FROM qa.Users WHERE Username = '{SqlFixture.AdminUser}'");
        var stageId = await SqlFixture.Scalar<int>(F.MasterCs, "SELECT Id FROM qa.InspectionStages WHERE StageCode = 'FINAL'");
        const int n = 12;
        var scopes = Enumerable.Range(0, n).Select(_ => F.Scoped<PhenomenaAdminService>()).ToList();
        try
        {
            var gate = new Barrier(n);
            var tasks = scopes.Select((s, i) => Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await s.Service.CreateAsync(SqlFixture.Actor(adminId, RoleNames.Administrator), new CreatePhenomenonRequest(shops["FINAL"].Id, stageId, $"Q8 concurrent {F.Prefix} #{i}", true));
            })).ToList();
            var results = await Task.WhenAll(tasks);
            Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Code));
            var codes = results.Select(r => r.Value!.PhenomenonCode).ToList();
            Assert.Equal(n, codes.Distinct().Count());
            Assert.All(codes, c => Assert.StartsWith("FINAL-", c));
        }
        finally { scopes.ForEach(s => s.Scope.Dispose()); }
    }

    [Fact]
    public async Task Q8_two_concurrent_creates_of_the_same_name_give_one_success_and_one_duplicate()
    {
        var shops = await F.ShopsAsync();
        var adminId = await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT Id FROM qa.Users WHERE Username = '{SqlFixture.AdminUser}'");
        var stageId = await SqlFixture.Scalar<int>(F.MasterCs, "SELECT Id FROM qa.InspectionStages WHERE StageCode = 'PRE_TREATMENT'");
        var (s1, a) = F.Scoped<PhenomenaAdminService>(); var (s2, b) = F.Scoped<PhenomenaAdminService>();
        using (s1) using (s2)
        {
            var gate = new Barrier(2);
            var name = $"Q8 same {F.Prefix}";
            var r = await Task.WhenAll(
                Task.Run(async () => { gate.SignalAndWait(); return await a.CreateAsync(SqlFixture.Actor(adminId, RoleNames.Administrator), new CreatePhenomenonRequest(shops["PRE"].Id, stageId, name, true)); }),
                Task.Run(async () => { gate.SignalAndWait(); return await b.CreateAsync(SqlFixture.Actor(adminId, RoleNames.Administrator), new CreatePhenomenonRequest(shops["PRE"].Id, stageId, name.ToUpperInvariant() + "  ", true)); }));
            Assert.Equal(1, r.Count(x => x.IsSuccess));
            Assert.Equal("duplicate_name", Assert.Single(r, x => !x.IsSuccess).Error!.Code);
        }
    }

    // ---------------- Q9 ----------------
    /// <summary>
    /// Deterministic interleaving of the race from the review: an entry save has validated the machine (and holds its lock),
    /// then an administrator deletes the machine. The delete must WAIT, then see the committed entry and refuse.
    /// </summary>
    [Fact]
    public async Task Q9_delete_waits_for_an_in_flight_entry_save_and_then_refuses()
    {
        var shops = await F.ShopsAsync();
        var adminId = await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT Id FROM qa.Users WHERE Username = '{SqlFixture.AdminUser}'");
        var machineId = await SqlFixture.Scalar<int>(F.MasterCs, $"INSERT INTO qa.Machines (MachineCode, MachineName, ShopId) OUTPUT INSERTED.Id VALUES ('Q9-{F.Prefix[^6..]}', 'Q9 race', {shops["CNC"].Id})");
        var phenId = await SqlFixture.Scalar<int>(F.MasterCs, "SELECT MIN(p.Id) FROM qa.RejectionPhenomena p JOIN qa.InspectionStages s ON s.Id = p.InspectionStageId WHERE s.StageCode = 'CNC_SHOP'");

        var (entryScope, master) = F.Scoped<IMasterData>();
        var router = entryScope.ServiceProvider.GetRequiredService<IEntryStoreRouter>();
        var (delScope, machines) = F.Scoped<MachineAdminService>();
        using (entryScope) using (delScope)
        {
            var validated = new TaskCompletionSource(); var mayCommit = new TaskCompletionSource();
            var save = master.InTransactionAsync(async () =>
            {
                var (m, p) = await master.ReadMastersForEntryLockedAsync(machineId, phenId);   // the entry save's check
                Assert.True(m!.IsActive);
                validated.SetResult();
                await mayCommit.Task;                                                            // ...delete starts here...
                await router.For("CNC").InsertAsync(new Quality.Core.Entities.QualityRejectionEntry
                {
                    SubmissionId = Guid.NewGuid(), ShopId = shops["CNC"].Id, ShopCode = "CNC", InspectionStageId = p!.InspectionStageId, StageCode = "CNC_SHOP", StageName = "CNC Shop",
                    MachineId = m.Id, MachineCode = m.MachineCode, MachineName = m.MachineName, RejectionPhenomenonId = p.Id, PhenomenonNameSnapshot = p.PhenomenonName,
                    Quantity = 1, InspectorId = adminId, InspectorEmployeeCode = "T", ServerCreatedAt = DateTime.UtcNow,
                });
            });
            await validated.Task;

            var delete = Task.Run(() => machines.DeleteAsync(SqlFixture.Actor(adminId, RoleNames.Administrator), machineId));
            await Task.Delay(1500);
            Assert.False(delete.IsCompleted, "Delete must wait for the in-flight entry save (before the fix it checked usage immediately and deleted).");

            mayCommit.SetResult();
            await save;
            var result = await delete;
            Assert.False(result.IsSuccess);
            Assert.Equal("machine_in_use", result.Error!.Code);
        }
        Assert.Equal(1, await SqlFixture.Scalar<int>(F.MasterCs, $"SELECT COUNT(*) FROM qa.Machines WHERE Id = {machineId}"));
    }

    /// <summary>Randomised version through the real API: whatever wins, no committed entry may reference a deleted machine.</summary>
    [Fact]
    public async Task Q9_random_races_never_leave_an_entry_pointing_at_a_deleted_machine()
    {
        var shops = await F.ShopsAsync();
        var (_, insp, pw) = await F.CreateUserAsync("QualityInspector", "GDC");
        var inspector = F.Client(await F.LoginAsync(insp, pw));
        var admin = F.Client(await F.AdminTokenAsync());
        var stage = (await inspector.GetFromJsonAsync<List<StageDto>>($"/api/shops/{shops["GDC"].Id}/stages"))!.Single();
        var phen = (await inspector.GetFromJsonAsync<List<PhenomenonDto>>($"/api/shops/{shops["GDC"].Id}/stages/{stage.Id}/phenomena"))!.First();
        int created = 0, deleted = 0;
        for (var i = 0; i < 15; i++)
        {
            var m = await (await admin.PostAsJsonAsync("/api/admin/machines", new CreateMachineRequest(shops["GDC"].Id, $"Q9R-{F.Prefix[^5..]}-{i}", "race", true))).Content.ReadFromJsonAsync<AdminMachineDto>();
            var post = inspector.PostAsJsonAsync("/api/rejection-entries", new CreateEntryRequest(Guid.NewGuid(), shops["GDC"].Id, stage.Id, m!.Id, phen.Id, 1, null));
            var del = admin.DeleteAsync($"/api/admin/machines/{m.Id}");
            var (p, d) = (await post, await del);
            if (p.StatusCode == HttpStatusCode.Created) created++;
            if (d.StatusCode == HttpStatusCode.NoContent) deleted++;
            Assert.False(p.StatusCode == HttpStatusCode.Created && d.StatusCode == HttpStatusCode.NoContent, $"round {i}: entry saved AND machine deleted");
        }
        var orphans = await SqlFixture.Scalar<int>(F.GdcCs, $"""
            SELECT COUNT(*) FROM qa.QualityRejectionEntries e
            WHERE e.MachineCode LIKE 'Q9R-{F.Prefix[^5..]}-%'   -- only this race's machines (other tests insert synthetic rows)
              AND NOT EXISTS (SELECT 1 FROM [{F.Prefix}_Master].qa.Machines m WHERE m.Id = e.MachineId)
            """);
        Assert.Equal(0, orphans);
        Assert.Equal(15, created + deleted);   // every round: exactly one side won
    }
}

[CollectionDefinition("AdminState", DisableParallelization = true)]
public sealed class AdminStateCollection;

/// <summary>Q3 tests temporarily change which administrators are active, so they run alone (after the parallel tests).</summary>
[Collection("AdminState")]
public sealed class LastAdministratorTests(SqlFixture f) : SqlTest(f)
{
    [Fact]
    public async Task Q3_two_admins_deactivating_each_other_at_once_leave_one_active_admin()
    {
        try { await Rounds(); }
        finally
        {
            // Always restore the bootstrap admin for the other tests, even when an assertion failed mid-round.
            await SqlFixture.Scalar<int>(F.MasterCs, $"UPDATE qa.Users SET IsActive = 1 WHERE Username = '{SqlFixture.AdminUser}'; SELECT 1");
        }
    }

    private async Task Rounds()
    {
        for (var round = 0; round < 5; round++)
        {
            // Exactly two active administrators for this round: A and B (everyone else is demoted out of the way).
            var (a, _, _) = await F.CreateUserAsync("Administrator");
            var (b, _, _) = await F.CreateUserAsync("Administrator");
            await SqlFixture.Scalar<int>(F.MasterCs, $"UPDATE qa.Users SET IsActive = 0 WHERE RoleId = 1 AND Id NOT IN ({a},{b}); SELECT 1");

            var (s1, svc1) = F.Scoped<UserAdminService>(); var (s2, svc2) = F.Scoped<UserAdminService>();
            using (s1) using (s2)
            {
                var gate = new Barrier(2);
                var t1 = Task.Run(async () => { gate.SignalAndWait(); return await svc1.SetStatusAsync(SqlFixture.Actor(a, RoleNames.Administrator), b, false); });
                var t2 = Task.Run(async () => { gate.SignalAndWait(); return await svc2.SetStatusAsync(SqlFixture.Actor(b, RoleNames.Administrator), a, false); });
                var results = await Task.WhenAll(t1, t2);

                Assert.Equal(1, results.Count(r => r.IsSuccess));
                Assert.Equal("last_admin", Assert.Single(results, r => !r.IsSuccess).Error!.Code);
            }
            Assert.Equal(1, await SqlFixture.Scalar<int>(F.MasterCs, "SELECT COUNT(*) FROM qa.Users WHERE RoleId = 1 AND IsActive = 1"));
            // Restore the bootstrap admin for the other tests.
            await SqlFixture.Scalar<int>(F.MasterCs, $"UPDATE qa.Users SET IsActive = 1 WHERE Username = '{SqlFixture.AdminUser}'; SELECT 1");
        }
    }

    [Fact]
    public async Task Q3_database_trigger_refuses_removing_the_last_admin_by_direct_sql()
    {
        // Demote every administrator except one inside a transaction, then try to deactivate that one: the trigger must refuse.
        await using var c = new SqlConnection(F.MasterCs);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await new SqlCommand("UPDATE qa.Users SET IsActive = 0 WHERE RoleId = 1 AND Username <> @u", c, tx) { Parameters = { new("@u", SqlFixture.AdminUser) } }.ExecuteNonQueryAsync();
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            new SqlCommand("UPDATE qa.Users SET IsActive = 0 WHERE Username = @u", c, tx) { Parameters = { new("@u", SqlFixture.AdminUser) } }.ExecuteNonQueryAsync());
        Assert.Equal(50090, ex.Number);
        await tx.RollbackAsync();
    }

}
