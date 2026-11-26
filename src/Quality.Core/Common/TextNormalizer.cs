using System.Text.RegularExpressions;

namespace Quality.Core.Common;

/// <summary>
/// Produces the uniqueness key for phenomenon names. Must behave identically to
/// normalize() in tools/import_excel.py (verified by Quality.Verify).
/// The original text is always stored separately and never altered.
/// </summary>
public static class TextNormalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Whitespace.Replace(value.Trim(), " ").ToUpperInvariant();
    }
}
