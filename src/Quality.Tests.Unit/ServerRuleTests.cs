using Quality.Api.Infrastructure;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Entities;
using Quality.Core.Services;

namespace Quality.Tests.Unit;

public sealed class ManualTime(DateTime start) : TimeProvider
{
    public DateTime Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

public sealed class LoginThrottleTests
{
    private readonly ManualTime _t = new(new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc));
    private static void Fail(LoginThrottle th, string c, string l) => Assert.Null(th.TryBegin(c, l));   // attempt allowed and counted

    [Fact]
    public void Five_failures_from_one_client_lock_that_login_on_that_client_only()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 5; i++) Fail(th, "10.0.0.5", "e1001");
        Assert.NotNull(th.TryBegin("10.0.0.5", "E1001"));            // case-insensitive login
        Assert.Null(th.RetryAfter("10.0.0.5", "e2002"));               // other user, same tablet: fine
        Assert.Null(th.RetryAfter("10.0.0.9", "e1001"));               // same user, other tablet: fine (below per-login limit)
    }

    [Fact]
    public void Per_login_limit_stops_a_distributed_guess_from_unknown_clients_and_expires()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 10; i++) Fail(th, $"10.0.1.{i}", "victim");
        Assert.NotNull(th.TryBegin("10.0.2.1", "victim"));
        _t.Now += LoginThrottle.PerLogin.Window;
        Assert.Null(th.RetryAfter("10.0.2.1", "victim"));
    }

    /// <summary>Full review #1: an attacker's failures must not lock the real user out of their usual device.</summary>
    [Fact]
    public void Attackers_cannot_lock_the_real_user_out_of_a_known_device()
    {
        var th = new LoginThrottle(_t);
        Assert.Null(th.TryBegin("tablet-7", "admin")); th.RecordSuccess("tablet-7", "admin");   // admin signed in here before
        _t.Now += TimeSpan.FromHours(1);
        for (var i = 0; i < 5; i++) Fail(th, "attacker-1", "admin");
        _t.Now += TimeSpan.FromMinutes(5);
        for (var i = 0; i < 5; i++) Fail(th, "attacker-2", "admin");                            // 10 failures: per-login bucket full
        Assert.NotNull(th.TryBegin("new-pc", "admin"));                                          // unknown device: limited
        Assert.Null(th.TryBegin("tablet-7", "admin"));                                           // usual device: still signs in
    }

    /// <summary>Full review #2: the check and the count are one step, so a parallel burst cannot slip past the limit.</summary>
    [Fact]
    public void Parallel_burst_cannot_exceed_the_per_client_and_login_limit()
    {
        var th = new LoginThrottle(_t);
        var allowed = 0;
        Parallel.For(0, 200, _ => { if (th.TryBegin("10.0.0.5", "e1001") is null) Interlocked.Increment(ref allowed); });
        Assert.Equal(LoginThrottle.PerClientAndLogin.MaxFailures, allowed);
    }

    [Fact]
    public void Per_client_limit_is_generous_for_shared_plant_addresses()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 29; i++) Fail(th, "plant-nat", "user" + i);
        Assert.Null(th.RetryAfter("plant-nat", "someone-else"));
        Fail(th, "plant-nat", "user29");
        Assert.NotNull(th.RetryAfter("plant-nat", "someone-else"));
    }

    [Fact]
    public void Success_and_refund_give_attempts_back()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 4; i++) Fail(th, "c", "u");
        Assert.Null(th.TryBegin("c", "u")); th.RecordSuccess("c", "u");
        for (var i = 0; i < 4; i++) Fail(th, "c", "u");
        Assert.Null(th.TryBegin("c", "u")); th.Refund("c", "u");   // e.g. empty password: never checked
        Assert.Null(th.RetryAfter("c", "u"));
    }

    [Fact]
    public void Long_login_names_share_one_bucket()
    {
        var th = new LoginThrottle(_t);
        var prefix = new string('a', 50);
        for (var i = 0; i < 5; i++) Fail(th, "c", prefix + i);
        Assert.NotNull(th.TryBegin("c", prefix + "zzz"));
    }
}

public sealed class RuleTests
{
    [Theory]
    [InlineData("E1", true)] [InlineData("A", false)] [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ1234", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ12345", false)] [InlineData("E 1", false)] [InlineData("E.1_2-3", true)]
    public void Q6_employee_code_is_2_to_30_characters(string code, bool ok) => Assert.Equal(ok, LoginRules.EmployeeCodeProblem(code) is null);

    [Fact]
    public void Q5_username_and_employee_code_share_one_normalized_namespace()
    {
        var both = LoginRules.IdentifiersFor(new User { Id = 1, Username = "Ravi", EmployeeCode = "ravi " });
        Assert.Equal("RAVI", Assert.Single(both).NormalizedIdentifier);
        Assert.Equal("B", both[0].Kind);
        var two = LoginRules.IdentifiersFor(new User { Id = 2, Username = "ravi.k", EmployeeCode = "E1001" });
        Assert.Equal(new[] { "RAVI.K|U", "E1001|E" }, two.Select(x => $"{x.NormalizedIdentifier}|{x.Kind}"));
    }

    [Theory]
    [InlineData(null, null)] [InlineData("   ", null)] [InlineData(" a  b ", "a  b")]
    public void Q7_remarks_cleanup_used_for_save_and_replay(string? input, string? expected) => Assert.Equal(expected, RejectionEntryService.CleanRemarks(input));

    [Fact]
    public void Date_filters_are_normalised_to_utc()
    {
        var local = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(5.5)).LocalDateTime;   // as model binding produces for "...+05:30"
        Assert.Equal(new DateTime(2026, 11, 26, 4, 30, 0, DateTimeKind.Utc), RejectionEntryService.ToUtc(local));
        var unspecified = new DateTime(2026, 11, 26, 4, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(DateTimeKind.Utc, RejectionEntryService.ToUtc(unspecified)!.Value.Kind);
        Assert.Null(RejectionEntryService.ToUtc(null));
    }

    [Fact]
    public void Row_versions_round_trip_and_garbage_never_matches()
    {
        var v = new byte[] { 0, 0, 0, 0, 0, 0, 7, 209 };
        Assert.True(RowVersions.Matches(v, RowVersions.Decode(RowVersions.Encode(v))!));
        Assert.Null(RowVersions.Decode(null));
        Assert.False(RowVersions.Matches(v, RowVersions.Decode("not base64 !!")!));
    }

    [Fact]
    public void Text_normalizer_matches_the_import_rule()
    {
        Assert.Equal("LUG HEIGHT O/S /UNCLEAN", TextNormalizer.NormalizeName("  LUG HEIGHT  O/S /UNCLEAN "));
        Assert.Equal("DENT", TextNormalizer.NormalizeName("Dent"));
    }

    [Fact]
    public void Error_mapping_does_not_report_conflicts_as_database_unavailable()
    {
        Assert.Equal(409, ErrorHandling.Classify(new UniqueViolationException("UQ_x")).Status);
        Assert.Equal(409, ErrorHandling.Classify(new ConcurrencyConflictException()).Status);
        Assert.Equal(503, ErrorHandling.Classify(new TimeoutException()).Status);
        Assert.Equal(500, ErrorHandling.Classify(new InvalidOperationException("other")).Status);
        Assert.Equal(503, ErrorHandling.Classify(new InvalidOperationException("No connection string configured for entry store 'X'.")).Status);
    }
}
