using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Keincheck.Browser.Demo;

/// <summary>
/// Code-behind for the validation surface. Every handler exists so a change an AI makes has a
/// visible, readable consequence — a click that only incremented a private field would prove
/// the click arrived but not that the app reacted.
/// </summary>
public partial class MainView : UserControl
{
    private int _count;

    public MainView()
    {
        InitializeComponent();

        AttachStatus.Text = App.AttachTarget is { } target
            ? $"Enrolled against {target}. The hub should list this app as 'browserdemo'."
            : "Not enrolled: this build has no embedded credential, so it is running unattached. "
              + "Build with KeincheckWebSocketEnroll=true and a hub whose WebSocket endpoint is on.";

        IncrementButton.Click += OnIncrement;
        ResetButton.Click += OnReset;
        NameBox.TextChanged += (_, _) =>
            EchoText.Text = string.IsNullOrEmpty(NameBox.Text)
                ? "(nothing typed yet)"
                : $"You typed: {NameBox.Text}";
        LevelSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                LevelText.Text = ((int)LevelSlider.Value).ToString();
        };
        ItemList.SelectionChanged += (_, _) =>
            SelectionText.Text = ItemList.SelectedItem is ListBoxItem item
                ? $"Selected: {item.Content}"
                : "(nothing selected)";
    }

    // InitializeComponent is NOT hand-written here, and that matters. Avalonia's generator emits
    // it together with the x:Name field assignments; supplying your own suppresses the generated
    // one, so every named field stays null and the first line of this constructor throws a
    // NullReferenceException during startup -- which in a browser is a black page and a stack
    // trace in the console, with nothing pointing at the cause.

    private void OnIncrement(object? sender, RoutedEventArgs e)
        => CounterText.Text = (++_count).ToString();

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _count = 0;
        CounterText.Text = "0";
    }
}
