// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class EntryFormPage : AppPage
{
    public EntryFormPage(EntryFormViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    // App design round (A1): a phenomenon was chosen - close the keyboard so the quantity and Save are in view.
    private void OnPhenomenonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count > 0) Search.Unfocus();
    }

    // "+ Add remarks" (its command opens the box): put the cursor in it.
    private void OnAddRemarks(object? sender, EventArgs e) => Dispatcher.Dispatch(() => RemarksEditor.Focus());
}
