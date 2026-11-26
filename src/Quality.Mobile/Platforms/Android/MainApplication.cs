// Recovered from the supplied Quality.Mobile.dll (Debug build, 7 Nov 2026).
using Android.App;
using Android.Runtime;

namespace Quality.Mobile;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
