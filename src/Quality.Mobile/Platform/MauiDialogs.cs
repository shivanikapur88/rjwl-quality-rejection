// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Platform;

public sealed class MauiDialogs : IDialogs
{
    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, accept, cancel));

    public Task<string?> PromptAsync(string title, string message, string accept, string cancel, string? placeholder = null, int maxLength = 500) =>
        MainThread.InvokeOnMainThreadAsync(async () => (string?)await Shell.Current.DisplayPromptAsync(title, message, accept, cancel, placeholder, maxLength));
}
