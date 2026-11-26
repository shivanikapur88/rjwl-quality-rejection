// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Platform;

/// <summary>Server address. Deliberately empty by default: it is entered once in Settings (no invented default).</summary>
public sealed class MauiAppSettings : IAppSettings
{
    private const string Key = "api.baseUrl";
    public string BaseUrl
    {
        get => Preferences.Default.Get(Key, "");
        set => Preferences.Default.Set(Key, value ?? "");
    }
}
