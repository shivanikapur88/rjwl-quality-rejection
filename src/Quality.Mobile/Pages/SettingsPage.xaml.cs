// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class SettingsPage : AppPage
{
    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.VersionText = "Version " + AppInfo.Current.VersionString;
    }
}
