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
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            await DisplayAlertAsync("Camera needed", "Allow camera access to scan machine labels, or choose the machine from the list.", "OK");
    }

    private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var text = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(text)) return;
        MainThread.BeginInvokeOnMainThread(async () => await _vm.OnScannedAsync(text));
    }
}
