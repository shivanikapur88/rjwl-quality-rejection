using System.Text.Json;
using Quality.Core.Abstractions;
using Quality.Core.Common;
using Quality.Core.Dtos;
using Quality.Core.Entities;

namespace Quality.Core.Services;

public sealed record CorrectionOptions(int MaxAgeDays = 30);

/// <summary>
/// Feature #1: entries are never edited or deleted. An inspector asks for a correction of their own entry (with a reason,
/// and optionally the corrected values); a supervisor of that shop or an administrator approves or rejects it.
/// Approval marks the original as REVERSED (excluded from totals, still visible in history) and, if values were proposed,
/// saves a replacement entry that points back to it. Every step is audited.
/// </summary>
/// <remarks>
/// Concurrency: the decision runs under an update lock on the request row, so two supervisors cannot both decide it;
/// one open request per entry and one approved correction per entry are also unique indexes. The entry-database part
/// (reverse + insert replacement) is one transaction there and is idempotent, so a retry after a failure in between
/// completes the same correction instead of doing it twice. Proposed machine/phenomenon are re-validated at approval
/// under the same shared locks as a normal entry save (review Q9).
/// </remarks>
public sealed class EntryCorrectionService
{
    public const int MaxReasonLength = 500, MaxListSize = 200;
    private const string Entity = "EntryCorrection";
    private readonly IMasterData _master; private readonly IEntryStoreRouter _router; private readonly ShopAccessGuard _guard;
    private readonly TimeProvider _time; private readonly CorrectionOptions _options; private readonly IShiftCalendarProvider _calendar;

    public EntryCorrectionService(IMasterData master, IEntryStoreRouter router, ShopAccessGuard guard, TimeProvider time, CorrectionOptions options, IShiftCalendarProvider calendar)
        => (_master, _router, _guard, _time, _options, _calendar) = (master, router, guard, time, options, calendar);

    private static ServiceResult<T> Fail<T>(ErrorKind k, string code, string msg) => ServiceResult<T>.Fail(k, code, msg);

    // ------------------------------------------------------------------ request
    public async Task<ServiceResult<CorrectionDto>> RequestAsync(CurrentUser user, int shopId, long entryId, CorrectionRequest req)
    {
        if (!user.IsInspector) return Fail<CorrectionDto>(ErrorKind.Forbidden, "forbidden", "Only the inspector who recorded an entry can ask for it to be corrected.");
        if (req is null) return Fail<CorrectionDto>(ErrorKind.Validation, "request_required", "Request body required.");
        var reason = (req.Reason ?? "").Trim();
        if (reason.Length == 0 || reason.Length > MaxReasonLength) return Fail<CorrectionDto>(ErrorKind.Validation, "reason_invalid", $"Give a reason (up to {MaxReasonLength} characters).");

        var (shop, err) = await _guard.AuthorizeAsync(user, shopId);
        if (err is not null) return ServiceResult<CorrectionDto>.Fail(err);
        var entry = await _router.For(shop!.EntryStoreKey).GetByIdAsync(entryId);
        if (entry is null || entry.ShopId != shop.Id || entry.InspectorId != user.Id) return Fail<CorrectionDto>(ErrorKind.NotFound, "entry_not_found", "Entry not found.");
        if (entry.ReversedAt is not null) return Fail<CorrectionDto>(ErrorKind.Conflict, "entry_reversed", "This entry has already been reversed.");

        var calendar = await _calendar.GetAsync();
        var today = calendar.Resolve(_time.GetUtcNow().UtcDateTime).ProductionDate;
        var day = entry.ProductionDate ?? calendar.Resolve(entry.ServerCreatedAt).ProductionDate;
        if (day.AddDays(_options.MaxAgeDays) < today)
            return Fail<CorrectionDto>(ErrorKind.Validation, "entry_too_old", $"Entries older than {_options.MaxAgeDays} days cannot be corrected from the app. Ask your supervisor.");

        var c = new EntryCorrection
        {
            ShopId = shop.Id, EntryId = entry.Id, EntrySubmissionId = entry.SubmissionId, RequestedBy = user.Id,
            RequestedAt = _time.GetUtcNow().UtcDateTime, Reason = reason, Status = CorrectionStatus.Pending,
            ProposedMachineId = req.MachineId, ProposedPhenomenonId = req.PhenomenonId, ProposedQuantity = req.Quantity,
            ProposedRemarks = req.Remarks is null ? null : RejectionEntryService.CleanRemarks(req.Remarks) ?? "",
            // Random and server-side (never sent to clients), so nobody can pre-claim it with an ordinary entry (Fable review #6).
            ReplacementSubmissionId = Guid.NewGuid(),
        };
        if (!c.IsReverseOnly)
        {
            var problem = await ValidateProposalAsync(c, entry, shop, lockRows: false);
            if (problem is not null) return ServiceResult<CorrectionDto>.Fail(problem);
            if ((c.ProposedMachineId ?? entry.MachineId) == entry.MachineId && (c.ProposedPhenomenonId ?? entry.RejectionPhenomenonId) == entry.RejectionPhenomenonId
                && (c.ProposedQuantity ?? entry.Quantity) == entry.Quantity && ResultingRemarks(c, entry) == entry.Remarks)
                return Fail<CorrectionDto>(ErrorKind.Validation, "no_change", "The proposed values are the same as the saved entry. Change something, or ask for the entry to be removed.");
        }

        try
        {
            await _master.InTransactionAsync(async () =>
            {
                _master.AddCorrection(c);
                await _master.SaveChangesAsync();
                _master.AddAudit(Audit(user.Id, AuditActions.CorrectionRequested, c, null));
                await _master.SaveChangesAsync();
            });
        }
        catch (UniqueViolationException)   // UX_EntryCorrections_OnePending
        {
            return Fail<CorrectionDto>(ErrorKind.Conflict, "correction_pending", "A correction request for this entry is already waiting for a decision.");
        }
        return ServiceResult<CorrectionDto>.Ok(await ToDtoAsync(c, shop, entry));
    }

    // ------------------------------------------------------------------ decide
    public Task<ServiceResult<CorrectionDto>> ApproveAsync(CurrentUser user, long id, CorrectionDecisionRequest? req) => DecideAsync(user, id, approve: true, req?.Note);
    public Task<ServiceResult<CorrectionDto>> RejectAsync(CurrentUser user, long id, CorrectionDecisionRequest? req) => DecideAsync(user, id, approve: false, req?.Note);

    private async Task<ServiceResult<CorrectionDto>> DecideAsync(CurrentUser user, long id, bool approve, string? note)
    {
        if (!user.IsSupervisor && !user.IsAdministrator) return Fail<CorrectionDto>(ErrorKind.Forbidden, "forbidden", "Only a supervisor or administrator can decide correction requests.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxReasonLength }) return Fail<CorrectionDto>(ErrorKind.Validation, "note_invalid", $"The note may not exceed {MaxReasonLength} characters.");
        if (!approve && note is null) return Fail<CorrectionDto>(ErrorKind.Validation, "note_required", "Give a reason for rejecting the request.");

        var peek = await _master.GetCorrectionAsync(id);
        if (peek is null) return Fail<CorrectionDto>(ErrorKind.NotFound, "correction_not_found", "Correction request not found.");
        var (shop, err) = await _guard.AuthorizeAsync(user, peek.ShopId);
        if (err is not null) return ServiceResult<CorrectionDto>.Fail(err.Kind == ErrorKind.NotFound ? err : new ServiceError(ErrorKind.NotFound, "correction_not_found", "Correction request not found."));
        var store = _router.For(shop!.EntryStoreKey);
        var calendar = await _calendar.GetAsync();

        ServiceError? refused = null;
        EntryCorrection? c = null;
        QualityRejectionEntry? original = null;
        await _master.InTransactionAsync(async () =>
        {
            c = await _master.GetCorrectionLockedAsync(id);
            if (c is null || c.Status != CorrectionStatus.Pending)
            { refused = new(ErrorKind.Conflict, "correction_not_pending", "This request has already been decided or withdrawn. Refresh the list."); return; }
            var old = Snapshot(c);
            var now = _time.GetUtcNow().UtcDateTime;
            original = await store.GetByIdAsync(c.EntryId);   // also for reject: we must know whether an earlier approval was half-applied
            if (original is not null && original.ShopId == c.ShopId && original.ReversedByCorrectionId == c.Id)
            {
                // An earlier approval committed in the entry database but not here (failure between the two databases).
                // Finish it as Approved whatever was asked now; a reject or withdraw would contradict the data (Fable review #1).
                await CompleteHalfAppliedAsync(c, store, user.Id, now, old);
                if (!approve) refused = new(ErrorKind.Conflict, "correction_already_applied", "This correction had already been applied; it is now recorded as approved.");
                return;
            }
            if (approve)
            {
                if (original is null || original.ShopId != c.ShopId)
                { refused = new(ErrorKind.Conflict, "entry_not_found", "The entry of this request no longer exists."); return; }
                QualityRejectionEntry? replacement = null;
                if (!c.IsReverseOnly)
                {
                    var problem = await ValidateProposalAsync(c, original, shop, lockRows: true);   // also takes the Q9 shared locks
                    if (problem is not null) { refused = problem; return; }
                    replacement = await BuildReplacementAsync(c, original, now, calendar);
                }
                try
                {
                    var saved = await store.ApplyCorrectionAsync(c.ShopId, c.EntryId, c.Id, now, replacement);
                    c.ReplacementEntryId = saved?.Id;
                }
                catch (CorrectionConflictException ex) { refused = new(ErrorKind.Conflict, "entry_reversed", ex.Message); return; }
            }
            c.Status = approve ? CorrectionStatus.Approved : CorrectionStatus.Rejected;
            c.DecidedBy = user.Id; c.DecidedAt = now; c.DecisionNote = note;
            _master.AddAudit(Audit(user.Id, approve ? AuditActions.CorrectionApproved : AuditActions.CorrectionRejected, c, old));
            await _master.SaveChangesAsync();
        });
        if (refused is not null) return ServiceResult<CorrectionDto>.Fail(refused);
        return ServiceResult<CorrectionDto>.Ok(await ToDtoAsync(c!, shop, await TryGetEntryAsync(store, c!.EntryId)));
    }

    /// <summary>Records an approval whose entry-database part had already committed (see DecideAsync).</summary>
    private async Task CompleteHalfAppliedAsync(EntryCorrection c, IEntryStore store, int actor, DateTime now, string old)
    {
        var replacement = c.IsReverseOnly ? null : await store.FindBySubmissionIdAsync(c.ReplacementSubmissionId);
        // The person who approved is not known here (the failed request did not record it), so DecidedBy stays empty and the
        // audit uses its own action: the current caller is NOT recorded as the approver (full review #7).
        c.Status = CorrectionStatus.Approved; c.DecidedAt = now;
        c.DecisionNote ??= "Completed automatically: the approval had already been applied to the entry.";
        c.ReplacementEntryId = replacement?.Id;
        _master.AddAudit(Audit(actor, AuditActions.CorrectionApprovalCompleted, c, old));
        await _master.SaveChangesAsync();
    }

    /// <summary>A read after a committed decision must not turn the success into an error (Fable review #8).</summary>
    private static async Task<QualityRejectionEntry?> TryGetEntryAsync(IEntryStore store, long id)
    {
        try { return await store.GetByIdAsync(id); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    // ------------------------------------------------------------------ withdraw
    public async Task<ServiceResult<CorrectionDto>> WithdrawAsync(CurrentUser user, long id)
    {
        var peek = await _master.GetCorrectionAsync(id);
        if (peek is null || peek.RequestedBy != user.Id) return Fail<CorrectionDto>(ErrorKind.NotFound, "correction_not_found", "Correction request not found.");
        var shop = await _master.GetShopAsync(peek.ShopId);
        var store = _router.For(shop!.EntryStoreKey);
        ServiceError? refused = null; EntryCorrection? c = null;
        await _master.InTransactionAsync(async () =>
        {
            c = await _master.GetCorrectionLockedAsync(id);
            if (c is null || c.Status != CorrectionStatus.Pending) { refused = new(ErrorKind.Conflict, "correction_not_pending", "This request has already been decided."); return; }
            var old = Snapshot(c);
            var original = await store.GetByIdAsync(c.EntryId);
            if (original is not null && original.ShopId == c.ShopId && original.ReversedByCorrectionId == c.Id)
            {
                await CompleteHalfAppliedAsync(c, store, c.RequestedBy, _time.GetUtcNow().UtcDateTime, old);
                refused = new(ErrorKind.Conflict, "correction_already_applied", "This correction had already been approved and applied.");
                return;
            }
            c.Status = CorrectionStatus.Withdrawn; c.DecidedBy = user.Id; c.DecidedAt = _time.GetUtcNow().UtcDateTime;
            _master.AddAudit(Audit(user.Id, AuditActions.CorrectionWithdrawn, c, old));
            await _master.SaveChangesAsync();
        });
        if (refused is not null) return ServiceResult<CorrectionDto>.Fail(refused);
        return ServiceResult<CorrectionDto>.Ok(await ToDtoAsync(c!, shop!, null));
    }

    // ------------------------------------------------------------------ list
    /// <summary>Inspector: own requests. Supervisor: requests of assigned shops. Administrator: all. Newest first.</summary>
    public async Task<ServiceResult<IReadOnlyList<CorrectionDto>>> ListAsync(CurrentUser user, string? status, int? shopId)
    {
        if (status is not null && status is not (CorrectionStatus.Pending or CorrectionStatus.Approved or CorrectionStatus.Rejected or CorrectionStatus.Withdrawn))
            return Fail<IReadOnlyList<CorrectionDto>>(ErrorKind.Validation, "status_invalid", "Unknown status.");
        IReadOnlyList<Shop> shops;
        if (shopId is int sid)
        {
            var (s, err) = await _guard.AuthorizeAsync(user, sid);
            if (err is not null) return ServiceResult<IReadOnlyList<CorrectionDto>>.Fail(err);
            shops = new[] { s! };
        }
        else shops = await _guard.AccessibleShopsAsync(user);
        var list = await _master.ListCorrectionsAsync(shops.Select(s => s.Id).ToList(), user.IsInspector ? user.Id : null, status, MaxListSize);
        var byId = shops.ToDictionary(s => s.Id);
        var result = new List<CorrectionDto>();
        foreach (var c in list)
        {
            QualityRejectionEntry? entry = null;
            try { entry = await _router.For(byId[c.ShopId].EntryStoreKey).GetByIdAsync(c.EntryId); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* entry database unreachable: show the request without the entry */ }
            result.Add(await ToDtoAsync(c, byId[c.ShopId], entry));
        }
        return ServiceResult<IReadOnlyList<CorrectionDto>>.Ok(result);
    }

    /// <summary>Count in the caller's scope, optionally for one shop (full review #13: the dashboard tile follows the shop filter).</summary>
    public async Task<ServiceResult<int>> CountAsync(CurrentUser user, string? status, int? shopId = null)
    {
        if (status is not null && status is not (CorrectionStatus.Pending or CorrectionStatus.Approved or CorrectionStatus.Rejected or CorrectionStatus.Withdrawn))
            return Fail<int>(ErrorKind.Validation, "status_invalid", "Unknown status.");
        IReadOnlyList<Shop> shops;
        if (shopId is int sid)
        {
            var (s, err) = await _guard.AuthorizeAsync(user, sid);
            if (err is not null) return ServiceResult<int>.Fail(err);
            shops = new[] { s! };
        }
        else shops = await _guard.AccessibleShopsAsync(user);
        return ServiceResult<int>.Ok(await _master.CountCorrectionsAsync(shops.Select(s => s.Id).ToList(), user.IsInspector ? user.Id : null, status));
    }

    // ------------------------------------------------------------------ helpers
    private async Task<ServiceError?> ValidateProposalAsync(EntryCorrection c, QualityRejectionEntry entry, Shop shop, bool lockRows)
    {
        if (c.ProposedQuantity is int q && (q < 1 || q > RejectionEntryService.MaxQuantity))
            return new(ErrorKind.Validation, "quantity_invalid", $"Quantity must be a whole number from 1 to {RejectionEntryService.MaxQuantity}.");
        if (c.ProposedRemarks is { Length: > RejectionEntryService.MaxRemarksLength })
            return new(ErrorKind.Validation, "remarks_too_long", $"Remarks may not exceed {RejectionEntryService.MaxRemarksLength} characters.");
        var machineId = c.ProposedMachineId ?? entry.MachineId; var phenId = c.ProposedPhenomenonId ?? entry.RejectionPhenomenonId;
        var (m, p) = lockRows ? await _master.ReadMastersForEntryLockedAsync(machineId, phenId)
                              : (await _master.GetMachineAsync(machineId), await _master.GetPhenomenonAsync(phenId));
        if (m is null || m.ShopId != shop.Id) return new(ErrorKind.Validation, "machine_not_in_shop", "The proposed machine does not belong to this shop.");
        if (!m.IsActive && c.ProposedMachineId is not null) return new(ErrorKind.Conflict, "machine_inactive", "The proposed machine is no longer active.");
        if (p is null || p.InspectionStageId != entry.InspectionStageId) return new(ErrorKind.Validation, "phenomenon_not_in_stage", "The proposed phenomenon does not belong to this entry's inspection stage.");
        if (!p.IsActive && c.ProposedPhenomenonId is not null) return new(ErrorKind.Conflict, "phenomenon_inactive", "The proposed phenomenon is no longer active.");
        return null;
    }

    private static string? ResultingRemarks(EntryCorrection c, QualityRejectionEntry e) =>
        c.ProposedRemarks is null ? e.Remarks : (c.ProposedRemarks.Length == 0 ? null : c.ProposedRemarks);

    private async Task<QualityRejectionEntry> BuildReplacementAsync(EntryCorrection c, QualityRejectionEntry o, DateTime now, ShiftCalendar calendar)
    {
        var m = (await _master.GetMachineAsync(c.ProposedMachineId ?? o.MachineId))!;
        var p = (await _master.GetPhenomenonAsync(c.ProposedPhenomenonId ?? o.RejectionPhenomenonId))!;
        return new QualityRejectionEntry
        {
            SubmissionId = c.ReplacementSubmissionId,   // fixed per request: a retried approval cannot insert twice
            ShopId = o.ShopId, ShopCode = o.ShopCode, InspectionStageId = o.InspectionStageId, StageCode = o.StageCode, StageName = o.StageName,
            MachineId = m.Id, MachineCode = m.MachineCode, MachineName = m.MachineName,
            RejectionPhenomenonId = p.Id, PhenomenonNameSnapshot = p.PhenomenonName,
            Quantity = c.ProposedQuantity ?? o.Quantity, Remarks = ResultingRemarks(c, o),
            InspectorId = o.InspectorId, InspectorEmployeeCode = o.InspectorEmployeeCode,   // the inspector who found the defect
            ServerCreatedAt = now,
            // The correction describes the SAME rejection event, so it keeps the original production day and shift.
            ProductionDate = o.ProductionDate ?? calendar.Resolve(o.ServerCreatedAt).ProductionDate,
            ShiftCode = o.ShiftCode ?? calendar.Resolve(o.ServerCreatedAt).ShiftCode,
            CorrectsEntryId = o.Id,
        };
    }

    private static string Snapshot(EntryCorrection c) => JsonSerializer.Serialize(new
    {
        c.ShopId, c.EntryId, c.Reason, c.ProposedMachineId, c.ProposedPhenomenonId, c.ProposedQuantity, c.ProposedRemarks,
        c.Status, c.DecidedBy, c.DecisionNote, c.ReplacementEntryId,
    });

    private AuditLogEntry Audit(int actor, string action, EntryCorrection c, string? old) => new()
    {
        UserId = actor, Action = action, EntityType = Entity, EntityId = c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        OldValue = old, NewValue = Snapshot(c), Timestamp = _time.GetUtcNow().UtcDateTime,
    };

    private async Task<CorrectionDto> ToDtoAsync(EntryCorrection c, Shop shop, QualityRejectionEntry? entry)
    {
        var codes = await _master.GetUserCodesAsync(new[] { c.RequestedBy }.Concat(c.DecidedBy is int d ? new[] { d } : Array.Empty<int>()).Distinct().ToList());
        var m = c.ProposedMachineId is int mid ? await _master.GetMachineAsync(mid) : null;
        var p = c.ProposedPhenomenonId is int pid ? await _master.GetPhenomenonAsync(pid) : null;
        return new CorrectionDto(c.Id, c.ShopId, shop.ShopCode, c.EntryId, entry is null ? null : Mapping.ToDto(entry, c.Status),
            c.RequestedBy, codes.GetValueOrDefault(c.RequestedBy, "#" + c.RequestedBy), DateTime.SpecifyKind(c.RequestedAt, DateTimeKind.Utc), c.Reason,
            c.IsReverseOnly, c.ProposedMachineId, m?.MachineCode, c.ProposedPhenomenonId, p?.PhenomenonName, c.ProposedQuantity, c.ProposedRemarks,
            c.Status, c.DecidedBy, c.DecidedBy is int db ? codes.GetValueOrDefault(db, "#" + db) : null,
            c.DecidedAt is DateTime da ? DateTime.SpecifyKind(da, DateTimeKind.Utc) : null, c.DecisionNote, c.ReplacementEntryId);
    }
}
