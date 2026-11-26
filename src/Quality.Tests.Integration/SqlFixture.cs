using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Quality.Core.Dtos;
using Quality.Core.Services;

[assembly: AssemblyFixture(typeof(Quality.Tests.Integration.SqlFixture))]
[assembly: CaptureConsole]

namespace Quality.Tests.Integration;

/// <summary>
/// Builds three fresh databases on the SQL Server named by QR_TEST_SQL by running the db/ scripts in the documented
/// fresh-install order (the same order as tools/create-test-databases.sh), then hosts the real API in-process against them.
/// READ_COMMITTED_SNAPSHOT is ON (the harder case for the locking fixes) unless QR_TEST_RCSI=OFF.
/// The databases are dropped afterwards unless QR_TEST_KEEP=1.
/// </summary>
public sealed class SqlFixture : IAsyncLifetime
{
    public static readonly string? Server = Environment.GetEnvironmentVariable("QR_TEST_SQL");
    public const string AdminUser = "boot.admin", AdminEmp = "ADM001", AdminPassword = "Bootstrap-Pass-2026!";

    public string Prefix { get; } = "QRIT_" + DateTime.UtcNow.ToString("MMddHHmmss");
    public string MasterCs => Db("Master");
    public string GdcCs => Db("Gdc");
    public string CncCs => Db("Cnc");
    public bool Rcsi { get; } = !string.Equals(Environment.GetEnvironmentVariable("QR_TEST_RCSI"), "OFF", StringComparison.OrdinalIgnoreCase);
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public static string DbDir => Path.Combine(AppContext.BaseDirectory, "db");

    public string Db(string suffix) => new SqlConnectionStringBuilder(Server) { InitialCatalog = $"{Prefix}_{suffix}", MultipleActiveResultSets = false, Pooling = true }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (Server is null) return;
        await using (var c = new SqlConnection(Server))
        {
            await c.OpenAsync();
            foreach (var d in new[] { "Master", "Gdc", "Cnc" })
                await Exec(c, $"CREATE DATABASE [{Prefix}_{d}]; ALTER DATABASE [{Prefix}_{d}] SET READ_COMMITTED_SNAPSHOT {(Rcsi ? "ON" : "OFF")};");
        }
        await RunScripts(MasterCs, "01_master_schema", "02c_entries_schema_PRE_FINAL", "03_seed_reference", "04_seed_phenomena",
            "05_DEV_ONLY_placeholder_machines",   // test database only: the workflow needs machines
            "08_upgrade_master_review_fixes", "09_upgrade_entries_indexes", "11_upgrade_master_shifts_corrections", "12_upgrade_entries_shift_reversal", "13_upgrade_master_production_counts");
        await RunScripts(GdcCs, "02a_entries_schema_GDC_AndonDB", "09_upgrade_entries_indexes", "12_upgrade_entries_shift_reversal");
        await RunScripts(CncCs, "02b_entries_schema_MachineShopDB", "09_upgrade_entries_indexes", "12_upgrade_entries_shift_reversal");

        Factory = CreateFactory(new Dictionary<string, string?>());
        _ = Factory.Services;   // starts the host (and its background bootstrap-administrator service) now, not on first request
        for (var i = 0; i < 300 && await Scalar<int>(MasterCs, "SELECT COUNT(*) FROM qa.LoginIdentifiers") == 0; i++) await Task.Delay(100);
        if (await Scalar<int>(MasterCs, "SELECT COUNT(*) FROM qa.Users") == 0) throw new InvalidOperationException("Bootstrap administrator was not created.");
    }

    public WebApplicationFactory<Program> CreateFactory(IDictionary<string, string?> overrides) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Master"] = MasterCs, ["ConnectionStrings:GdcEntries"] = GdcCs, ["ConnectionStrings:CncEntries"] = CncCs,
                ["ConnectionStrings:PreEntries"] = MasterCs, ["ConnectionStrings:FinalEntries"] = MasterCs,
                ["Jwt:Key"] = "integration-test-signing-key-0123456789-abcdefghij", ["Jwt:Issuer"] = "QualityRejectionApi",
                ["Jwt:Audience"] = "QualityRejectionApp", ["Jwt:ExpiryMinutes"] = "480",
                ["LoginThrottle:RequestsPerMinutePerClient"] = "100000",   // the whole suite runs from one test client address
                ["Bootstrap:AdminEmployeeCode"] = AdminEmp, ["Bootstrap:AdminUsername"] = AdminUser, ["Bootstrap:AdminPassword"] = AdminPassword,
            };
            foreach (var (k, v) in overrides) settings[k] = v;
            foreach (var (k, v) in settings) b.UseSetting(k, v);   // UseSetting: visible to Program's top-level configuration reads
            b.UseEnvironment("Testing");
        });

    public async ValueTask DisposeAsync()
    {
        if (Server is null) return;
        if (Factory is not null) await Factory.DisposeAsync();
        if (Environment.GetEnvironmentVariable("QR_TEST_KEEP") == "1") return;
        SqlConnection.ClearAllPools();
        await using var c = new SqlConnection(Server);
        await c.OpenAsync();
        foreach (var d in new[] { "Master", "Gdc", "Cnc" })
            await Exec(c, $"IF DB_ID('{Prefix}_{d}') IS NOT NULL BEGIN ALTER DATABASE [{Prefix}_{d}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Prefix}_{d}]; END");
    }

    // ---------- SQL helpers ----------
    private static readonly Regex Go = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static async Task RunScripts(string cs, params string[] names)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        foreach (var n in names)
            foreach (var batch in Go.Split(await File.ReadAllTextAsync(Path.Combine(DbDir, n + ".sql"))).Where(b => b.Trim().Length > 0))
                await Exec(c, batch);
    }

    public static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(string cs, string sql, params (string, object)[] ps)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (n, v) in ps)
        {
            var prm = cmd.Parameters.AddWithValue(n, v);
            if (v is DateTime) prm.SqlDbType = System.Data.SqlDbType.DateTime2;   // AddWithValue would send legacy datetime (rounds ms)
        }
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public static async Task<List<object[]>> Rows(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object[]>();
        while (await r.ReadAsync()) { var row = new object[r.FieldCount]; r.GetValues(row); list.Add(row); }
        return list;
    }

    // ---------- API helpers ----------
    public HttpClient Client(string? token = null)
    {
        var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<string> LoginAsync(string login, string password)
    {
        var r = await Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    public Task<string> AdminTokenAsync() => LoginAsync(AdminUser, AdminPassword);

    private int _seq;
    /// <summary>Creates a user through the real admin API. Returns its id, username and password.</summary>
    public async Task<(int Id, string Username, string Password)> CreateUserAsync(string role, params string[] shopCodes)
    {
        var n = Interlocked.Increment(ref _seq);
        var uname = $"{role[..3].ToLowerInvariant()}.{Prefix[^6..]}.{n}"; var emp = $"E{Prefix[^6..]}{n:000}"; const string pw = "Test-Password-123";
        var admin = Client(await AdminTokenAsync());
        var shops = await ShopsAsync();
        var r = await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(emp, uname, "Test " + uname, role, pw, shopCodes.Select(c => shops[c].Id).ToList()));
        r.EnsureSuccessStatusCode();
        return ((await r.Content.ReadFromJsonAsync<AdminUserDto>())!.Id, uname, pw);
    }

    public async Task<Dictionary<string, ShopDto>> ShopsAsync()
    {
        var admin = Client(await AdminTokenAsync());
        return (await admin.GetFromJsonAsync<List<ShopDto>>("/api/shops"))!.ToDictionary(s => s.ShopCode);
    }

    /// <summary>A service from a fresh DI scope (own DbContexts), as a separate HTTP request would get.</summary>
    public (IServiceScope Scope, T Service) Scoped<T>() where T : notnull
    {
        var scope = Factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<T>());
    }

    public static CurrentUser Actor(int id, string role, string emp = "X") => new(id, emp, role);
}

/// <summary>Base class: skips every test when no SQL Server is configured.</summary>
public abstract class SqlTest
{
    protected SqlFixture F { get; }
    protected SqlTest(SqlFixture f)
    {
        Assert.SkipWhen(SqlFixture.Server is null, "QR_TEST_SQL is not set: no SQL Server available for integration tests.");
        F = f;
    }
}
