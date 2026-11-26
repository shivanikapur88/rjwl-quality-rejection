// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026); the original source file was not in the package.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Pages;

public partial class MachinePage : AppPage
{
    public MachinePage(MachineViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Quality.Core.Dtos.MachineDto item)
            ((MachineViewModel)BindingContext).SelectMachineCommand.Execute(item);
        if (sender is CollectionView list) list.SelectedItem = null;   // allow choosing the same row again
    }
}
