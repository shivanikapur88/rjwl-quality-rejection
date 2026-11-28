using System.Xml.Linq;

namespace Quality.Tests.Unit;

/// <summary>
/// iOS cannot be built in this environment (Apple allows iOS builds only on macOS). These static checks keep the iOS settings
/// that matter for security and for not being closed by iOS: camera reason, HTTPS only, Keychain group = ApplicationId.
/// </summary>
public sealed class IosConfigurationTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "Quality.Mobile"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new DirectoryNotFoundException("repository root not found"), "src", "Quality.Mobile");
    }

    private static Dictionary<string, XElement> PlistDict(string file)
    {
        var dict = XDocument.Load(Path.Combine(Root(), "Platforms", "iOS", file)).Root!.Element("dict")!;
        var items = dict.Elements().ToList();
        var map = new Dictionary<string, XElement>();
        for (var i = 0; i + 1 < items.Count; i += 2) map[items[i].Value] = items[i + 1];
        return map;
    }

    [Fact]
    public void Info_plist_explains_the_camera_and_allows_no_cleartext()
    {
        var info = PlistDict("Info.plist");
        Assert.True(info.TryGetValue("NSCameraUsageDescription", out var cam) && cam.Value.Trim().Length > 20, "camera reason missing (iOS closes the app without it)");
        Assert.False(info.ContainsKey("NSAppTransportSecurity"), "no App Transport Security exceptions: HTTPS only, as on Android");
    }

    [Fact]
    public void Keychain_group_matches_the_application_id()
    {
        var csproj = XDocument.Load(Path.Combine(Root(), "Quality.Mobile.csproj"));
        var appId = csproj.Descendants("ApplicationId").Single().Value.Trim();
        var groups = PlistDict("Entitlements.plist")["keychain-access-groups"].Elements("string").Select(e => e.Value).ToList();
        Assert.Contains("$(AppIdentifierPrefix)" + appId, groups);
    }

    [Fact]
    public void Ios_target_is_opt_in_so_android_builds_elsewhere_are_unchanged()
    {
        var csproj = XDocument.Load(Path.Combine(Root(), "Quality.Mobile.csproj"));
        var tfms = csproj.Descendants("TargetFrameworks").ToList();
        Assert.Contains(tfms, t => t.Attribute("Condition") is null && t.Value == "net10.0-android");
        Assert.Contains(tfms, t => t.Value.Contains("net10.0-ios") && t.Attribute("Condition")!.Value.Contains("IsOSPlatform('osx')"));
    }
}
