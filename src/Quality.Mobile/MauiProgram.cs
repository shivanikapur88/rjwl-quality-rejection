using Quality.Mobile.Core.Infrastructure;
using Quality.Mobile.Core.Services;
using Quality.Mobile.Core.ViewModels;
using Quality.Mobile.Pages;
using Quality.Mobile.Platform;

namespace Quality.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if DEBUG
        // Debug builds only: write unhandled exceptions to the debug output so they show in Visual Studio's Output window.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => System.Diagnostics.Debug.WriteLine("QR UNHANDLED: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => System.Diagnostics.Debug.WriteLine("QR UNOBSERVED TASK: " + e.Exception);
#endif
        var b = MauiApp.CreateBuilder();
        b.UseMauiApp<App>();
        ZXing.Net.Maui.Controls.CameraViewExtensions.UseBarcodeReader(b);   // round 2 (#8): camera QR scanning

        var s = b.Services;
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<ISecureStore, MauiSecureStore>();
        s.AddSingleton<IAppSettings, MauiAppSettings>();
        s.AddSingleton<INavigator, ShellNavigator>();
        s.AddSingleton<IDialogs, MauiDialogs>();
        s.AddSingleton<ISession, Session>();
        s.AddSingleton(_ => new HttpClient(CreateHandler()));
        s.AddSingleton<IApiClient, ApiClient>();
        s.AddSingleton<SessionExpiryHandler>();
        s.AddSingleton<EntryDraftStore>();   // keeps an entry interrupted by session expiry (in memory)

        s.AddTransient<LoginViewModel>(); s.AddTransient<LoginPage>();
        s.AddTransient<HomeViewModel>(); s.AddTransient<HomePage>();
        s.AddTransient<StageViewModel>(); s.AddTransient<StagePage>();
        s.AddTransient<MachineViewModel>(); s.AddTransient<MachinePage>();
        s.AddTransient<EntryFormViewModel>(); s.AddTransient<EntryFormPage>();
        s.AddTransient<ConfirmationViewModel>(); s.AddTransient<ConfirmationPage>();
        s.AddTransient<MyEntriesViewModel>(); s.AddTransient<MyEntriesPage>();
        s.AddTransient<SettingsViewModel>(); s.AddTransient<SettingsPage>();
        s.AddTransient<AdminPhenomenaViewModel>(); s.AddTransient<AdminPhenomenaPage>();
        s.AddTransient<PhenomenonEditorViewModel>(); s.AddTransient<PhenomenonEditorPage>();
        s.AddTransient<AdminMachinesViewModel>(); s.AddTransient<AdminMachinesPage>();
        s.AddTransient<MachineEditorViewModel>(); s.AddTransient<MachineEditorPage>();
        s.AddTransient<AdminUsersViewModel>(); s.AddTransient<AdminUsersPage>();
        s.AddTransient<UserEditorViewModel>(); s.AddTransient<UserEditorPage>();
        s.AddTransient<EntryDetailViewModel>(); s.AddTransient<EntryDetailPage>();   // round 2
        s.AddTransient<CorrectionsViewModel>(); s.AddTransient<CorrectionsPage>();
        s.AddTransient<SummaryViewModel>(); s.AddTransient<SummaryPage>();
        s.AddTransient<ScanViewModel>(); s.AddTransient<ScanPage>();

        var app = b.Build();
        app.Services.GetRequiredService<SessionExpiryHandler>();   // subscribe once so an expired token returns the user to login
        return app;
    }

    private static HttpMessageHandler CreateHandler()
    {
#if DEBUG
        // Development only: accept the ASP.NET Core dev certificate, and ONLY for the emulator host alias / localhost.
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || msg.RequestUri?.Host is "10.0.2.2" or "localhost",
        };
#else
        return new HttpClientHandler();   // Release: normal certificate validation, no exceptions
#endif
    }
}
