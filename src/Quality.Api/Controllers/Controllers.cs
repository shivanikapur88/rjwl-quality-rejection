using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quality.Api.Infrastructure;
using Quality.Core.Common;
using Quality.Core.Entities;
using Quality.Core.Dtos;
using Quality.Core.Services;

namespace Quality.Api.Controllers;

[ApiController, Route("api/health")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Liveness: the API process answers. Used by the app's Settings screen. Says nothing about the databases.</summary>
    [HttpGet, AllowAnonymous]
    public IActionResult Get() => Ok(new { status = "ok" });

    /// <summary>
    /// Readiness: can the API reach the master database and every entry database? 200 when all are reachable, otherwise 503.
    /// Reports only "ok"/"unavailable" per component (no server names, no error text).
    /// Anonymous, so it is rate-limited per address and the result is cached for 10 seconds: a client looping on it cannot
    /// open five database connections per request (second full review).
    /// </summary>
    [HttpGet("ready"), AllowAnonymous, EnableRateLimiting("health")]
    [ProducesResponseType(typeof(ReadinessDto), 200), ProducesResponseType(typeof(ReadinessDto), 503)]
    public async Task<IActionResult> Ready([FromServices] Quality.Data.MasterDbContext master, [FromServices] Quality.Core.Abstractions.IEntryStoreRouter router,
        [FromServices] ReadinessCache cache, CancellationToken ct)
    {
        var (status, dto) = await cache.GetAsync(() => Probe(master, router), ct);
        return StatusCode(status, dto);
    }

    /// <summary>Each check gets its own time limit and is NOT tied to the calling request: a caller that disconnects must not
    /// turn a healthy database into a cached "unavailable" for everybody (third full review).</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private async Task<(int, ReadinessDto)> Probe(Quality.Data.MasterDbContext master, Quality.Core.Abstractions.IEntryStoreRouter router)
    {
        var components = new Dictionary<string, string>();
        components["master"] = await CanConnect(t => master.Database.CanConnectAsync(t));
        foreach (var key in router.Keys) components["entries:" + key] = await CanConnect(t => router.CanConnectAsync(key, t));
        // Round 2: entries are stamped with shifts from qa.ShiftDefinitions; a fallback calendar means those are not being used.
        var calendarProvider = HttpContext.RequestServices.GetRequiredService<Quality.Core.Services.IShiftCalendarProvider>();
        var calendar = await calendarProvider.GetAsync();
        // "unavailable" also when an older calendar is still in use because the latest read of the shift table failed.
        components["shifts"] = calendar.IsFallback || calendarProvider.LastLoadFailed ? "unavailable" : "ok";
        // Full review #5: entries without a production day are left out of reports; say so instead of silently under-counting.
        var warnings = new List<string>();
        foreach (var key in router.Keys)
        {
            if (components["entries:" + key] != "ok") continue;
            try
            {
                using var cts = new CancellationTokenSource(ProbeTimeout);
                var n = await router.For(key).CountWithoutProductionDateAsync(cts.Token);
                if (n > 0) warnings.Add($"entries:{key}: some entries have no production day and are not in reports (see the database runbook, script 12).");
            }
            catch (Exception) { /* reachability is already reported above */ }
        }
        var ok = components.Values.All(v => v == "ok");
        return (ok ? 200 : 503, new ReadinessDto(ok ? "ok" : "unavailable", components, warnings.Distinct().ToList()));

        static async Task<string> CanConnect(Func<CancellationToken, Task<bool>> probe)
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            try { return await probe(cts.Token) ? "ok" : "unavailable"; } catch (Exception) { return "unavailable"; }
        }
    }
}

/// <summary>Holds the last readiness result for 10 seconds; concurrent callers share one probe.</summary>
public sealed class ReadinessCache(TimeProvider time)
{
    public static readonly TimeSpan For = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Entry(DateTime At, int Status, ReadinessDto Dto);   // immutable: read without the lock, never torn
    private volatile Entry? _last;

    public async Task<(int Status, ReadinessDto Dto)> GetAsync(Func<Task<(int, ReadinessDto)>> probe, CancellationToken ct = default)
    {
        if (Fresh() is { } hit) return (hit.Status, hit.Dto);
        await _gate.WaitAsync(ct);   // a caller that gives up leaves the queue
        try
        {
            if (Fresh() is { } hit2) return (hit2.Status, hit2.Dto);
            var (status, dto) = await probe();
            _last = new Entry(time.GetUtcNow().UtcDateTime, status, dto);   // stamped when the probe FINISHED
            return (status, dto);
        }
        finally { _gate.Release(); }
    }

    private Entry? Fresh() => _last is { } l && time.GetUtcNow().UtcDateTime - l.At < For ? l : null;
}

[ApiController, Route("api/auth")]
public sealed class AuthController : ApiControllerBase
{
    private readonly AuthService _auth;
    private readonly LoginThrottle _throttle;
    public AuthController(AuthService auth, LoginThrottle throttle) => (_auth, _throttle) = (auth, throttle);

    /// <summary>429 "too_many_attempts" (with Retry-After) after repeated FAILED attempts from this client/for this login (review Q4).</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login"), RequestSizeLimit(8 * 1024)]   // no 30 MB anonymous bodies
    [ProducesResponseType(typeof(LoginResponse), 200), ProducesResponseType(429)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var client = LoginThrottle.ClientKey(HttpContext.Connection.RemoteIpAddress);
        var login = request?.Username ?? "";
        await _throttle.PrepareAsync(client, login, HttpContext.RequestAborted);   // known logins survive restarts (db/16)
        if (_throttle.TryBegin(client, login, out var known) is { } wait)   // checks AND reserves the attempt atomically (full review #2)
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ProblemFor(429, "too_many_attempts", "Too many failed sign-in attempts. Wait a few minutes and try again.");
        }
        ServiceResult<LoginResponse> result;
        try { result = await _auth.LoginAsync(request!); }
        catch
        {
            // Database down, timeout, client gone: not a wrong password, so it must not count (third full review).
            _throttle.Refund(client, login, known);
            throw;
        }
        if (result.IsSuccess) { _throttle.RecordSuccess(client, login, known); await _throttle.RememberAsync(client, login, CancellationToken.None); }
        else if (result.Error!.Kind != ErrorKind.Unauthorized) _throttle.Refund(client, login, known);   // never reached the password check
        return Map(result);
    }
}

/// <summary>Lists that drive the inspector workflow. Phenomena and machines are always read from the database.</summary>
[ApiController, Route("api"), Authorize]
public sealed class CatalogController : ApiControllerBase
{
    private readonly CatalogService _catalog;
    public CatalogController(CatalogService catalog) => _catalog = catalog;

    [HttpGet("shops")]
    public async Task<IActionResult> Shops() => Map(await _catalog.GetShopsAsync(Me));

    [HttpGet("shops/{shopId:int}/stages")]
    public async Task<IActionResult> Stages(int shopId) => Map(await _catalog.GetStagesAsync(Me, shopId));

    [HttpGet("shops/{shopId:int}/stages/{stageId:int}/machines")]
    public async Task<IActionResult> Machines(int shopId, int stageId) => Map(await _catalog.GetMachinesAsync(Me, shopId, stageId));

    [HttpGet("shops/{shopId:int}/stages/{stageId:int}/phenomena")]
    public async Task<IActionResult> Phenomena(int shopId, int stageId) => Map(await _catalog.GetPhenomenaAsync(Me, shopId, stageId));
}

[ApiController, Route("api/rejection-entries"), Authorize]
public sealed class RejectionEntriesController : ApiControllerBase
{
    private readonly RejectionEntryService _entries;
    public RejectionEntriesController(RejectionEntryService entries) => _entries = entries;

    /// <summary>201 for a new entry, 200 when the same SubmissionId was already saved (safe retry).</summary>
    [HttpPost, Authorize(Roles = RoleNames.QualityInspector)]
    [ProducesResponseType(typeof(CreateEntryResult), 201), ProducesResponseType(typeof(CreateEntryResult), 200)]
    public async Task<IActionResult> Create([FromBody] CreateEntryRequest request) =>
        Map(await _entries.CreateAsync(Me, request), r => r.AlreadySubmitted
            ? Ok(r)
            : Created($"/api/rejection-entries/{r.Entry.ShopId}/{r.Entry.Id}", r));

    /// <summary>Legacy list: newest first, at most `limit` (default 50, max 200), toUtc INCLUSIVE. Not a complete history - use /page.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? shopId, [FromQuery] int? stageId, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] int? limit) =>
        Map(await _entries.ListAsync(Me, new EntryQuery(shopId, stageId, fromUtc, toUtc, limit)));

    /// <summary>
    /// Paged history (stable order, cursor-based). fromUtc inclusive, toUtc EXCLUSIVE; send the previous response's nextCursor
    /// as `cursor` to get the next page. `unavailableShops` lists shops whose database could not be read (page is incomplete).
    /// </summary>
    [HttpGet("page")]
    [ProducesResponseType(typeof(EntryPage), 200)]
    public async Task<IActionResult> Page([FromQuery] int? shopId, [FromQuery] int? stageId, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] int? limit, [FromQuery] string? cursor) =>
        Map(await _entries.PageAsync(Me, new EntryPageQuery(shopId, stageId, fromUtc, toUtc, limit, cursor)));

    /// <summary>Entry ids are only unique within one shop database, so the shop is part of the route.</summary>
    [HttpGet("{shopId:int}/{id:long}")]
    public async Task<IActionResult> Get(int shopId, long id) => Map(await _entries.GetAsync(Me, shopId, id));
}

[ApiController, Route("api/admin/phenomena"), Authorize(Roles = RoleNames.Administrator)]
public sealed class AdminPhenomenaController : ApiControllerBase
{
    private readonly PhenomenaAdminService _admin;
    public AdminPhenomenaController(PhenomenaAdminService admin) => _admin = admin;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? shopId, [FromQuery] int? stageId, [FromQuery] bool? isActive, [FromQuery] string? search) =>
        Map(await _admin.ListAsync(Me, shopId, stageId, isActive, search));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePhenomenonRequest request) =>
        Map(await _admin.CreateAsync(Me, request), r => Created($"/api/admin/phenomena/{r.Id}", r));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdatePhenomenonRequest request) => Map(await _admin.UpdateAsync(Me, id, request));

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest request) => Map(await _admin.SetStatusAsync(Me, id, request));

    /// <summary>Allowed only for phenomena never used in any entry database; otherwise 409 and the client should deactivate.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) => Map(await _admin.DeleteAsync(Me, id), _ => NoContent());
}

[ApiController, Route("api/admin/machines"), Authorize(Roles = RoleNames.Administrator)]
public sealed class AdminMachinesController : ApiControllerBase
{
    private readonly MachineAdminService _machines;
    public AdminMachinesController(MachineAdminService machines) => _machines = machines;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? shopId, [FromQuery] bool? isActive, [FromQuery] string? search) => Map(await _machines.ListAsync(Me, shopId, isActive, search));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMachineRequest request) => Map(await _machines.CreateAsync(Me, request), r => Created($"/api/admin/machines/{r.Id}", r));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateMachineRequest request) => Map(await _machines.UpdateAsync(Me, id, request));

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest request) => Map(await _machines.SetStatusAsync(Me, id, request));

    /// <summary>Only for machines never used in a rejection entry; otherwise 409 `machine_in_use` and the client should deactivate.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) => Map(await _machines.DeleteAsync(Me, id), _ => NoContent());
}

[ApiController, Route("api/admin/users"), Authorize(Roles = RoleNames.Administrator)]
public sealed class AdminUsersController : ApiControllerBase
{
    private readonly UserAdminService _users;
    public AdminUsersController(UserAdminService users) => _users = users;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] bool? isActive) => Map(await _users.ListAsync(Me, search, isActive));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request) => Map(await _users.CreateAsync(Me, request), r => Created($"/api/admin/users/{r.Id}", r));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest request) => Map(await _users.UpdateAsync(Me, id, request));

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest request) => Map(await _users.SetStatusAsync(Me, id, request));

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest request) => Map(await _users.ResetPasswordAsync(Me, id, request), _ => NoContent());
}

/// <summary>Feature #1: correction requests. Entries are never edited; an approved request reverses the entry (and may add a replacement).</summary>
[ApiController, Route("api"), Authorize]
public sealed class CorrectionsController : ApiControllerBase
{
    private readonly EntryCorrectionService _svc;
    public CorrectionsController(EntryCorrectionService svc) => _svc = svc;

    /// <summary>Inspector asks to correct one of their own entries. No proposed values = remove the entry; any value = corrected entry.</summary>
    [HttpPost("rejection-entries/{shopId:int}/{id:long}/corrections"), Authorize(Roles = RoleNames.QualityInspector)]
    [ProducesResponseType(typeof(CorrectionDto), 201)]
    public async Task<IActionResult> Create(int shopId, long id, [FromBody] CorrectionRequest request) =>
        Map(await _svc.RequestAsync(Me, shopId, id, request), r => Created($"/api/corrections/{r.Id}", r));

    /// <summary>Inspector: own requests. Supervisor: assigned shops. Administrator: all. Default status: Pending.</summary>
    [HttpGet("corrections")]
    public async Task<IActionResult> List([FromQuery] string? status = CorrectionStatus.Pending, [FromQuery] int? shopId = null) =>
        Map(await _svc.ListAsync(Me, string.Equals(status, "all", StringComparison.OrdinalIgnoreCase) ? null : status, shopId));

    /// <summary>Number of requests in the caller's scope (for badges and the dashboard tile); one query, no entry reads.</summary>
    [HttpGet("corrections/count")]
    public async Task<IActionResult> Count([FromQuery] string? status = CorrectionStatus.Pending, [FromQuery] int? shopId = null) =>
        Map(await _svc.CountAsync(Me, string.Equals(status, "all", StringComparison.OrdinalIgnoreCase) ? null : status, shopId));

    [HttpPost("corrections/{id:long}/approve"), Authorize(Roles = RoleNames.Supervisor + "," + RoleNames.Administrator)]
    public async Task<IActionResult> Approve(long id, [FromBody] CorrectionDecisionRequest? request) => Map(await _svc.ApproveAsync(Me, id, request));

    [HttpPost("corrections/{id:long}/reject"), Authorize(Roles = RoleNames.Supervisor + "," + RoleNames.Administrator)]
    public async Task<IActionResult> Reject(long id, [FromBody] CorrectionDecisionRequest? request) => Map(await _svc.RejectAsync(Me, id, request));

    [HttpPost("corrections/{id:long}/withdraw"), Authorize(Roles = RoleNames.QualityInspector)]
    public async Task<IActionResult> Withdraw(long id) => Map(await _svc.WithdrawAsync(Me, id));
}

/// <summary>Features #2, #4, #6: plant calendar and rejection totals.</summary>
[ApiController, Route("api/reports"), Authorize]
public sealed class ReportsController : ApiControllerBase
{
    private readonly ReportService _reports;
    public ReportsController(ReportService reports) => _reports = reports;

    /// <summary>Today's production date, current shift and the shift list (any signed-in user).</summary>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(CalendarDto), 200)]
    public async Task<IActionResult> Calendar() => Ok(await _reports.CalendarAsync());

    /// <summary>Totals for production days fromDate..toDate (inclusive; default today), optional shop and shift. Supervisor/administrator.</summary>
    [HttpGet("totals")]
    [ProducesResponseType(typeof(ReportDto), 200)]
    public async Task<IActionResult> Totals([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate, [FromQuery] int? shopId, [FromQuery] string? shiftCode, [FromQuery] int? top,
        [FromQuery] int? machineId, [FromQuery] int? phenomenonId, [FromQuery] bool compare = false) =>
        Map(await _reports.RangeAsync(Me, new ReportQuery(fromDate, toDate, shopId, shiftCode, top, machineId, phenomenonId, compare)));

    /// <summary>Round 3 drill-down: the entries behind a selection (same filters as totals; reversed excluded), newest first.</summary>
    [HttpGet("entries")]
    [ProducesResponseType(typeof(ReportEntriesDto), 200)]
    public async Task<IActionResult> Entries([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate, [FromQuery] int? shopId, [FromQuery] string? shiftCode,
        [FromQuery] int? machineId, [FromQuery] int? phenomenonId, [FromQuery] int? limit) =>
        Map(await _reports.EntriesAsync(Me, new ReportQuery(fromDate, toDate, shopId, shiftCode, null, machineId, phenomenonId), limit));
}

/// <summary>Round 3, #8: production counts for the rejection rate (administrators).</summary>
[ApiController, Route("api/admin/production-counts"), Authorize(Roles = RoleNames.Administrator)]
public sealed class ProductionCountsController : ApiControllerBase
{
    /// <summary>Validate (dryRun = true, the default) or apply an upload. All-or-nothing; problems are listed by row.</summary>
    [HttpPost, RequestSizeLimit(2_000_000)]
    [ProducesResponseType(typeof(ProductionCountResult), 200)]
    public async Task<IActionResult> Upload([FromBody] ProductionCountUpload request, [FromServices] ProductionCountService svc) =>
        Map(await svc.UploadAsync(Me, request));
}

/// <summary>Feature #8: machine QR codes.</summary>
[ApiController, Route("api"), Authorize]
public sealed class MachineQrController : ApiControllerBase
{
    /// <summary>Resolves a scanned label (inspector). 409 machine_inactive, 403 shop_not_assigned, 400 qr_invalid.</summary>
    [HttpGet("machines/lookup"), Authorize(Roles = RoleNames.QualityInspector)]
    [ProducesResponseType(typeof(MachineLookupDto), 200)]
    public async Task<IActionResult> Lookup([FromQuery] string code, [FromServices] MachineLookupService svc) => Map(await svc.LookupAsync(Me, code));

    /// <summary>SVG QR code for printing a machine label (administrator). Content: RJWLQR:M:&lt;id&gt;.</summary>
    [HttpGet("admin/machines/{id:int}/qr.svg"), Authorize(Roles = RoleNames.Administrator)]
    public async Task<IActionResult> QrSvg(int id, [FromServices] Quality.Core.Abstractions.IMasterData master)
    {
        if (await master.GetMachineAsync(id) is null) return ProblemFor(404, "machine_not_found", "Machine not found.");
        using var data = new QRCoder.QRCodeGenerator().CreateQrCode(MachineLookupService.LabelPayload(id), QRCoder.QRCodeGenerator.ECCLevel.Q);
        var svg = new QRCoder.SvgQRCode(data).GetGraphic(8);
        return Content(svg, "image/svg+xml");
    }
}
