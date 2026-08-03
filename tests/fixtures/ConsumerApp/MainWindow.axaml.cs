using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ConsumerApp;

public partial class MainWindow : Window
{
    private int _presses;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>An observable side effect, so the CI job can prove the click really landed.</summary>
    private void OnPress(object? sender, RoutedEventArgs e)
    {
        _presses++;
        this.FindControl<TextBlock>("ConsumerLabel")!.Text = $"Presses: {_presses}";
    }
}
