using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Keincheck.Demo;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        // Set a real DataContext so get_data_context has something to read and
        // the two-way bindings on the controls have a backing model.
        DataContext = _viewModel;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Click handler for <c>CountButton</c>: mutates a bound ViewModel property
    /// (<see cref="MainViewModel.ClickCount"/>) so invoking the button via the
    /// MCP tools produces an observable, bindable side effect.
    /// </summary>
    private void OnIncrementClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClickCount++;
        _viewModel.StatusMessage = $"Incremented to {_viewModel.ClickCount}.";
    }
}
