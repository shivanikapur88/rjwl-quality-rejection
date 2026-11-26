using Quality.Core.Abstractions;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

public sealed class RejectionEntryService
{
    public const int MaxRemarksLength = 500;
    public const int MaxQuantity = 9999;
    private readonly IMasterData _master;
    private readonly IEntryStoreRouter _router;
    private readonly ShopAccessGuard _guard;
    private readonly TimeProvider _time;

    public RejectionEntryService(IMasterData master, IEntryStoreRouter router, ShopAccessGuard guard, TimeProvider time)
        => (_master, _router, _guard, _time) = (master, router, guard, time);

    public async Task<ServiceResult<CreateEntryResult>> CreateAsync(CurrentUser user, CreateEntryRequest req)
    {
        static ServiceResult<CreateEntryResult> Fail(ErrorKind k, string c, string m) => ServiceResult<CreateEntryResult>.Fail(k, c, m);

        if (!user.IsInspector)
            return Fail(ErrorKind.Forbidden, "forbidden", "Only quality inspectors can create rejection entries.");
        if (req is null || req.SubmissionId == Guid.Empty)
            return Fail(ErrorKind.Validation, "submission_id_required", "A submission id is required.");

        if (req.Quantity < 1 || req.Quantity > MaxQuantity)
            return Fail(ErrorKind.Validation, "quantity_invalid", $"Quantity must be a whole number from 1 to {MaxQuantity}.");

        var remarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim();
        if (remarks is not null && remarks.Length > MaxRemarksLength)
            return Fail(ErrorKind.Validation, "remarks_too_long", $"Remarks may not exceed {MaxRemarksLength} characters.");

        var (shop, shopErr) = await _guard.AuthorizeAsync(user, req.ShopId);
        if (shopErr is not null) return ServiceResult<CreateEntryResult>.Fail(shopErr);
        var store = _router.For(shop!.EntryStoreKey);

        // Idempotency first: a retry must return the original even if the phenomenon was deactivated in between.
        var existing = await store.FindBySubmissionIdAsync(req.SubmissionId);
        if (existing is not null) return Replay(existing, user, req);

        var stage = await _master.GetStageAsync(req.StageId);
        if (stage is null || !stage.IsActive || !await _master.IsStageMappedToShopAsync(shop.Id, stage.Id))
            return Fail(ErrorKind.Validation, "stage_not_in_shop", "The selected inspection stage does not belong to the selected shop.");

        var machine = await _master.GetMachineAsync(req.MachineId);
        if (machine is null || !machine.IsActive || machine.ShopId != shop.Id)
            return Fail(ErrorKind.Validation, "machine_not_in_shop", "The selected machine does not belong to the selected shop.");

        var phen = await _master.GetPhenomenonAsync(req.PhenomenonId);
        if (phen is null)
            return Fail(ErrorKind.Validation, "phenomenon_not_found", "The selected rejection phenomenon does not exist.");
        if (phen.InspectionStageId != stage.Id)
            return Fail(ErrorKind.Validation, "phenomenon_not_in_stage", "The selected rejection phenomenon does not belong to the selected inspection stage.");
        if (!phen.IsActive)
            return Fail(ErrorKind.Conflict, "phenomenon_inactive", "The selected rejection phenomenon is no longer active. Refresh the list and choose again.");

        var entry = new QualityRejectionEntry
        {
            SubmissionId = req.SubmissionId,
            ShopId = shop.Id, ShopCode = shop.ShopCode,
            InspectionStageId = stage.Id, StageCode = stage.StageCode, StageName = stage.StageName,
            MachineId = machine.Id, MachineCode = machine.MachineCode, MachineName = machine.MachineName,
            RejectionPhenomenonId = phen.Id, PhenomenonNameSnapshot = phen.PhenomenonName, Quantity = req.Quantity,
            InspectorId = user.Id, InspectorEmployeeCode = user.EmployeeCode,   // from the token, never the client
            Remarks = remarks,
            ServerCreatedAt = _time.GetUtcNow().UtcDateTime,                    // server time
        };

        try
        {
            var saved = await store.InsertAsync(entry);
            return ServiceResult<CreateEntryResult>.Ok(new CreateEntryResult(Mapping.ToDto(saved), false));
        }
        catch (DuplicateSubmissionException)   // concurrent double tap / retry that passed the check above
        {
            var winner = await store.FindBySubmissionIdAsync(req.SubmissionId);
            return winner is null
                ? Fail(ErrorKind.Conflict, "duplicate_submission", "This entry was already submitted.")
                : Replay(winner, user, req);
        }
    }

    private static ServiceResult<CreateEntryResult> Replay(QualityRejectionEntry existing, CurrentUser user, CreateEntryRequest req)
    {
        if (existing.InspectorId != user.Id)
            return ServiceResult<CreateEntryResult>.Fail(ErrorKind.Conflict, "submission_id_in_use", "This submission id is already in use.");
        var sameContent = existing.ShopId == req.ShopId && existing.InspectionStageId == req.StageId
            && existing.MachineId == req.MachineId && existing.RejectionPhenomenonId == req.PhenomenonId && existing.Quantity == req.Quantity;
        if (!sameContent)
            return ServiceResult<CreateEntryResult>.Fail(ErrorKind.Conflict, "submission_payload_mismatch", "This submission id was already used for a different entry.");
        return ServiceResult<CreateEntryResult>.Ok(new CreateEntryResult(Mapping.ToDto(existing), true));
    }

    /// <summary>Inspector: own entries. Supervisor: all entries of assigned shops. Administrator: all shops.</summary>
    public async Task<ServiceResult<IReadOnlyList<EntryDto>>> ListAsync(CurrentUser user, EntryQuery query)
    {
        var limit = Math.Clamp(query.Limit ?? 50, 1, 200);
        IReadOnlyList<Shop> shops;
        if (query.ShopId is int sid)
        {
            var (shop, err) = await _guard.AuthorizeAsync(user, sid);
            if (err is not null) return ServiceResult<IReadOnlyList<EntryDto>>.Fail(err);
            shops = new[] { shop! };
        }
        else shops = await _guard.AccessibleShopsAsync(user);

        var all = new List<QualityRejectionEntry>();
        foreach (var shop in shops)
        {
            var filter = new EntryFilter(shop.Id, user.IsInspector ? user.Id : null, query.StageId, query.FromUtc, query.ToUtc, limit);
            all.AddRange(await _router.For(shop.EntryStoreKey).ListAsync(filter));
        }

        var result = all.OrderByDescending(e => e.ServerCreatedAt).ThenByDescending(e => e.Id).Take(limit).Select(Mapping.ToDto).ToList();
        return ServiceResult<IReadOnlyList<EntryDto>>.Ok(result);
    }

    /// <summary>Ids are only unique per shop database, hence the shopId in the lookup.</summary>
    public async Task<ServiceResult<EntryDto>> GetAsync(CurrentUser user, int shopId, long id)
    {
        var (shop, err) = await _guard.AuthorizeAsync(user, shopId);
        if (err is not null) return ServiceResult<EntryDto>.Fail(err);
        var entry = await _router.For(shop!.EntryStoreKey).GetByIdAsync(id);
        if (entry is null || entry.ShopId != shop.Id || (user.IsInspector && entry.InspectorId != user.Id))   // ShopId check: a shared table can hold another shop's entry with this id
            return ServiceResult<EntryDto>.Fail(ErrorKind.NotFound, "entry_not_found", "Entry not found.");
        return ServiceResult<EntryDto>.Ok(Mapping.ToDto(entry));
    }
}
