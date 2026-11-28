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

    // App design round: the text next to the checkbox toggles it too.
    private void OnShowDecidedTapped(object? sender, TappedEventArgs e)
    {
        var vm = (CorrectionsViewModel)BindingContext;
        vm.ShowDecided = !vm.ShowDecided;
    }
}
