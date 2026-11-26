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
}
