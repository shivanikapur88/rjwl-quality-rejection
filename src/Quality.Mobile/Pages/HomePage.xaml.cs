// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class HomePage : AppPage
{
    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private void OnShopSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Quality.Core.Dtos.ShopDto item)
            ((HomeViewModel)BindingContext).SelectShopCommand.Execute(item);
        if (sender is CollectionView list) list.SelectedItem = null;   // allow choosing the same row again
    }
}
