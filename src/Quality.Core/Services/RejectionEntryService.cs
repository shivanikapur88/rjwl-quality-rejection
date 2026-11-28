using System.Globalization;
using System.Text;
using Quality.Core.Abstractions;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

/// <remarks>
/// Review fixes in this class:
/// Q7 - an idempotent retry is compared on the cleaned-up remarks as well as shop, stage, machine, phenomenon and quantity.
/// Q9 - the machine and phenomenon are read with shared locks (master database) that are held until the entry insert in the
///      shop's entry database has committed; delete/deactivate wait for that, and see the committed entry.
/// Also: an inactive machine is reported as "machine_inactive" (409) so the app knows to refresh its list; date filters are
///      normalised to UTC; history can be read page by page with a stable cursor and reports any entry database it could not read.
/// </remarks>
public sealed class RejectionEntryService
{
    public const int MaxRemarksLength = 500;
    public const int MaxQuantity = 9999;
    public const int DefaultPageSize = 50, MaxPageSize = 200;
    private readonly IMasterData _master;
    private readonly IEntryStoreRouter _router;
    private readonly ShopAccessGuard _guard;
    private readonly TimeProvider _time;
    private readonly IShiftCalendarProvider _calendar;

    public RejectionEntryService(IMasterData master, IEntryStoreRouter router, ShopAccessGuard guard, TimeProvider time, IShiftCalendarProvider calendar)
        => (_master, _router, _guard, _time, _calendar) = (master, router, guard, time, calendar);

    /// <summary>The one definition of "cleaned-up remarks", used when saving AND when comparing a retry.</summary>
    public static string? CleanRemarks(string? remarks) => string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();

    public async Task<ServiceResult<CreateEntryResult>> CreateAsync(CurrentUser user, CreateEntryRequest req)
    {
        static ServiceResult<CreateEntryResult> Fail(ErrorKind k, string c, string m) => ServiceResult<CreateEntryResult>.Fail(k, c, m);

        if (!user.IsInspector)
            return Fail(ErrorKind.Forbidden, "forbidden", "Only quality inspectors can create rejection entries.");
        if (req is null || req.SubmissionId == Guid.Empty)
            return Fail(ErrorKind.Validation, "submission_id_required", "A submission id is required.");

        if (req.Quantity < 1 || req.Quantity > MaxQuantity)
            return Fail(ErrorKind.Validation, "quantity_invalid", $"Quantity must be a whole number from 1 to {MaxQuantity}.");

        var remarks = CleanRemarks(req.Remarks);
        if (remarks is not null && remarks.Length > MaxRemarksLength)
            return Fail(ErrorKind.Validation, "remarks_too_long", $"Remarks may not exceed {MaxRemarksLength} characters.");

        var (shop, shopErr) = await _guard.AuthorizeAsync(user, req.ShopId);
        if (shopErr is not null) return ServiceResult<CreateEntryResult>.Fail(shopErr);
        var store = _router.For(shop!.EntryStoreKey);

        // Idempotency first: a retry must return the original even if the phenomenon was deactivated in between.
        var existing = await store.FindBySubmissionIdAsync(req.SubmissionId);
        if (existing is not null) return Replay(existing, user, req, remarks);

        var stage = await _master.GetStageAsync(req.StageId);
        if (stage is null || !stage.IsActive || !await _master.IsStageMappedToShopAsync(shop.Id, stage.Id))
            return Fail(ErrorKind.Validation, "stage_not_in_shop", "The selected inspection stage does not belong to the selected shop.");

        var calendar = await _calendar.GetAsync();
        ServiceResult<CreateEntryResult>? result = null;
        await _master.InTransactionAsync(async () =>
        {
            // Q9: shared locks on both master rows until this transaction ends, i.e. until after the entry insert committed.
            var (machine, phen) = await _master.ReadMastersForEntryLockedAsync(req.MachineId, req.PhenomenonId);

            if (machine is null || machine.ShopId != shop.Id)
            { result = Fail(ErrorKind.Validation, "machine_not_in_shop", "The selected machine does not belong to the selected shop."); return; }
            if (!machine.IsActive)
            { result = Fail(ErrorKind.Conflict, "machine_inactive", "The selected machine is no longer active. Refresh the list and choose again."); return; }
            if (phen is null)
            { result = Fail(ErrorKind.Validation, "phenomenon_not_found", "The selected rejection phenomenon does not exist."); return; }
            if (phen.InspectionStageId != stage.Id)
            { result = Fail(ErrorKind.Validation, "phenomenon_not_in_stage", "The selected rejection phenomenon does not belong to the selected inspection stage."); return; }
            if (!phen.IsActive)
            { result = Fail(ErrorKind.Conflict, "phenomenon_inactive", "The selected rejection phenomenon is no longer active. Refresh the list and choose again."); return; }

            var entry = new QualityRejectionEntry
            {
                SubmissionId = req.SubmissionId,
                ShopId = shop.Id, ShopCode = shop.ShopCode,
                InspectionStageId = stage.Id, StageCode = stage.StageCode, StageName = stage.StageName,
                MachineId = machine.Id, MachineCode = machine.MachineCode, MachineName = machine.MachineName,
                RejectionPhenomenonId = phen.Id, PhenomenonNameSnapshot = phen.PhenomenonName, Quantity = req.Quantity,
                InspectorId = user.Id, InspectorEmployeeCode = user.EmployeeCode,   // from the token, never the client
                Remarks = remarks,
                ServerCreatedAt = TimeRules.ToMilliseconds(_time.GetUtcNow().UtcDateTime),   // server time, at the column's datetime2(3) precision
            };
            (entry.ProductionDate, entry.ShiftCode) = calendar.Resolve(entry.ServerCreatedAt);   // #2: plant production day + shift

            try
            {
                var saved = await store.InsertAsync(entry);
                result = ServiceResult<CreateEntryResult>.Ok(new CreateEntryResult(Mapping.ToDto(saved), false));
            }
            catch (DuplicateSubmissionException)   // concurrent double tap / retry that passed the check above
            {
                var winner = await store.FindBySubmissionIdAsync(req.SubmissionId);
                result = winner is null
                    ? Fail(ErrorKind.Conflict, "duplicate_submission", "This entry was already submitted.")
                    : Replay(winner, user, req, remarks);
            }
        });
        return result!;
    }

    private static ServiceResult<CreateEntryResult> Replay(QualityRejectionEntry existing, CurrentUser user, CreateEntryRequest req, string? cleanedRemarks)
    {
        if (existing.InspectorId != user.Id)
            return ServiceResult<CreateEntryResult>.Fail(ErrorKind.Conflict, "submission_id_in_use", "This submission id is already in use.");
        var sameContent = existing.ShopId == req.ShopId && existing.InspectionStageId == req.StageId
            && existing.MachineId == req.MachineId && existing.RejectionPhenomenonId == req.PhenomenonId && existing.Quantity == req.Quantity
            && string.Equals(CleanRemarks(existing.Remarks), cleanedRemarks, StringComparison.Ordinal);   // Q7
        if (!sameContent)
            return ServiceResult<CreateEntryResult>.Fail(ErrorKind.Conflict, "submission_payload_mismatch", "This submission id was already used for a different entry.");
        return ServiceResult<CreateEntryResult>.Ok(new CreateEntryResult(Mapping.ToDto(existing), true));
    }

    /// <summary>A DateTime from a query string: "Z"/offset values arrive as Local and are converted; values without a zone are taken as UTC.</summary>
    public static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Legacy list (kept for compatibility): newest first, at most <c>limit</c> (default 50, max 200) entries, ToUtc INCLUSIVE.
    /// Inspector: own entries. Supervisor: all entries of assigned shops. Administrator: all shops.
    /// If any entry database cannot be read the whole request fails; use <see cref="PageAsync"/> for complete history.
    /// </summary>
    public async Task<ServiceResult<IReadOnlyList<EntryDto>>> ListAsync(CurrentUser user, EntryQuery query)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var (shops, err) = await ScopeAsync(user, query.ShopId);
        if (err is not null) return ServiceResult<IReadOnlyList<EntryDto>>.Fail(err);

        var all = new List<QualityRejectionEntry>();
        foreach (var shop in shops!)
        {
            var filter = new EntryFilter(shop.Id, user.IsInspector ? user.Id : null, query.StageId, ToUtc(query.FromUtc), ToUtc(query.ToUtc), limit);
            all.AddRange(await _router.For(shop.EntryStoreKey).ListAsync(filter));
        }

        var result = all.OrderByDescending(e => e.ServerCreatedAt).ThenByDescending(e => e.ShopId).ThenByDescending(e => e.Id).Take(limit).Select(Mapping.ToDto).ToList();
        return ServiceResult<IReadOnlyList<EntryDto>>.Ok(result);
    }

    /// <summary>
    /// Paged history. Order: ServerCreatedAt DESC, ShopId DESC, Id DESC (stable, also for equal timestamps in different databases).
    /// FromUtc inclusive, ToUtc EXCLUSIVE. A shop whose entry database fails is listed in UnavailableShops instead of failing the request.
    /// </summary>
    public async Task<ServiceResult<EntryPage>> PageAsync(CurrentUser user, EntryPageQuery query)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var from = ToUtc(query.FromUtc); var to = ToUtc(query.ToUtc);
        if (from is not null && to is not null && from >= to)
            return ServiceResult<EntryPage>.Fail(ErrorKind.Validation, "date_range_invalid", "The start of the date range must be before its end.");
        Cursor? cursor = null;
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            cursor = Cursor.TryParse(query.Cursor);
            if (cursor is null) return ServiceResult<EntryPage>.Fail(ErrorKind.Validation, "cursor_invalid", "The page cursor is not valid. Reload the list.");
        }
        var (shops, err) = await ScopeAsync(user, query.ShopId);
        if (err is not null) return ServiceResult<EntryPage>.Fail(err);

        var all = new List<QualityRejectionEntry>();
        var unavailable = new List<string>();
        foreach (var shop in shops!)
        {
            // Per shop, the global cursor (time, shopId, id) becomes "time < t, or time = t and Id < bound".
            long idBelow = cursor is null ? long.MinValue
                : shop.Id < cursor.ShopId ? long.MaxValue        // same time, lower shop id: every row sorts after the cursor
                : shop.Id > cursor.ShopId ? long.MinValue        // same time, higher shop id: every row sorts before it
                : cursor.Id;
            var filter = new EntryFilter(shop.Id, user.IsInspector ? user.Id : null, query.StageId, from, to, limit + 1,
                ToInclusive: false, CursorTimeUtc: cursor?.TimeUtc, CursorIdBelow: idBelow);
            try { all.AddRange(await _router.For(shop.EntryStoreKey).ListAsync(filter)); }
            catch (Exception ex) when (ex is not OperationCanceledException && !DbErrors.IsDeadlock(ex))   /* a deadlock goes to the GET retry */ { unavailable.Add(shop.ShopCode); }
        }

        var ordered = all.OrderByDescending(e => e.ServerCreatedAt).ThenByDescending(e => e.ShopId).ThenByDescending(e => e.Id).ToList();
        var page = ordered.Take(limit).ToList();
        string? next = ordered.Count > limit ? new Cursor(page[^1].ServerCreatedAt, page[^1].ShopId, page[^1].Id).ToString() : null;
        return ServiceResult<EntryPage>.Ok(new EntryPage(await WithCorrectionStatusAsync(page), next, unavailable));
    }

    private async Task<(IReadOnlyList<Shop>? Shops, ServiceError? Error)> ScopeAsync(CurrentUser user, int? shopId)
    {
        if (shopId is int sid)
        {
            var (shop, err) = await _guard.AuthorizeAsync(user, sid);
            return err is not null ? (null, err) : (new[] { shop! }, null);
        }
        return (await _guard.AccessibleShopsAsync(user), null);
    }

    /// <summary>Ids are only unique per shop database, hence the shopId in the lookup.</summary>
    public async Task<ServiceResult<EntryDto>> GetAsync(CurrentUser user, int shopId, long id)
    {
        var (shop, err) = await _guard.AuthorizeAsync(user, shopId);
        if (err is not null) return ServiceResult<EntryDto>.Fail(err);
        var entry = await _router.For(shop!.EntryStoreKey).GetByIdAsync(id);
        if (entry is null || entry.ShopId != shop.Id || (user.IsInspector && entry.InspectorId != user.Id))   // ShopId check: a shared table can hold another shop's entry with this id
            return ServiceResult<EntryDto>.Fail(ErrorKind.NotFound, "entry_not_found", "Entry not found.");
        return ServiceResult<EntryDto>.Ok((await WithCorrectionStatusAsync(new[] { entry }))[0]);
    }

    /// <summary>Adds the latest correction status per entry (#1), one master query per shop on the page.</summary>
    private async Task<IReadOnlyList<EntryDto>> WithCorrectionStatusAsync(IReadOnlyList<QualityRejectionEntry> entries)
    {
        var status = new Dictionary<(int, long), string>();
        foreach (var g in entries.GroupBy(e => e.ShopId))
            foreach (var c in await _master.GetCorrectionsForEntriesAsync(g.Key, g.Select(e => e.Id).ToList()))
                status[(c.ShopId, c.EntryId)] = c.Status;
        return entries.Select(e => Mapping.ToDto(e, status.TryGetValue((e.ShopId, e.Id), out var s) ? s : null)).ToList();
    }

    /// <summary>Opaque page cursor: base64url("ticks.shopId.id") of the last row returned.</summary>
    private sealed record Cursor(DateTime TimeUtc, int ShopId, long Id)
    {
        public override string ToString() =>
            Convert.ToBase64String(Encoding.ASCII.GetBytes(FormattableString.Invariant($"{TimeUtc.Ticks}.{ShopId}.{Id}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static Cursor? TryParse(string s)
        {
            try
            {
                var b64 = s.Replace('-', '+').Replace('_', '/');
                b64 += new string('=', (4 - b64.Length % 4) % 4);
                var parts = Encoding.ASCII.GetString(Convert.FromBase64String(b64)).Split('.');
                if (parts.Length != 3) return null;
                var ticks = long.Parse(parts[0], CultureInfo.InvariantCulture);
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
                return new Cursor(new DateTime(ticks, DateTimeKind.Utc), int.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        }
    }
}
