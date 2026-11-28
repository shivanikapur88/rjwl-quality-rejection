// New (not in the supplied app): keeps an unsaved rejection entry when the session expires while it is being saved,
// so the inspector can sign in again and resume it with the SAME submission id (no duplicate if the first attempt
// was in fact saved). In memory only: closing the app discards it (documented in docs/USER_GUIDE.md).
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.Services;

public sealed record EntryDraft(int UserId, ShopDto Shop, StageDto Stage, MachineDto Machine, int? PhenomenonId,
    string QuantityText, string Remarks, Guid SubmissionId);

public sealed class EntryDraftStore
{
    private EntryDraft? _draft;
    public void Save(EntryDraft draft) => _draft = draft;
    public void Clear() => _draft = null;

    /// <summary>Returns and removes the draft if it belongs to <paramref name="userId"/>; a draft of another user is discarded.</summary>
    public EntryDraft? Take(int userId)
    {
        var d = _draft; _draft = null;
        return d is not null && d.UserId == userId ? d : null;
    }

    /// <summary>Review fix: returns the draft of <paramref name="userId"/> WITHOUT removing it (a draft of another user is
    /// discarded). The entry form removes it with <see cref="Remove"/> once it has put it back on screen, so a failed
    /// phenomena load (or leaving the form) does not lose it.</summary>
    public EntryDraft? Peek(int userId)
    {
        if (_draft is not null && _draft.UserId != userId) _draft = null;
        return _draft;
    }

    /// <summary>Removes <paramref name="draft"/> if it is still the one kept (a newer draft is left alone).</summary>
    public void Remove(EntryDraft draft)
    {
        if (Equals(_draft, draft)) _draft = null;
    }

    // Review fix: the submission id of a save whose outcome is unknown (timeout, lost response) outlives the entry form
    // (a transient view-model): Back, re-enter and Save for the same user, shop, stage and machine reuses it, so the server
    // replays the saved entry (or answers submission_payload_mismatch) instead of creating a duplicate.
    // Fifth review: one slot per user/shop/stage/machine (a second unknown outcome on another machine no longer drops the
    // first), the entry's details are kept so the form shows them again (never a blank form silently tied to an old id),
    // and a pending id expires after PendingLifetime, so a genuinely new identical rejection later is not taken for it.
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(30);

    /// <summary>The clock (tests replace it).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    private readonly Dictionary<(int User, int Shop, int Stage, int Machine), PendingSubmission> _pending = new();

    public void SetPending(PendingSubmission pending) =>
        _pending[(pending.UserId, pending.ShopId, pending.StageId, pending.MachineId)] = pending with { SentAt = Now() };

    /// <summary>The outcome of <paramref name="submissionId"/> is known (saved or refused): forget it.</summary>
    public void SettlePending(Guid submissionId)
    {
        foreach (var k in _pending.Where(kv => kv.Value.SubmissionId == submissionId).Select(kv => kv.Key).ToList()) _pending.Remove(k);
    }

    public void ClearPending() => _pending.Clear();

    /// <summary>The unexpired pending save of this user, shop, stage and machine, with its details; null if none.</summary>
    public PendingSubmission? PendingEntry(int userId, int shopId, int stageId, int machineId)
    {
        var key = (userId, shopId, stageId, machineId);
        if (!_pending.TryGetValue(key, out var p)) return null;
        if (Now() - p.SentAt > PendingLifetime) { _pending.Remove(key); return null; }
        return p;
    }

    public Guid? PendingFor(int userId, int shopId, int stageId, int machineId) =>
        PendingEntry(userId, shopId, stageId, machineId)?.SubmissionId;
}

/// <summary>A save that may have reached the server: its identity, the details sent and when.</summary>
public sealed record PendingSubmission(int UserId, int ShopId, int StageId, int MachineId, Guid SubmissionId,
    int? PhenomenonId = null, string QuantityText = "1", string Remarks = "", DateTimeOffset SentAt = default);
