using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CPUx.Core;

/// <summary>A label/value row shown inside an info card.</summary>
public sealed partial class InfoItem : ObservableObject
{
    public InfoItem(string label, string? value)
    {
        Label = label;
        _value = Fmt.OrDash(value);
    }

    public string Label { get; }

    [ObservableProperty]
    private string _value;

    [RelayCommand]
    private void CopyValue() => Clip.Set(Value);

    [RelayCommand]
    private void CopyRow() => Clip.Set($"{Label}: {Value}");
}

/// <summary>A titled card of label/value rows. Used by every page and by the report exporter.</summary>
public sealed partial class InfoSection : ObservableObject
{
    public InfoSection(string title, string? icon = null)
    {
        Title = title;
        Icon = icon;
    }

    public string Title { get; }
    public string? Icon { get; }
    public ObservableCollection<InfoItem> Items { get; } = new();

    public InfoSection Add(string label, string? value)
    {
        Items.Add(new InfoItem(label, value));
        return this;
    }

    /// <summary>Adds the row only when the value is meaningful.</summary>
    public InfoSection AddIf(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != Fmt.Dash) Items.Add(new InfoItem(label, value));
        return this;
    }

    public InfoItem? Find(string label) => Items.FirstOrDefault(i => i.Label == label);

    public void Set(string label, string? value)
    {
        var item = Find(label);
        if (item == null) Items.Add(new InfoItem(label, value));
        else item.Value = Fmt.OrDash(value);
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        int w = Items.Count == 0 ? 0 : Items.Max(i => i.Label.Length);
        foreach (var i in Items) sb.AppendLine($"  {i.Label.PadRight(w)}  {i.Value}");
        return sb.ToString();
    }

    [RelayCommand]
    private void Copy() => Clip.Set(ToText());
}

public static class Clip
{
    public static void Set(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Error("Clipboard", ex); }
    }
}
