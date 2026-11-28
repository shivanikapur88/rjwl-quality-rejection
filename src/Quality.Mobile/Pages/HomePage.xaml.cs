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

    // App design round (A3): the shops are a BindableLayout inside the page's ScrollView; a tap opens the shop.
    private void OnShopTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is Quality.Core.Dtos.ShopDto item)
            ((HomeViewModel)BindingContext).SelectShopCommand.Execute(item);
    }
}
