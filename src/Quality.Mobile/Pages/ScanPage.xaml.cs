// Round 2 (#8): camera scanning of machine QR labels with ZXing.Net.Maui.
using Quality.Mobile.Core.ViewModels;
using ZXing.Net.Maui;

namespace Quality.Mobile.Pages;

public partial class ScanPage : AppPage
{
    private readonly ScanViewModel _vm;

    public ScanPage(ScanViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        Camera.Options = new BarcodeReaderOptions { Formats = BarcodeFormat.QrCode, AutoRotate = true, Multiple = false };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try   // async void: a permission or alert failure must not crash the app
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
                await DisplayAlertAsync("Camera needed", "Allow camera access to scan machine labels, or choose the machine from the list.", "OK");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Camera permission check failed: " + ex); }
    }

    private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var text = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(text)) return;
        MainThread.BeginInvokeOnMainThread(async () => await _vm.OnScannedAsync(text));
    }
}
