using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PVOMR.Core.Score;

namespace PVOMR.Gui;

/// <summary>Asks what to put into the MusicXML besides the notes and rests; the choice is remembered between sessions.</summary>
public sealed class ExportDialog : Window
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PVOMR", "export.json");
    private readonly List<(CheckBox box, Action<ExportOptions, bool> set, Func<ExportOptions, bool> get)> _items = new();
    private readonly ExportOptions _options;

    private ExportDialog(ExportOptions options, int recognisedPages, int totalPages)
    {
        _options = options;
        Title = "Export MusicXML";
        Width = 420; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = recognisedPages < totalPages
            ? $"{recognisedPages} of {totalPages} pages are recognised; only those are exported (use \"Recognise all pages\" first for the whole file)."
            : $"All {totalPages} page(s) are exported.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Notes and rests are always exported. Include also:", FontWeight = Avalonia.Media.FontWeight.Bold });
        Add(panel, "Dynamics and hairpins", "MuseScore plays pp and ff very differently; leave this off to hear every note at one volume", (o, v) => o.Dynamics = v, o => o.Dynamics);
        Add(panel, "Articulations and fermatas", "staccato dots, accents, fermatas", (o, v) => o.Articulations = v, o => o.Articulations);
        Add(panel, "Slurs", null, (o, v) => o.Slurs = v, o => o.Slurs);
        Add(panel, "Ties", "a wrong tie joins two notes into one; leave off to hear each note", (o, v) => o.Ties = v, o => o.Ties);
        Add(panel, "Text directions", "tempo words and other text", (o, v) => o.Text = v, o => o.Text);
        Add(panel, "Repeat signs and bar line styles", "a misread repeat makes playback jump; off writes plain bar lines", (o, v) => o.Repeats = v, o => o.Repeats);
        Add(panel, "Tuplet brackets", "the durations keep their tuplet ratio either way", (o, v) => o.TupletBrackets = v, o => o.TupletBrackets);
        Add(panel, "Beams", "visual only; without them the notes are flagged", (o, v) => o.Beams = v, o => o.Beams);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var all = new Button { Content = "All on" }; all.Click += (_, _) => { foreach (var it in _items) it.box.IsChecked = true; };
        var notesOnly = new Button { Content = "Notes only" }; notesOnly.Click += (_, _) => { foreach (var it in _items) it.box.IsChecked = false; };
        var ok = new Button { Content = "Export…", IsDefault = true }; ok.Click += (_, _) => { Apply(); Save(); Close(true); };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => Close(false);
        buttons.Children.Add(all); buttons.Children.Add(notesOnly); buttons.Children.Add(cancel); buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private void Add(StackPanel panel, string label, string? hint, Action<ExportOptions, bool> set, Func<ExportOptions, bool> get)
    {
        var box = new CheckBox { Content = label, IsChecked = get(_options) };
        panel.Children.Add(box);
        if (hint != null) panel.Children.Add(new TextBlock { Text = hint, Margin = new Thickness(28, -4, 0, 2), FontSize = 12, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        _items.Add((box, set, get));
    }

    private void Apply() { foreach (var (box, set, _) in _items) set(_options, box.IsChecked == true); }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_options, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a failed save only loses the remembered choice */ }
    }

    private static ExportOptions LoadSaved()
    {
        try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<ExportOptions>(File.ReadAllText(SettingsPath)) ?? new ExportOptions(); }
        catch { }
        return new ExportOptions();
    }

    /// <summary>Shows the dialog; returns the chosen options, or null when cancelled.</summary>
    public static async Task<ExportOptions?> ShowAsync(Window owner, int recognisedPages, int totalPages)
    {
        var dialog = new ExportDialog(LoadSaved(), recognisedPages, totalPages);
        bool ok = await dialog.ShowDialog<bool>(owner);
        return ok ? dialog._options : null;
    }
}
