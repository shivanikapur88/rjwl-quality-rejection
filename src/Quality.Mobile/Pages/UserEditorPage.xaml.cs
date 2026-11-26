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
}
