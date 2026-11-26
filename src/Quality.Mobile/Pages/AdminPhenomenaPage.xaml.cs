// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class AdminPhenomenaPage : AppPage
{
    public AdminPhenomenaPage(AdminPhenomenaViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private AdminPhenomenaViewModel Model => (AdminPhenomenaViewModel)BindingContext;
    private static AdminItem? Item(object? sender) => (sender as BindableObject)?.BindingContext as AdminItem;

    private void OnEdit(object? s, EventArgs e) { if (Item(s) is { } i) Model.EditCommand.Execute(i); }
    private void OnToggle(object? s, EventArgs e) { if (Item(s) is { } i) Model.ToggleCommand.Execute(i); }
    private void OnDelete(object? s, EventArgs e) { if (Item(s) is { } i) Model.DeleteCommand.Execute(i); }
}
