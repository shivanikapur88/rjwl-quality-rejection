using Quality.Core.Abstractions;
using Quality.Core.Dtos;

namespace Quality.Core.Services;

/// <summary>Read-only lists that drive the Android workflow. All names come from the database, never from the app.</summary>
public sealed class CatalogService
{
    private readonly IMasterData _master;
    private readonly ShopAccessGuard _guard;

    public CatalogService(IMasterData master, ShopAccessGuard guard) => (_master, _guard) = (master, guard);

    public async Task<ServiceResult<IReadOnlyList<ShopDto>>> GetShopsAsync(CurrentUser user)
    {
        var shops = await _guard.AccessibleShopsAsync(user);
        return ServiceResult<IReadOnlyList<ShopDto>>.Ok(shops.Select(s => new ShopDto(s.Id, s.ShopCode, s.ShopName)).ToList());
    }

    public async Task<ServiceResult<IReadOnlyList<StageDto>>> GetStagesAsync(CurrentUser user, int shopId)
    {
        var (shop, err) = await _guard.AuthorizeAsync(user, shopId);
        if (err is not null) return ServiceResult<IReadOnlyList<StageDto>>.Fail(err);
        var stages = await _master.GetStagesForShopAsync(shop!.Id);
        return ServiceResult<IReadOnlyList<StageDto>>.Ok(stages.Select(s => new StageDto(s.Id, s.StageCode, s.StageName)).ToList());
    }

    public async Task<ServiceResult<IReadOnlyList<MachineDto>>> GetMachinesAsync(CurrentUser user, int shopId, int stageId)
    {
        var (shop, err) = await _guard.AuthorizeAsync(user, shopId);
        if (err is not null) return ServiceResult<IReadOnlyList<MachineDto>>.Fail(err);
        if (!await _master.IsStageMappedToShopAsync(shop!.Id, stageId))
            return ServiceResult<IReadOnlyList<MachineDto>>.Fail(ErrorKind.Validation, "stage_not_in_shop", "This inspection stage is not available for the selected shop.");
        var machines = await _master.GetActiveMachinesForShopAsync(shop.Id);
        return ServiceResult<IReadOnlyList<MachineDto>>.Ok(machines.Select(m => new MachineDto(m.Id, m.MachineCode, m.MachineName)).ToList());
    }

    /// <summary>Active phenomena of exactly one stage (Pre-treatment and FINAL are never merged).</summary>
    public async Task<ServiceResult<IReadOnlyList<PhenomenonDto>>> GetPhenomenaAsync(CurrentUser user, int shopId, int stageId)
    {
        var (shop, err) = await _guard.AuthorizeAsync(user, shopId);
        if (err is not null) return ServiceResult<IReadOnlyList<PhenomenonDto>>.Fail(err);
        if (!await _master.IsStageMappedToShopAsync(shop!.Id, stageId))
            return ServiceResult<IReadOnlyList<PhenomenonDto>>.Fail(ErrorKind.Validation, "stage_not_in_shop", "This inspection stage is not available for the selected shop.");
        var list = await _master.GetPhenomenaAsync(stageId, isActive: true, search: null);
        return ServiceResult<IReadOnlyList<PhenomenonDto>>.Ok(list.Select(p => new PhenomenonDto(p.Id, p.PhenomenonCode, p.PhenomenonName)).ToList());
    }
}
