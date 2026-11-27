// App design round: one place that decides the colour of a status chip, so "Correction requested", "Reversed (corrected)",
// "Correction of entry #…", "Active"/"Inactive" and correction statuses look the same on every screen.
namespace Quality.Mobile.Core.ViewModels;

public enum ChipTone { Info, Warning, Danger, Muted, Success }

public static class StatusTone
{
    /// <summary>Tone of a status text shown in a chip. Unknown texts are Info.</summary>
    public static ChipTone For(string? text) => (text ?? "").Trim() switch
    {
        "Correction requested" or "Pending" => ChipTone.Warning,
        "Reversed (corrected)" or "Inactive" or "Withdrawn" => ChipTone.Muted,
        "Rejected" => ChipTone.Danger,
        "Active" or "Approved" => ChipTone.Success,
        _ => ChipTone.Info,   // e.g. "Correction of entry #123"
    };
}
