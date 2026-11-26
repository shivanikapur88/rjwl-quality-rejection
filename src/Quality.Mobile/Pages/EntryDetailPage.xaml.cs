// Round 2 (new).
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class EntryDetailPage : AppPage
{
    public EntryDetailPage(EntryDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
