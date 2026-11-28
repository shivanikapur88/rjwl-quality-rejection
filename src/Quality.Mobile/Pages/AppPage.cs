// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.Infrastructure;

namespace Quality.Mobile.Pages;

/// <summary>Connects a page to its view-model: forwards Shell query parameters and the appearing event.</summary>
public abstract class AppPage : ContentPage, IQueryAttributable
{
    protected BaseViewModel Vm => (BaseViewModel)BindingContext;

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = NavigatedToAsync(new Dictionary<string, object>(query));

    private async Task NavigatedToAsync(IDictionary<string, object> args)
    {
        // The task used to be discarded, so an exception was lost unobserved; it is now caught and logged.
        try { await Vm.OnNavigatedToAsync(args); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("OnNavigatedToAsync failed: " + ex); }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await Vm.OnAppearingAsync(); }
        catch (Exception) { /* view-models report their own errors; never crash the page */ }
    }
}
