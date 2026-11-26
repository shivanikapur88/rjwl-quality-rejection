// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class MyEntriesPage : AppPage
{
    public MyEntriesPage(MyEntriesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    // Round 2: tap an entry to see its detail (and request a correction).
    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is EntryRow row) _ = ((MyEntriesViewModel)BindingContext).OpenAsync(row);
        if (sender is CollectionView list) list.SelectedItem = null;
    }
}
