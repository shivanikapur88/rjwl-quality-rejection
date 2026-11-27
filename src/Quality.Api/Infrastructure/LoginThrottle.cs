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
/// Second full review (Sonnet):
/// * Many tablets share one plant address, so 30 failures from that address (one bad tablet, or an insider) used to block
///   EVERY sign-in from the plant for up to 15 minutes. A login that already signed in successfully from this address
///   ("known") is now exempt from the per-address cap; it keeps its own 5-per-5-minutes limit.
/// * Keys are folded (Unicode compatibility form, accents removed) so "admín" and "admin", which an accent-insensitive
///   database collation treats as the same account, share one bucket.
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
    private const int MaxLoginKeyLength = 50;
    public const int MaxEntries = 50_000;
    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(30);
    private DateTime _lastPrune = DateTime.MinValue;
    /// <summary>Number of live keys (tests and diagnostics).</summary>
    public int KeyCount { get { lock (_gate) return _failures.Count; } }

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTime>> _failures = new();
    private readonly Dictionary<string, DateTime> _known = new();   // "client|LOGIN" -> last successful sign-in
    private readonly TimeProvider _time;
    public LoginThrottle(TimeProvider time) => _time = time;

    private static string Login(string login)
    {
        var l = (login ?? "").Trim();
        if (l.Length > MaxLoginKeyLength) l = l[..MaxLoginKeyLength];   // cut first: never upper-case a huge string
        var folded = new System.Text.StringBuilder(l.Length);
        foreach (var ch in l.Normalize(System.Text.NormalizationForm.FormKD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) folded.Append(ch);
        return folded.ToString().ToUpperInvariant();
    }

    private IEnumerable<(string Key, Limit Limit)> Buckets(string client, string login, bool knownClient)
    {
        var l = Login(login);
        yield return ($"cl:{client}|{l}", PerClientAndLogin);
        if (knownClient) yield break;                 // known login on this address: only its own client+login limit
        yield return ($"l:{l}", PerLogin);
        yield return ($"c:{client}", PerClient);
    }

    private bool IsKnown(string client, string login, DateTime now) =>
        _known.TryGetValue($"{client}|{Login(login)}", out var at) && now - at < KnownClientFor;

    /// <summary>
    /// Atomically checks the limits and, if the attempt may go ahead, reserves it as a failure in every bucket.
    /// Returns how long to wait when refused (nothing is reserved then).
    /// </summary>
    public TimeSpan? TryBegin(string client, string login) => TryBegin(client, login, out _);

    /// <param name="known">Whether the login counted as known on this address when the attempt began; pass it back to
    /// <see cref="RecordSuccess(string,string,bool)"/> / <see cref="Refund(string,string,bool)"/> so exactly the reserved buckets are settled.</param>
    public TimeSpan? TryBegin(string client, string login, out bool known)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            known = IsKnown(client, login, now);
            var buckets = Buckets(client, login, known).ToList();
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
            if ((_failures.Count > MaxEntries || _known.Count > MaxEntries) && now - _lastPrune > PruneEvery) Prune(now);
            return null;
        }
    }

    /// <summary>The attempt failed on the password: the reservation stands. (No-op; kept for readability at the call site.)</summary>
    public void RecordFailure(string client, string login) { }

    /// <summary>Signed in: clear this login's counters, give back the client reservation, remember the client as known.</summary>
    public void RecordSuccess(string client, string login) => RecordSuccess(client, login, IsKnownNow(client, login));

    public void RecordSuccess(string client, string login, bool knownAtBegin)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var l = Login(login);
            _failures.Remove($"cl:{client}|{l}");
            _failures.Remove($"l:{l}");
            if (!knownAtBegin) RemoveNewest($"c:{client}");   // a known login reserved nothing there
            _known[$"{client}|{l}"] = now;
        }
    }

    /// <summary>The request never reached the password check (e.g. empty fields): give the reservation back.</summary>
    public void Refund(string client, string login) => Refund(client, login, IsKnownNow(client, login));

    /// <summary>Gives back exactly the reservation made by <see cref="TryBegin(string,string,out bool)"/>.</summary>
    public void Refund(string client, string login, bool knownAtBegin)
    {
        lock (_gate)
        {
            foreach (var (key, _) in Buckets(client, login, knownAtBegin)) RemoveNewest(key);
        }
    }

    private bool IsKnownNow(string client, string login) { lock (_gate) return IsKnown(client, login, _time.GetUtcNow().UtcDateTime); }

    /// <summary>
    /// The address used for throttling: IPv4 as is; IPv6 by its /64 network, because one client controls a whole /64 and
    /// could otherwise rotate addresses to get fresh per-address buckets (third full review).
    /// </summary>
    public static string ClientKey(System.Net.IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        var b = address.GetAddressBytes();
        for (var i = 8; i < 16; i++) b[i] = 0;
        return new System.Net.IPAddress(b) + "/64";
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

    /// <summary>
    /// Removes expired keys. Runs at most every <see cref="PruneEvery"/> (not on every call once the table is large), and
    /// enforces a hard cap: if live keys still exceed it (a flood of random logins), the keys with the OLDEST last failure
    /// are dropped down to three quarters of the cap, so memory and the cost of a prune stay bounded (third full review).
    /// Known logins are capped the same way, oldest sign-in first.
    /// </summary>
    private void Prune(DateTime now)
    {
        _lastPrune = now;
        var longest = new[] { PerClientAndLogin.Window, PerLogin.Window, PerClient.Window }.Max();
        foreach (var key in _failures.Where(kv => kv.Value.Count == 0 || kv.Value.Last() <= now - longest).Select(kv => kv.Key).ToList()) _failures.Remove(key);
        foreach (var key in _known.Where(kv => now - kv.Value >= KnownClientFor).Select(kv => kv.Key).ToList()) _known.Remove(key);
        if (_failures.Count > MaxEntries)
            foreach (var key in _failures.OrderBy(kv => kv.Value.Last()).Take(_failures.Count - MaxEntries * 3 / 4).Select(kv => kv.Key).ToList()) _failures.Remove(key);
        if (_known.Count > MaxEntries)
            foreach (var key in _known.OrderBy(kv => kv.Value).Take(_known.Count - MaxEntries * 3 / 4).Select(kv => kv.Key).ToList()) _known.Remove(key);
    }
}
