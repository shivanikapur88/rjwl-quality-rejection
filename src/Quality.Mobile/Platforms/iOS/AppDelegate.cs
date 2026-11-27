using Foundation;

namespace Quality.Mobile;

/// <summary>iOS entry point (added for iOS support; builds only on a Mac with Xcode, see docs/BUILD_AND_DEPLOY.md §iOS).</summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
