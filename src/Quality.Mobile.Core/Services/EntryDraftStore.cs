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
}
