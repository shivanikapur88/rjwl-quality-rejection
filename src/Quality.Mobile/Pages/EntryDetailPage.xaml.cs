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

    // App design round: the text next to the checkbox toggles it too.
    private void OnChangeRemarksTapped(object? sender, TappedEventArgs e)
    {
        var vm = (EntryDetailViewModel)BindingContext;
        vm.ChangeRemarks = !vm.ChangeRemarks;
    }
}
