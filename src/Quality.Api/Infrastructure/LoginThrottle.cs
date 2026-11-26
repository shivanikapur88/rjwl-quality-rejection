namespace Quality.Api.Infrastructure;

/// <summary>
/// Review Q4: replaces the single server-wide login quota with FAILED-attempt counters per (client address + login name),
/// per login name and per client address.
/// </summary>
/// <remarks>
/// Full review (Sonnet) fixes:
/// * #1 lock-out: the per-LOGIN bucket used to refuse everybody, so an anonymous caller could lock out any account (even the only
///   administrator) with ~10 requests per 15 minutes. It now applies only to clients that have NOT signed in successfully as that
///   login before ("known clients", remembered for 30 days). A plant tablet the user normally uses keeps working during such an
///   attack; an unknown address is still limited. The per-client and per-client+login buckets apply to everybody.
/// * #2 burst: checking and counting are one atomic step (<see cref="TryBegin"/>), so 200 parallel guesses can no longer all pass
///   the check before the first failure is recorded. A success or a request that never reached the password check gives the
///   reserved attempt back.
/// * Login names are truncated to 50 characters (the identifier column size) so suffixes cannot create endless fresh buckets.
/// Counters live in memory: they reset when the API restarts and are per server instance (single-instance deployment).
/// Behind a reverse proxy, configure ReverseProxy:KnownProxies or every client shares the proxy's address.
/// </remarks>
public sealed class LoginThrottle
{
    public sealed record Limit(int MaxFailures, TimeSpan Window);

    public static readonly Limit PerClientAndLogin = new(5, TimeSpan.FromMinutes(5));
    public static readonly Limit PerLogin = new(10, TimeSpan.FromMinutes(15));
    public static readonly Limit PerClient = new(30, TimeSpan.FromMinutes(15));
    public static readonly TimeSpan KnownClientFor = TimeSpan.FromDays(30);
    private const int MaxLoginKeyLength = 50, MaxEntries = 50_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTime>> _failures = new();
    private readonly Dictionary<string, DateTime> _known = new();   // "client|LOGIN" -> last successful sign-in
    private readonly TimeProvider _time;
    public LoginThrottle(TimeProvider time) => _time = time;

    private static string Login(string login)
    {
        var l = (login ?? "").Trim().ToUpperInvariant();
        return l.Length > MaxLoginKeyLength ? l[..MaxLoginKeyLength] : l;
    }

    private IEnumerable<(string Key, Limit Limit)> Buckets(string client, string login, bool knownClient)
    {
        var l = Login(login);
        yield return ($"cl:{client}|{l}", PerClientAndLogin);
        if (!knownClient) yield return ($"l:{l}", PerLogin);
        yield return ($"c:{client}", PerClient);
    }

    private bool IsKnown(string client, string login, DateTime now) =>
        _known.TryGetValue($"{client}|{Login(login)}", out var at) && now - at < KnownClientFor;

    /// <summary>
    /// Atomically checks the limits and, if the attempt may go ahead, reserves it as a failure in every bucket.
    /// Returns how long to wait when refused (nothing is reserved then).
    /// </summary>
    public TimeSpan? TryBegin(string client, string login)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var buckets = Buckets(client, login, IsKnown(client, login, now)).ToList();
            TimeSpan? wait = null;
            foreach (var (key, limit) in buckets)
            {
                if (!_failures.TryGetValue(key, out var q)) continue;
                Trim(q, now, limit.Window);
                if (q.Count >= limit.MaxFailures)
                {
                    var until = q.Peek() + limit.Window - now;
                    if (wait is null || until > wait) wait = until;
                }
            }
            if (wait is not null) return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            foreach (var (key, _) in buckets)
            {
                if (!_failures.TryGetValue(key, out var q)) _failures[key] = q = new Queue<DateTime>();
                q.Enqueue(now);
            }
            if (_failures.Count > MaxEntries || _known.Count > MaxEntries) Prune(now);
            return null;
        }
    }

    /// <summary>The attempt failed on the password: the reservation stands. (No-op; kept for readability at the call site.)</summary>
    public void RecordFailure(string client, string login) { }

    /// <summary>Signed in: clear this login's counters, give back the client reservation, remember the client as known.</summary>
    public void RecordSuccess(string client, string login)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var l = Login(login);
            _failures.Remove($"cl:{client}|{l}");
            _failures.Remove($"l:{l}");
            RemoveNewest($"c:{client}");
            _known[$"{client}|{l}"] = now;
        }
    }

    /// <summary>The request never reached the password check (e.g. empty fields): give the reservation back.</summary>
    public void Refund(string client, string login)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var (key, _) in Buckets(client, login, IsKnown(client, login, now))) RemoveNewest(key);
        }
    }

    private void RemoveNewest(string key)
    {
        if (!_failures.TryGetValue(key, out var q) || q.Count == 0) return;
        var items = q.ToArray();
        q.Clear();
        for (var i = 0; i < items.Length - 1; i++) q.Enqueue(items[i]);
    }

    /// <summary>Compatibility helper for callers/tests that only want to know the current wait (does not reserve).</summary>
    public TimeSpan? RetryAfter(string client, string login)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            TimeSpan? wait = null;
            foreach (var (key, limit) in Buckets(client, login, IsKnown(client, login, now)))
            {
                if (!_failures.TryGetValue(key, out var q)) continue;
                Trim(q, now, limit.Window);
                if (q.Count >= limit.MaxFailures) { var until = q.Peek() + limit.Window - now; if (wait is null || until > wait) wait = until; }
            }
            return wait is { } w && w < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        }
    }

    private static void Trim(Queue<DateTime> q, DateTime now, TimeSpan window)
    {
        while (q.Count > 0 && q.Peek() <= now - window) q.Dequeue();
    }

    private void Prune(DateTime now)
    {
        var longest = new[] { PerClientAndLogin.Window, PerLogin.Window, PerClient.Window }.Max();
        foreach (var key in _failures.Where(kv => kv.Value.Count == 0 || kv.Value.Last() <= now - longest).Select(kv => kv.Key).ToList()) _failures.Remove(key);
        foreach (var key in _known.Where(kv => now - kv.Value >= KnownClientFor).Select(kv => kv.Key).ToList()) _known.Remove(key);
    }
}
