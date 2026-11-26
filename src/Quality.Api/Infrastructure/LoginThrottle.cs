using System.Collections.Concurrent;

namespace Quality.Api.Infrastructure;

/// <summary>
/// Review Q4: replaces the single server-wide login quota (10/minute for everybody) with FAILED-attempt counters per
/// (client address + login name), per login name and per client address. One abusive client can only lock itself out,
/// and a mistyped password on one tablet does not stop other people signing in.
/// </summary>
/// <remarks>
/// Plant tablets may share one network address (NAT), so the per-address limit is deliberately generous.
/// A successful sign-in clears that login's counters. The counters live in memory: they reset when the API restarts and
/// are per server instance (the documented deployment is a single instance). Behind a reverse proxy, configure
/// forwarded headers (see docs/DEPLOYMENT.md) or every client shares the proxy's address.
/// </remarks>
public sealed class LoginThrottle
{
    public sealed record Limit(int MaxFailures, TimeSpan Window);

    public static readonly Limit PerClientAndLogin = new(5, TimeSpan.FromMinutes(5));
    public static readonly Limit PerLogin = new(10, TimeSpan.FromMinutes(15));
    public static readonly Limit PerClient = new(30, TimeSpan.FromMinutes(15));

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new();
    private readonly TimeProvider _time;
    public LoginThrottle(TimeProvider time) => _time = time;

    private static IEnumerable<(string Key, Limit Limit)> Keys(string client, string login)
    {
        var l = login.Trim().ToUpperInvariant();
        yield return ($"cl:{client}|{l}", PerClientAndLogin);
        yield return ($"l:{l}", PerLogin);
        yield return ($"c:{client}", PerClient);
    }

    /// <summary>Returns how long the caller must wait, or null if the attempt may go ahead.</summary>
    public TimeSpan? RetryAfter(string client, string login)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        TimeSpan? wait = null;
        foreach (var (key, limit) in Keys(client, login))
        {
            if (!_failures.TryGetValue(key, out var q)) continue;
            lock (q)
            {
                Trim(q, now, limit.Window);
                if (q.Count >= limit.MaxFailures)
                {
                    var until = q.Peek() + limit.Window - now;   // oldest counted failure leaves the window
                    if (wait is null || until > wait) wait = until;
                }
            }
        }
        return wait is { } w && w < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    public void RecordFailure(string client, string login)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (key, limit) in Keys(client, login))
        {
            var q = _failures.GetOrAdd(key, _ => new Queue<DateTime>());
            lock (q) { Trim(q, now, limit.Window); q.Enqueue(now); }
        }
        if (_failures.Count > 50_000) Prune(now);
    }

    public void RecordSuccess(string client, string login)
    {
        var (clientLogin, _) = Keys(client, login).First();
        _failures.TryRemove(clientLogin, out _);
        _failures.TryRemove($"l:{login.Trim().ToUpperInvariant()}", out _);
    }

    private static void Trim(Queue<DateTime> q, DateTime now, TimeSpan window)
    {
        while (q.Count > 0 && q.Peek() <= now - window) q.Dequeue();
    }

    private void Prune(DateTime now)
    {
        var longest = new[] { PerClientAndLogin.Window, PerLogin.Window, PerClient.Window }.Max();
        foreach (var (key, q) in _failures)
            lock (q) { if (q.Count == 0 || q.Last() <= now - longest) _failures.TryRemove(key, out _); }
    }
}
