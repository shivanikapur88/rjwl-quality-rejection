namespace Quality.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // App design round (A2): the palette is a light one; until a dark palette exists, the app always renders light,
        // whatever the phone's setting (no white text on light cards, light system dialogs).
        UserAppTheme = AppTheme.Light;
        // Entry form (A1): with the keyboard open the page is resized rather than panned, so its ScrollView keeps the
        // selected phenomenon, the quantity and Save reachable.
        Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.Application.SetWindowSoftInputModeAdjust(
            this, Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.WindowSoftInputModeAdjust.Resize);
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
#if DEBUG
        // Debug builds only: if the first screen cannot be created, show the error as text instead of a black screen.
        // Release builds never show exception details to users.
        try { return new Window(new AppShell()); }
        catch (Exception ex)
        {
            return new Window(new ContentPage
            {
                BackgroundColor = Colors.White,
                Content = new ScrollView { Content = new Label { Padding = 16, TextColor = Colors.Black, FontSize = 13, Text = "Startup error (debug build only)\n\n" + ex } },
            });
        }
#else
        return new Window(new AppShell());
#endif
    }
}
