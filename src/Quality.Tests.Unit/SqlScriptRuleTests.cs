using System.Text.RegularExpressions;

namespace Quality.Tests.Unit;

/// <summary>
/// Static checks on db/*.sql. sqlcmd (the documented way to run them) starts with QUOTED_IDENTIFIER OFF, and SQL Server refuses
/// filtered indexes then; the in-process integration tests use SqlClient (ON) and would not notice. Found in round 2 (db/11).
/// </summary>
public sealed class SqlScriptRuleTests
{
    private static string DbDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "db"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new DirectoryNotFoundException("db folder not found"), "db");
    }

    public static TheoryData<string> Scripts() => new(Directory.GetFiles(DbDir(), "*.sql").Select(Path.GetFileName).OrderBy(x => x)!);

    [Theory, MemberData(nameof(Scripts))]
    public void Scripts_with_filtered_indexes_set_quoted_identifier_on(string script)
    {
        var sql = File.ReadAllText(Path.Combine(DbDir(), script));
        var filtered = Regex.IsMatch(sql, @"CREATE\s+(UNIQUE\s+)?(NONCLUSTERED\s+)?INDEX[^;]*?\)\s*WHERE\s", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (filtered) Assert.Matches(new Regex(@"SET\s+QUOTED_IDENTIFIER\s+ON", RegexOptions.IgnoreCase), sql);
    }

    [Theory, MemberData(nameof(Scripts))]
    public void Every_script_stops_on_error_inside_transactions(string script)
    {
        var sql = File.ReadAllText(Path.Combine(DbDir(), script));
        if (Regex.IsMatch(sql, @"BEGIN\s+TRAN", RegexOptions.IgnoreCase)) Assert.Contains("XACT_ABORT ON", sql, StringComparison.OrdinalIgnoreCase);
    }
}
