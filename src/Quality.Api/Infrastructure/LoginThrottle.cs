using Microsoft.EntityFrameworkCore;
using Quality.Core.Entities;
using Quality.Data;

namespace Quality.Api.Infrastructure;

/// <summary>Durable memory of (client address, login) pairs that signed in successfully (db/16), so the "known login"
/// exemptions survive API restarts and application-pool recycles (Fable review).</summary>
public interface IKnownLoginStore
{
    Task<DateTime?> LastSuccessAsync(string client, string loginKey, CancellationToken ct);
    Task SaveAsync(string client, string loginKey, DateTime atUtc, CancellationToken ct);
}

public sealed class EfKnownLoginStore(IServiceScopeFactory scopes, ILogger<EfKnownLoginStore> log) : IKnownLoginStore
{
    private int _saves;

    public async Task<DateTime?> LastSuccessAsync(string client, string loginKey, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        return await db.LoginKnownClients.AsNoTracking().Where(x => x.ClientKey == client && x.LoginKey == loginKey)
            .Select(x => (DateTime?)x.LastSuccessAt).FirstOrDefaultAsync(ct);
    }

    public async Task SaveAsync(string client, string loginKey, DateTime atUtc, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var at = new DateTime(atUtc.Ticks - atUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var updated = await db.LoginKnownClients.Where(x => x.ClientKey == client && x.LoginKey == loginKey)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSuccessAt, at), ct);
        if (updated == 0)
        {
            db.LoginKnownClients.Add(new LoginKnownClient { ClientKey = client, LoginKey = loginKey, LastSuccessAt = at });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* a parallel sign-in inserted it first: fine */ }
        }
        if (Interlocked.Increment(ref _saves) % 500 == 1)   // now and then: remove pairs not used for the exemption period
        {
            var cutoff = at - LoginThrottle.KnownClientFor;
            var removed = await db.LoginKnownClients.Where(x => x.LastSuccessAt < cutoff).ExecuteDeleteAsync(ct);
            if (removed > 0) log.LogInformation("Removed {Count} expired known-login rows.", removed);
        }
    }
}

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
    private readonly IKnownLoginStore? _store;
    private readonly ILogger? _log;
    private readonly Dictionary<string, DateTime> _checked = new();   // pairs looked up in the store recently (hit or miss)
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5);

    public LoginThrottle(TimeProvider time, IKnownLoginStore? store = null, ILogger<LoginThrottle>? log = null) => (_time, _store, _log) = (time, store, log);

    /// <summary>Every login that is not a valid identifier after folding shares this one key.</summary>
    public const string OtherLoginKey = "~OTHER";
    private static readonly System.Text.RegularExpressions.Regex ValidKey = new(@"^[A-Z0-9._-]+$");

    /// <summary>
    /// The throttle key of a typed login: trimmed, cut to 50 characters (never splitting a surrogate pair), folded
    /// (compatibility form, accents removed, upper case). Identifiers can only contain letters, digits, '.', '_' and '-'
    /// (LoginRules), so anything else after folding - zero-width characters, 'ß', emoji, control characters - shares ONE key
    /// instead of opening a fresh bucket per variant that the database collation may treat as the same account (Fable review).
    /// </summary>
    public static string LoginKey(string login)
    {
        var l = (login ?? "").Trim();
        if (l.Length > MaxLoginKeyLength) l = l[..MaxLoginKeyLength];   // cut first: never fold a huge string
        if (l.Length > 0 && char.IsHighSurrogate(l[^1])) l = l[..^1];  // do not leave half a surrogate pair
        string normalized;
        try { normalized = l.Normalize(System.Text.NormalizationForm.FormKD); }
        catch (ArgumentException) { return OtherLoginKey; }            // invalid UTF-16: not an identifier
        var folded = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) folded.Append(ch);
        var key = folded.ToString().ToUpperInvariant();
        return ValidKey.IsMatch(key) ? key : OtherLoginKey;
    }

    private static string Login(string login) => LoginKey(login);

    /// <summary>
    /// Before <see cref="TryBegin(string,string,out bool)"/>: if this pair is not known in memory, look it up in the durable
    /// store (at most every 5 minutes per pair), so a restart does not forget known logins. Store failures are ignored.
    /// </summary>
    public async Task PrepareAsync(string client, string login, CancellationToken ct)
    {
        if (_store is null) return;
        var now = _time.GetUtcNow().UtcDateTime;
        var key = $"{client}|{Login(login)}";
        lock (_gate)
        {
            if (_known.TryGetValue(key, out var at) && now - at < KnownClientFor) return;
            if (_checked.TryGetValue(key, out var c) && now - c < RecheckAfter) return;
        }
        try
        {
            var last = await _store.LastSuccessAsync(client, Login(login), ct);
            lock (_gate)
            {
                // Marked as checked only after a successful lookup: a failed one is retried on the next attempt.
                if (_checked.Count > MaxEntries) _checked.Clear();
                _checked[key] = now;
                if (last is { } l && now - l < KnownClientFor && (!_known.TryGetValue(key, out var mem) || mem < l)) _known[key] = l;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "Could not read known logins; using memory only."); }
    }

    /// <summary>After a successful sign-in: remember the pair durably. Store failures are logged, never fail the sign-in.</summary>
    public async Task RememberAsync(string client, string login, CancellationToken ct)
    {
        if (_store is null || Login(login) == OtherLoginKey) return;
        try { await _store.SaveAsync(client, Login(login), _time.GetUtcNow().UtcDateTime, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "Could not save a known login; it is kept in memory."); }
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
            _failures.Remove($"cl:{client}|{l}");                 // this client signed in: its own failures are cleared
            // The per-login bucket also holds failures from OTHER addresses (possibly an attacker's): give back only this
            // attempt's reservation instead of wiping them (fourth review).
            if (!knownAtBegin) { RemoveNewest($"l:{l}"); RemoveNewest($"c:{client}"); }
            // The shared key of non-identifier logins never becomes "known" (it would exempt every odd login from this address).
            if (l != OtherLoginKey) _known[$"{client}|{l}"] = now;
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
    private static Limit LimitOf(string key) => key.StartsWith("cl:", StringComparison.Ordinal) ? PerClientAndLogin
        : key.StartsWith("l:", StringComparison.Ordinal) ? PerLogin : PerClient;

    private void Prune(DateTime now)
    {
        _lastPrune = now;
        var longest = new[] { PerClientAndLogin.Window, PerLogin.Window, PerClient.Window }.Max();
        foreach (var key in _failures.Where(kv => kv.Value.Count == 0 || kv.Value.Last() <= now - longest).Select(kv => kv.Key).ToList()) _failures.Remove(key);
        foreach (var key in _known.Where(kv => now - kv.Value >= KnownClientFor).Select(kv => kv.Key).ToList()) _known.Remove(key);
        if (_failures.Count > MaxEntries)
            // Evict what matters least first: never a bucket that is AT its limit (an attacker must not be able to flush a
            // targeted login's bucket by flooding random logins - Fable review), then single-failure keys, oldest first.
            foreach (var key in _failures.Where(kv => kv.Value.Count < LimitOf(kv.Key).MaxFailures)
                         .OrderBy(kv => kv.Value.Count > 1).ThenBy(kv => kv.Value.Last())
                         .Take(_failures.Count - MaxEntries * 3 / 4).Select(kv => kv.Key).ToList())
                _failures.Remove(key);
        if (_known.Count > MaxEntries)
            foreach (var key in _known.OrderBy(kv => kv.Value).Take(_known.Count - MaxEntries * 3 / 4).Select(kv => kv.Key).ToList()) _known.Remove(key);
    }
}
