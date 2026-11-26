// Round 2 (new).
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class SummaryPage : AppPage
{
    public SummaryPage(SummaryViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
