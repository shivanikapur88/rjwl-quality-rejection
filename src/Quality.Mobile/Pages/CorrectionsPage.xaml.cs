// Round 2 (new).
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class CorrectionsPage : AppPage
{
    public CorrectionsPage(CorrectionsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
