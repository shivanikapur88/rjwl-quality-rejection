using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Quality.Api.Infrastructure;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Services;

namespace Quality.Api.Controllers;

[ApiController, Route("api/health")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Anonymous connectivity check used by the app's Settings screen. Reveals nothing about the system.</summary>
    [HttpGet, AllowAnonymous]
    public IActionResult Get() => Ok(new { status = "ok" });
}

[ApiController, Route("api/auth")]
public sealed class AuthController : ApiControllerBase
{
    private readonly AuthService _auth;
    public AuthController(AuthService auth) => _auth = auth;

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), 200)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request) => Map(await _auth.LoginAsync(request));
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

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? shopId, [FromQuery] int? stageId, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] int? limit) =>
        Map(await _entries.ListAsync(Me, new EntryQuery(shopId, stageId, fromUtc, toUtc, limit)));

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
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest request) => Map(await _admin.SetStatusAsync(Me, id, request.IsActive));

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
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest request) => Map(await _machines.SetStatusAsync(Me, id, request.IsActive));

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
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest request) => Map(await _users.SetStatusAsync(Me, id, request.IsActive));

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest request) => Map(await _users.ResetPasswordAsync(Me, id, request), _ => NoContent());
}
