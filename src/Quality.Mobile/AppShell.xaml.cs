using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Pages;

namespace Quality.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Stages, typeof(StagePage));
        Routing.RegisterRoute(Routes.Machines, typeof(MachinePage));
        Routing.RegisterRoute(Routes.Entry, typeof(EntryFormPage));
        Routing.RegisterRoute(Routes.Confirmation, typeof(ConfirmationPage));
        Routing.RegisterRoute(Routes.MyEntries, typeof(MyEntriesPage));
        Routing.RegisterRoute(Routes.Settings, typeof(SettingsPage));
        Routing.RegisterRoute(Routes.Admin, typeof(AdminPhenomenaPage));
        Routing.RegisterRoute(Routes.Editor, typeof(PhenomenonEditorPage));
        Routing.RegisterRoute(Routes.AdminMachines, typeof(AdminMachinesPage));
        Routing.RegisterRoute(Routes.MachineEditor, typeof(MachineEditorPage));
        Routing.RegisterRoute(Routes.AdminUsers, typeof(AdminUsersPage));
        Routing.RegisterRoute(Routes.UserEditor, typeof(UserEditorPage));
    }
}
