// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Platform;

/// <summary>Token storage in the Android Keystore via SecureStorage.</summary>
public sealed class MauiSecureStore : ISecureStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);
    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);
    public void Remove(string key) => SecureStorage.Default.Remove(key);
}
