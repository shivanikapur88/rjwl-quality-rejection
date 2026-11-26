namespace Quality.Mobile;

public partial class App : Application
{
    public App() { InitializeComponent(); }
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
