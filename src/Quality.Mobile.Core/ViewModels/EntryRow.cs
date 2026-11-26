// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026), then extended (round 2):
// carries the entry itself (for the detail screen) and a status badge (reversed / correction pending / correction).
using Quality.Core.Dtos;

namespace Quality.Mobile.Core.ViewModels;

public sealed record EntryRow(long Id, string Line1, string Line2, string Line3, EntryDto? Dto = null, string? Badge = null)
{
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
}
