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
    /// </summary>
    [HttpGet("ready"), AllowAnonymous]
    [ProducesResponseType(typeof(ReadinessDto), 200), ProducesResponseType(typeof(ReadinessDto), 503)]
    public async Task<IActionResult> Ready([FromServices] Quality.Data.MasterDbContext master, [FromServices] Quality.Core.Abstractions.IEntryStoreRouter router,
        CancellationToken ct)
    {
        var components = new Dictionary<string, string>();
        components["master"] = await CanConnect(() => master.Database.CanConnectAsync(ct));
        foreach (var key in router.Keys) components["entries:" + key] = await CanConnect(() => router.CanConnectAsync(key, ct));
        var ok = components.Values.All(v => v == "ok");
        return StatusCode(ok ? 200 : 503, new ReadinessDto(ok ? "ok" : "unavailable", components));

        static async Task<string> CanConnect(Func<Task<bool>> probe)
        {
            try { return await probe() ? "ok" : "unavailable"; } catch (Exception) { return "unavailable"; }
        }
    }
}

[ApiController, Route("api/auth")]
public sealed class AuthController : ApiControllerBase
{
    private readonly AuthService _auth;
    private readonly LoginThrottle _throttle;
    public AuthController(AuthService auth, LoginThrottle throttle) => (_auth, _throttle) = (auth, throttle);

    /// <summary>429 "too_many_attempts" (with Retry-After) after repeated FAILED attempts from this client/for this login (review Q4).</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), 200), ProducesResponseType(429)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var login = request?.Username ?? "";
        if (_throttle.RetryAfter(client, login) is { } wait)
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ProblemFor(429, "too_many_attempts", "Too many failed sign-in attempts. Wait a few minutes and try again.");
        }
        var result = await _auth.LoginAsync(request!);
        if (result.IsSuccess) _throttle.RecordSuccess(client, login);
        else if (result.Error!.Kind == ErrorKind.Unauthorized) _throttle.RecordFailure(client, login);
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
    public async Task<IActionResult> Totals([FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate, [FromQuery] int? shopId, [FromQuery] string? shiftCode, [FromQuery] int? top) =>
        Map(await _reports.RangeAsync(Me, new ReportQuery(fromDate, toDate, shopId, shiftCode, top)));
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
