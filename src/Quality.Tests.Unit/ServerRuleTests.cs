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

    [Fact]
    public void Five_failures_from_one_client_lock_that_login_on_that_client_only()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 5; i++) { Assert.Null(th.RetryAfter("10.0.0.5", "e1001")); th.RecordFailure("10.0.0.5", "e1001"); }
        Assert.NotNull(th.RetryAfter("10.0.0.5", "E1001"));            // case-insensitive login
        Assert.Null(th.RetryAfter("10.0.0.5", "e2002"));               // other user, same tablet: fine
        Assert.Null(th.RetryAfter("10.0.0.9", "e1001"));               // same user, other tablet: fine (below per-login limit)
    }

    [Fact]
    public void Per_login_limit_stops_a_distributed_guess_and_expires()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 10; i++) th.RecordFailure($"10.0.1.{i}", "victim");
        Assert.NotNull(th.RetryAfter("10.0.2.1", "victim"));
        _t.Now += LoginThrottle.PerLogin.Window;
        Assert.Null(th.RetryAfter("10.0.2.1", "victim"));
    }

    [Fact]
    public void Per_client_limit_is_generous_for_shared_plant_addresses()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 29; i++) th.RecordFailure("plant-nat", "user" + i);
        Assert.Null(th.RetryAfter("plant-nat", "someone-else"));
        th.RecordFailure("plant-nat", "user29");
        Assert.NotNull(th.RetryAfter("plant-nat", "someone-else"));
    }

    [Fact]
    public void Success_clears_that_logins_counters()
    {
        var th = new LoginThrottle(_t);
        for (var i = 0; i < 4; i++) th.RecordFailure("c", "u");
        th.RecordSuccess("c", "u");
        for (var i = 0; i < 4; i++) th.RecordFailure("c", "u");
        Assert.Null(th.RetryAfter("c", "u"));
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
