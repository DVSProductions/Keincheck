using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaMcp.Hub;

/// <summary>
/// The hub's status window (built in code to match the project's XAML-free style). It
/// shows the "AI is driving X" banner, the live client list with per-row launch /
/// restart / read-only / make-active controls, and a scrolling audit log of AI tool
/// calls. Closing the window hides it (the daemon stays in the tray).
/// </summary>
public sealed class HubWindow : Window
{
    private readonly HubViewModel _vm;

    public HubWindow(HubViewModel vm)
    {
        _vm = vm;
        DataContext = vm;

        Title = "AvaloniaMcp Hub";
        Width = 640;
        Height = 520;
        MinWidth = 480;
        MinHeight = 360;

        Content = BuildLayout();
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,160"),
        };

        // --- driving banner -------------------------------------------------
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x3A)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                [!TextBlock.TextProperty] = new Binding(nameof(HubViewModel.DrivingText)),
            },
        };
        Grid.SetRow(banner, 0);
        root.Children.Add(banner);

        // --- client list ----------------------------------------------------
        var list = new ListBox
        {
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(HubViewModel.Clients)),
            ItemTemplate = new FuncDataTemplate<ClientRow>((row, _) => BuildClientRow(row), supportsRecycling: true),
        };
        Grid.SetRow(list, 1);
        root.Children.Add(list);

        // --- audit header ---------------------------------------------------
        var auditHeader = new TextBlock
        {
            Text = "Audit log — AI tool calls",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(2, 12, 0, 4),
        };
        Grid.SetRow(auditHeader, 2);
        root.Children.Add(auditHeader);

        // --- audit log ------------------------------------------------------
        var audit = new ListBox
        {
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(HubViewModel.Audit)),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
        };
        var auditScroll = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = audit,
        };
        Grid.SetRow(auditScroll, 3);
        root.Children.Add(auditScroll);

        return root;
    }

    private Control BuildClientRow(ClientRow row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(2, 4),
        };

        // name + status
        var info = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            [!TextBlock.TextProperty] = new Binding(nameof(ClientRow.Display)),
        });
        info.Children.Add(new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.Gray,
            [!TextBlock.TextProperty] = new Binding(nameof(ClientRow.StatusLine)),
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // make-active
        var activate = new Button { Content = "Make active", Margin = new Thickness(4, 0, 0, 0) };
        activate.Click += (_, _) => _vm.SelectActive(row.ClientId);
        Grid.SetColumn(activate, 1);
        grid.Children.Add(activate);

        // launch
        var launch = new Button { Content = "Launch", Margin = new Thickness(4, 0, 0, 0) };
        launch.Click += async (_, _) => await _vm.LaunchAsync(row.ClientId);
        Grid.SetColumn(launch, 2);
        grid.Children.Add(launch);

        // restart
        var restart = new Button { Content = "Restart", Margin = new Thickness(4, 0, 0, 0) };
        restart.Click += async (_, _) => await _vm.RestartAsync(row.ClientId);
        Grid.SetColumn(restart, 3);
        grid.Children.Add(restart);

        // read-only toggle
        var readOnly = new CheckBox
        {
            Content = "Read-only",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton_IsCheckedProperty()] = new Binding(nameof(ClientRow.ReadOnly)) { Mode = BindingMode.OneWay },
        };
        readOnly.IsCheckedChanged += (_, _) =>
        {
            var v = readOnly.IsChecked ?? false;
            if (v != row.ReadOnly)
                _vm.SetReadOnly(row.ClientId, v);
        };
        Grid.SetColumn(readOnly, 4);
        grid.Children.Add(readOnly);

        return grid;
    }

    private static Avalonia.AvaloniaProperty ToggleButton_IsCheckedProperty()
        => Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;
}
