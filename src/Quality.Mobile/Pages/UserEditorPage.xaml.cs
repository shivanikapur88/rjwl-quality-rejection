// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class UserEditorPage : AppPage
{
    public UserEditorPage(UserEditorViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    // App design round: a shop's name toggles its checkbox too.
    private void OnShopTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is ShopChoice choice) choice.IsSelected = !choice.IsSelected;
    }
}
