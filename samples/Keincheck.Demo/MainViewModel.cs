using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Keincheck.Demo;

/// <summary>
/// DataContext for <see cref="MainWindow"/>. Deliberately small but
/// representative: a few two-way-bound scalars, a click counter the button
/// handler mutates, and an observable item collection for the ListBox — enough
/// surface for the <c>get_data_context</c> tool to read something meaningful and
/// for property/binding tools to have live targets.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _name = "Avalonia";
    private bool _isSubscribed = true;
    private int _clickCount;
    private string _statusMessage = "Ready.";
    private DemoItem? _selectedItem;

    public MainViewModel()
    {
        Items = new ObservableCollection<DemoItem>
        {
            new("Alpha", 1),
            new("Bravo", 2),
            new("Charlie", 3),
            new("Delta", 4),
        };
        _selectedItem = Items[0];
    }

    /// <summary>Two-way bound to the demo <c>TextBox</c>.</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>Two-way bound to the demo <c>CheckBox</c>.</summary>
    public bool IsSubscribed
    {
        get => _isSubscribed;
        set => SetField(ref _isSubscribed, value);
    }

    /// <summary>
    /// Mutated by the button's click handler in code-behind; bound one-way into
    /// a TextBlock so a tool can observe the side effect of an invoke/click.
    /// </summary>
    public int ClickCount
    {
        get => _clickCount;
        set => SetField(ref _clickCount, value);
    }

    /// <summary>Free-form status line updated by the button handler.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    /// <summary>Items shown in the demo <c>ListBox</c>.</summary>
    public ObservableCollection<DemoItem> Items { get; }

    /// <summary>Two-way bound to the ListBox selection.</summary>
    public DemoItem? SelectedItem
    {
        get => _selectedItem;
        set => SetField(ref _selectedItem, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(propertyName);
    }
}

/// <summary>A trivial record-like item for the ListBox.</summary>
public sealed class DemoItem
{
    public DemoItem(string label, int value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public int Value { get; }

    /// <summary>Shown directly via the ListBox item template / ToString.</summary>
    public override string ToString() => $"{Label} ({Value})";
}
