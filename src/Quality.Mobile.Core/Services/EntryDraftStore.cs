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
    private PendingSubmission? _pending;

    public void SetPending(PendingSubmission pending) => _pending = pending;

    /// <summary>The outcome of <paramref name="submissionId"/> is known (saved or refused): forget it.</summary>
    public void SettlePending(Guid submissionId)
    {
        if (_pending is not null && _pending.SubmissionId == submissionId) _pending = null;
    }

    public void ClearPending() => _pending = null;

    public Guid? PendingFor(int userId, int shopId, int stageId, int machineId) =>
        _pending is { } p && p.UserId == userId && p.ShopId == shopId && p.StageId == stageId && p.MachineId == machineId
            ? p.SubmissionId : null;
}

/// <summary>A save that may have reached the server, and the identity of its payload.</summary>
public sealed record PendingSubmission(int UserId, int ShopId, int StageId, int MachineId, Guid SubmissionId);
