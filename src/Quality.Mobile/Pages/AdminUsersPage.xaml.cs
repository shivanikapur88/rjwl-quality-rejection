// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class AdminUsersPage : AppPage
{
    public AdminUsersPage(AdminUsersViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private AdminUsersViewModel Model => (AdminUsersViewModel)BindingContext;
    private static UserItem? Item(object? sender) => (sender as BindableObject)?.BindingContext as UserItem;

    private void OnEdit(object? s, EventArgs e) { if (Item(s) is { } i) Model.EditCommand.Execute(i); }
    private void OnToggle(object? s, EventArgs e) { if (Item(s) is { } i) Model.ToggleCommand.Execute(i); }
}
