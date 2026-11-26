using System.Text.Json;
using System.Text.RegularExpressions;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <summary>Administrator-only machine master management. Delete is allowed only for machines never used in an entry.</summary>
/// <remarks>
/// Review Q9: delete takes an exclusive lock on the machine row BEFORE the "never used" check. An entry save holds a shared
/// lock on the same row until its insert has committed (RejectionEntryService), so the two can no longer interleave.
/// Stale screens: Update/SetStatus accept the Version the screen was built from.
/// </remarks>
public sealed class MachineAdminService
{
    public const int MaxCodeLength = 30, MaxNameLength = 100;
    private const string Entity = "Machine";
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9][A-Za-z0-9 ._/-]{0,29}$");
    private readonly IMasterData _master; private readonly IEntryStoreRouter _router; private readonly TimeProvider _time;

    public MachineAdminService(IMasterData master, IEntryStoreRouter router, TimeProvider time) => (_master, _router, _time) = (master, router, time);

    private static ServiceResult<T>? RequireAdmin<T>(CurrentUser u) =>
        u.IsAdministrator ? null : ServiceResult<T>.Fail(ErrorKind.Forbidden, "forbidden", "Administrator access is required.");

    private static ServiceError? Validate(string? code, string? name, out string c, out string n)
    {
        c = (code ?? "").Trim(); n = (name ?? "").Trim();
        if (c.Length == 0) return new(ErrorKind.Validation, "code_invalid", "Enter a machine code.");
        if (!CodePattern.IsMatch(c)) return new(ErrorKind.Validation, "code_invalid", $"The machine code may use letters, digits, space and . _ / - (max {MaxCodeLength} characters).");
        if (n.Length == 0) return new(ErrorKind.Validation, "name_invalid", "Enter a machine name.");
        if (n.Length > MaxNameLength) return new(ErrorKind.Validation, "name_invalid", $"The machine name may not exceed {MaxNameLength} characters.");
        return null;
    }

    private static string Snapshot(Machine m) => JsonSerializer.Serialize(new { m.MachineCode, m.MachineName, m.ShopId, m.IsActive });

    private async Task<AdminMachineDto> ToDtoAsync(Machine m)
    {
        var shop = await _master.GetShopAsync(m.ShopId) ?? throw new InvalidOperationException("Shop missing for machine " + m.Id);
        return new AdminMachineDto(m.Id, m.MachineCode, m.MachineName, m.ShopId, shop.ShopCode, shop.ShopName, m.IsActive, RowVersions.Encode(m.RowVer));
    }

    public async Task<ServiceResult<IReadOnlyList<AdminMachineDto>>> ListAsync(CurrentUser user, int? shopId, bool? isActive, string? search)
    {
        var deny = RequireAdmin<IReadOnlyList<AdminMachineDto>>(user); if (deny is not null) return deny;
        var shops = (await _master.GetAllShopsAsync()).ToDictionary(s => s.Id);
        var list = (await _master.GetMachinesAsync(shopId, isActive, search))
            .Select(m => new AdminMachineDto(m.Id, m.MachineCode, m.MachineName, m.ShopId, shops[m.ShopId].ShopCode, shops[m.ShopId].ShopName, m.IsActive, RowVersions.Encode(m.RowVer))).ToList();
        return ServiceResult<IReadOnlyList<AdminMachineDto>>.Ok(list);
    }

    public async Task<ServiceResult<AdminMachineDto>> CreateAsync(CurrentUser user, CreateMachineRequest req)
    {
        var deny = RequireAdmin<AdminMachineDto>(user); if (deny is not null) return deny;
        var err = Validate(req?.MachineCode, req?.MachineName, out var code, out var name);
        if (err is not null) return ServiceResult<AdminMachineDto>.Fail(err);
        var shop = await _master.GetShopAsync(req!.ShopId);
        if (shop is null || !shop.IsActive) return ServiceResult<AdminMachineDto>.Fail(ErrorKind.Validation, "shop_not_found", "Select a shop.");
        if (await _master.MachineCodeExistsAsync(shop.Id, code, null))
            return ServiceResult<AdminMachineDto>.Fail(ErrorKind.Conflict, "duplicate_code", $"Machine code '{code}' already exists in {shop.ShopName}.");

        var m = new Machine { MachineCode = code, MachineName = name, ShopId = shop.Id, IsActive = req.IsActive };
        try
        {
            await _master.InTransactionAsync(async () =>
            {
                _master.AddMachine(m);
                await _master.SaveChangesAsync();
                _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = AuditActions.MachineCreated, EntityType = Entity, EntityId = m.Id.ToString(), NewValue = Snapshot(m), Timestamp = _time.GetUtcNow().UtcDateTime });
                await _master.SaveChangesAsync();
            });
        }
        catch (UniqueViolationException)   // concurrent create of the same code (UQ_Machines_Shop_Code)
        {
            return ServiceResult<AdminMachineDto>.Fail(ErrorKind.Conflict, "duplicate_code", $"Machine code '{code}' already exists in {shop.ShopName}.");
        }
        return ServiceResult<AdminMachineDto>.Ok(await ToDtoAsync(m));
    }

    public async Task<ServiceResult<AdminMachineDto>> UpdateAsync(CurrentUser user, int id, UpdateMachineRequest req)
    {
        var deny = RequireAdmin<AdminMachineDto>(user); if (deny is not null) return deny;
        var err = Validate(req?.MachineCode, req?.MachineName, out var code, out var name);
        if (err is not null) return ServiceResult<AdminMachineDto>.Fail(err);
        var m = await _master.GetMachineForEditAsync(id);
        if (m is null) return ServiceResult<AdminMachineDto>.Fail(ErrorKind.NotFound, "machine_not_found", "Machine not found.");
        if (ExpectVersion(m, req!.Version) is { } stale) return ServiceResult<AdminMachineDto>.Fail(stale);
        if (m.MachineCode == code && m.MachineName == name) return ServiceResult<AdminMachineDto>.Ok(await ToDtoAsync(m));
        if (await _master.MachineCodeExistsAsync(m.ShopId, code, m.Id))
            return ServiceResult<AdminMachineDto>.Fail(ErrorKind.Conflict, "duplicate_code", $"Machine code '{code}' already exists in this shop.");

        var old = Snapshot(m);
        m.MachineCode = code; m.MachineName = name;   // past entries keep their own snapshot of code and name
        m.ModifiedAt = _time.GetUtcNow().UtcDateTime;
        try
        {
            await _master.InTransactionAsync(async () =>
            {
                _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = AuditActions.MachineEdited, EntityType = Entity, EntityId = m.Id.ToString(), OldValue = old, NewValue = Snapshot(m), Timestamp = m.ModifiedAt.Value });
                await _master.SaveChangesAsync();
            });
        }
        catch (UniqueViolationException)
        {
            return ServiceResult<AdminMachineDto>.Fail(ErrorKind.Conflict, "duplicate_code", $"Machine code '{code}' already exists in this shop.");
        }
        return ServiceResult<AdminMachineDto>.Ok(await ToDtoAsync(m));
    }

    public Task<ServiceResult<AdminMachineDto>> SetStatusAsync(CurrentUser user, int id, bool isActive) => SetStatusAsync(user, id, new SetStatusRequest(isActive));

    public async Task<ServiceResult<AdminMachineDto>> SetStatusAsync(CurrentUser user, int id, SetStatusRequest req)
    {
        var deny = RequireAdmin<AdminMachineDto>(user); if (deny is not null) return deny;
        var isActive = req.IsActive;
        var m = await _master.GetMachineForEditAsync(id);
        if (m is null) return ServiceResult<AdminMachineDto>.Fail(ErrorKind.NotFound, "machine_not_found", "Machine not found.");
        if (m.IsActive == isActive) return ServiceResult<AdminMachineDto>.Ok(await ToDtoAsync(m));
        if (ExpectVersion(m, req.Version) is { } stale) return ServiceResult<AdminMachineDto>.Fail(stale);
        // Deactivation needs no extra lock: the UPDATE's exclusive lock waits for any entry save that holds the row (Q9).
        var old = Snapshot(m); m.IsActive = isActive; m.ModifiedAt = _time.GetUtcNow().UtcDateTime;
        await _master.InTransactionAsync(async () =>
        {
            _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = isActive ? AuditActions.MachineActivated : AuditActions.MachineDeactivated, EntityType = Entity, EntityId = m.Id.ToString(), OldValue = old, NewValue = Snapshot(m), Timestamp = _time.GetUtcNow().UtcDateTime });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<AdminMachineDto>.Ok(await ToDtoAsync(m));
    }

    public async Task<ServiceResult<bool>> DeleteAsync(CurrentUser user, int id)
    {
        var deny = RequireAdmin<bool>(user); if (deny is not null) return deny;
        var m = await _master.GetMachineForEditAsync(id);
        if (m is null) return ServiceResult<bool>.Fail(ErrorKind.NotFound, "machine_not_found", "Machine not found.");
        var shop = await _master.GetShopAsync(m.ShopId);
        if (shop is null) return ServiceResult<bool>.Fail(ErrorKind.NotFound, "shop_not_found", "Shop not found.");

        var old = Snapshot(m);
        var inUse = false;
        await _master.InTransactionAsync(async () =>
        {
            await _master.LockMachineExclusiveAsync(m.Id);                    // Q9: lock first ...
            if (await _router.For(shop.EntryStoreKey).IsMachineUsedAsync(m.Id)) { inUse = true; return; }   // ... then check
            _master.RemoveMachine(m);
            _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = AuditActions.MachineDeleted, EntityType = Entity, EntityId = m.Id.ToString(), OldValue = old, Timestamp = _time.GetUtcNow().UtcDateTime });
            await _master.SaveChangesAsync();
        });
        if (inUse) return ServiceResult<bool>.Fail(ErrorKind.Conflict, "machine_in_use", "This machine is used in saved rejection entries and cannot be deleted. Deactivate it instead.");
        return ServiceResult<bool>.Ok(true);
    }

    private ServiceError? ExpectVersion(Machine m, string? version)
    {
        var expected = RowVersions.Decode(version);
        if (expected is null) return null;
        if (!RowVersions.Matches(m.RowVer, expected)) return RowVersions.Stale();
        _master.ExpectRowVersion(m, expected);
        return null;
    }
}
