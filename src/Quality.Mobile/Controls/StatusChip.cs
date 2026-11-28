// App design round: a small rounded status chip. Its colours come from the Chip* styles in Styles.xaml and its tone from
// StatusTone (Quality.Mobile.Core), so the same status looks the same on every screen. Hidden while Text is empty.
using Quality.Mobile.Core.ViewModels;

namespace Quality.Mobile.Controls;

public sealed class StatusChip : Border
{
    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(StatusChip), null, propertyChanged: (b, _, _) => ((StatusChip)b).Apply());

    private readonly Label _label = new() { LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };

    public StatusChip()
    {
        Content = _label;
        Apply();
    }

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private void Apply()
    {
        var tone = StatusTone.For(Text).ToString();
        if (Lookup("Chip" + tone) is Style chip) Style = chip;
        if (Lookup("ChipText" + tone) is Style text) _label.Style = text;
        _label.Text = Text;
        IsVisible = !string.IsNullOrWhiteSpace(Text);
        SemanticProperties.SetDescription(this, Text);
    }

    private static Style? Lookup(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Style : null;
}
