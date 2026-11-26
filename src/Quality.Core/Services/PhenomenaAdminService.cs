using System.Text.Json;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <summary>Administrator-only management of the rejection phenomena master. Every change is audited in the same transaction.</summary>
/// <remarks>
/// Review Q8: code allocation, the normalized-name check and the insert run under one lock on the stage row, and a code
/// clash (e.g. from a manual insert) is retried with the next number. Review Q9: delete locks the phenomenon row exclusively
/// BEFORE checking that no entry uses it. Stale screens: Update/SetStatus accept the Version the screen was built from.
/// </remarks>
public sealed class PhenomenaAdminService
{
    public const int MaxNameLength = 200;
    private const string Entity = "RejectionPhenomenon";
    private readonly IMasterData _master;
    private readonly IEntryStoreRouter _router;
    private readonly TimeProvider _time;

    public PhenomenaAdminService(IMasterData master, IEntryStoreRouter router, TimeProvider time)
        => (_master, _router, _time) = (master, router, time);

    private static ServiceResult<T>? RequireAdmin<T>(CurrentUser u) =>
        u.IsAdministrator ? null : ServiceResult<T>.Fail(ErrorKind.Forbidden, "forbidden", "Administrator access is required.");

    private static string? ValidateName(string? name, out string cleaned)
    {
        cleaned = (name ?? "").Trim();
        if (cleaned.Length == 0) return "Enter a phenomenon name.";
        if (cleaned.Length > MaxNameLength) return $"The name may not exceed {MaxNameLength} characters.";
        return null;
    }

    private static string Snapshot(RejectionPhenomenon p) =>
        JsonSerializer.Serialize(new { p.PhenomenonCode, p.PhenomenonName, p.InspectionStageId, p.IsActive });

    private async Task<AdminPhenomenonDto> ToDtoAsync(RejectionPhenomenon p, Dictionary<int, (InspectionStage Stage, List<string> Shops)>? cache = null)
    {
        cache ??= new();
        if (!cache.TryGetValue(p.InspectionStageId, out var info))
        {
            var stage = await _master.GetStageAsync(p.InspectionStageId) ?? throw new InvalidOperationException("Stage missing for phenomenon " + p.Id);
            var shops = (await _master.GetShopsForStageAsync(p.InspectionStageId)).Select(s => s.ShopCode).ToList();
            info = (stage, shops);
            cache[p.InspectionStageId] = info;
        }
        return new AdminPhenomenonDto(p.Id, p.PhenomenonCode, p.PhenomenonName, info.Stage.Id, info.Stage.StageCode, info.Stage.StageName,
            info.Shops, p.IsActive, p.CreatedBy, DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc), p.ModifiedBy,
            p.ModifiedAt is null ? null : DateTime.SpecifyKind(p.ModifiedAt.Value, DateTimeKind.Utc), RowVersions.Encode(p.RowVer));
    }

    /// <summary>Shows active AND inactive items unless filtered. shopId narrows to stages mapped to that shop.</summary>
    public async Task<ServiceResult<IReadOnlyList<AdminPhenomenonDto>>> ListAsync(CurrentUser user, int? shopId, int? stageId, bool? isActive, string? search)
    {
        var deny = RequireAdmin<IReadOnlyList<AdminPhenomenonDto>>(user); if (deny is not null) return deny;
        if (shopId is int sid && stageId is int stg && !await _master.IsStageMappedToShopAsync(sid, stg))
            return ServiceResult<IReadOnlyList<AdminPhenomenonDto>>.Fail(ErrorKind.Validation, "stage_not_in_shop", "This inspection stage is not available for the selected shop.");

        IEnumerable<RejectionPhenomenon> items = await _master.GetPhenomenaAsync(stageId, isActive, search);
        if (shopId is int shopFilter && stageId is null)
        {
            var allowed = (await _master.GetStagesForShopAsync(shopFilter)).Select(s => s.Id).ToHashSet();
            items = items.Where(p => allowed.Contains(p.InspectionStageId));
        }
        var cache = new Dictionary<int, (InspectionStage, List<string>)>();
        var dtos = new List<AdminPhenomenonDto>();
        foreach (var p in items) dtos.Add(await ToDtoAsync(p, cache));
        return ServiceResult<IReadOnlyList<AdminPhenomenonDto>>.Ok(dtos);
    }

    public async Task<ServiceResult<AdminPhenomenonDto>> CreateAsync(CurrentUser user, CreatePhenomenonRequest req)
    {
        var deny = RequireAdmin<AdminPhenomenonDto>(user); if (deny is not null) return deny;
        var nameErr = ValidateName(req?.Name, out var name);
        if (nameErr is not null) return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Validation, "name_invalid", nameErr);

        var shop = await _master.GetShopAsync(req!.ShopId);
        if (shop is null) return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Validation, "shop_not_found", "Select a shop.");
        var stage = await _master.GetStageAsync(req.StageId);
        if (stage is null) return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Validation, "stage_not_found", "Select an inspection stage.");
        if (!await _master.IsStageMappedToShopAsync(shop.Id, stage.Id))
            return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Validation, "stage_not_in_shop", "The selected stage does not belong to the selected shop.");

        var norm = TextNormalizer.NormalizeName(name);
        var duplicate = ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Conflict, "duplicate_name", $"'{name}' already exists for {stage.StageName}.");
        if (await _master.NormalizedNameExistsAsync(stage.Id, norm, null)) return duplicate;

        for (var attempt = 1; ; attempt++)
        {
            var p = new RejectionPhenomenon
            {
                PhenomenonName = name, NormalizedName = norm, InspectionStageId = stage.Id, IsActive = req.IsActive,
                CreatedBy = user.Id, CreatedAt = _time.GetUtcNow().UtcDateTime,
            };
            var nameTaken = false;
            try
            {
                await _master.InTransactionAsync(async () =>
                {
                    await _master.LockStageForPhenomenonChangesAsync(stage.Id);              // Q8: one allocation per stage at a time
                    if (await _master.NormalizedNameExistsAsync(stage.Id, norm, null)) { nameTaken = true; return; }
                    p.PhenomenonCode = await _master.NextPhenomenonCodeAsync(stage);
                    _master.AddPhenomenon(p);
                    await _master.SaveChangesAsync();   // assigns Id
                    _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = AuditActions.PhenomenonCreated, EntityType = Entity, EntityId = p.Id.ToString(), NewValue = Snapshot(p), Timestamp = p.CreatedAt });
                    await _master.SaveChangesAsync();
                });
            }
            catch (UniqueViolationException ex) when (ex.Name == "UQ_RejectionPhenomena_Stage_Name")
            {
                return duplicate;
            }
            catch (UniqueViolationException) when (attempt < 3)
            {
                _master.Detach(p);
                continue;   // code taken outside the lock (e.g. by a manual insert): allocate the next one
            }
            if (nameTaken) return duplicate;
            return ServiceResult<AdminPhenomenonDto>.Ok(await ToDtoAsync(p));
        }
    }

    public async Task<ServiceResult<AdminPhenomenonDto>> UpdateAsync(CurrentUser user, int id, UpdatePhenomenonRequest req)
    {
        var deny = RequireAdmin<AdminPhenomenonDto>(user); if (deny is not null) return deny;
        var nameErr = ValidateName(req?.Name, out var name);
        if (nameErr is not null) return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Validation, "name_invalid", nameErr);
        var p = await _master.GetPhenomenonAsync(id);
        if (p is null) return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.NotFound, "phenomenon_not_found", "Phenomenon not found.");
        if (ExpectVersion(p, req!.Version) is { } stale) return ServiceResult<AdminPhenomenonDto>.Fail(stale);

        if (string.Equals(p.PhenomenonName, name, StringComparison.Ordinal))
            return ServiceResult<AdminPhenomenonDto>.Ok(await ToDtoAsync(p));   // nothing to change

        var norm = TextNormalizer.NormalizeName(name);
        if (await _master.NormalizedNameExistsAsync(p.InspectionStageId, norm, p.Id))
            return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Conflict, "duplicate_name", $"'{name}' already exists in this inspection stage.");

        var old = Snapshot(p);
        p.PhenomenonName = name; p.NormalizedName = norm; p.ModifiedBy = user.Id; p.ModifiedAt = _time.GetUtcNow().UtcDateTime;
        try
        {
            await _master.InTransactionAsync(async () =>
            {
                _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = AuditActions.PhenomenonEdited, EntityType = Entity, EntityId = p.Id.ToString(), OldValue = old, NewValue = Snapshot(p), Timestamp = p.ModifiedAt.Value });
                await _master.SaveChangesAsync();
            });
        }
        catch (UniqueViolationException)   // concurrent rename/create to the same name in this stage
        {
            return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.Conflict, "duplicate_name", $"'{name}' already exists in this inspection stage.");
        }
        return ServiceResult<AdminPhenomenonDto>.Ok(await ToDtoAsync(p));
    }

    public Task<ServiceResult<AdminPhenomenonDto>> SetStatusAsync(CurrentUser user, int id, bool isActive) => SetStatusAsync(user, id, new SetStatusRequest(isActive));

    public async Task<ServiceResult<AdminPhenomenonDto>> SetStatusAsync(CurrentUser user, int id, SetStatusRequest req)
    {
        var deny = RequireAdmin<AdminPhenomenonDto>(user); if (deny is not null) return deny;
        var isActive = req.IsActive;
        var p = await _master.GetPhenomenonAsync(id);
        if (p is null) return ServiceResult<AdminPhenomenonDto>.Fail(ErrorKind.NotFound, "phenomenon_not_found", "Phenomenon not found.");
        if (p.IsActive == isActive) return ServiceResult<AdminPhenomenonDto>.Ok(await ToDtoAsync(p));
        if (ExpectVersion(p, req.Version) is { } stale) return ServiceResult<AdminPhenomenonDto>.Fail(stale);

        var old = Snapshot(p);
        p.IsActive = isActive; p.ModifiedBy = user.Id; p.ModifiedAt = _time.GetUtcNow().UtcDateTime;
        await _master.InTransactionAsync(async () =>
        {
            _master.AddAudit(new AuditLogEntry
            {
                UserId = user.Id, Action = isActive ? AuditActions.PhenomenonActivated : AuditActions.PhenomenonDeactivated,
                EntityType = Entity, EntityId = p.Id.ToString(), OldValue = old, NewValue = Snapshot(p), Timestamp = p.ModifiedAt.Value
            });
            await _master.SaveChangesAsync();
        });
        return ServiceResult<AdminPhenomenonDto>.Ok(await ToDtoAsync(p));
    }

    /// <summary>Permanent delete only if the phenomenon was never used in ANY entry database; otherwise the caller must deactivate.</summary>
    public async Task<ServiceResult<bool>> DeleteAsync(CurrentUser user, int id)
    {
        var deny = RequireAdmin<bool>(user); if (deny is not null) return deny;
        var p = await _master.GetPhenomenonAsync(id);
        if (p is null) return ServiceResult<bool>.Fail(ErrorKind.NotFound, "phenomenon_not_found", "Phenomenon not found.");

        var old = Snapshot(p);
        var inUse = false;
        await _master.InTransactionAsync(async () =>
        {
            await _master.LockPhenomenonExclusiveAsync(p.Id);                 // Q9: lock first ...
            foreach (var store in _router.All())                             // ... then check every entry database
                if (await store.IsPhenomenonUsedAsync(p.Id)) { inUse = true; return; }
            if (await _master.IsPhenomenonReferencedInMasterAsync(p.Id)) { inUse = true; return; }   // and correction requests
            _master.RemovePhenomenon(p);
            _master.AddAudit(new AuditLogEntry { UserId = user.Id, Action = AuditActions.PhenomenonDeleted, EntityType = Entity, EntityId = p.Id.ToString(), OldValue = old, Timestamp = _time.GetUtcNow().UtcDateTime });
            await _master.SaveChangesAsync();
        });
        if (inUse) return ServiceResult<bool>.Fail(ErrorKind.Conflict, "phenomenon_in_use", "This phenomenon is used in saved rejection entries or correction requests and cannot be deleted. Deactivate it instead.");
        return ServiceResult<bool>.Ok(true);
    }

    private ServiceError? ExpectVersion(RejectionPhenomenon p, string? version)
    {
        var expected = RowVersions.Decode(version);
        if (expected is null) return null;
        if (!RowVersions.Matches(p.RowVer, expected)) return RowVersions.Stale();
        _master.ExpectRowVersion(p, expected);
        return null;
    }
}
