using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GameModeMonitor.App;

// All confirmations are opt-in: Enter defaults to Cancel and Escape never accepts.
public sealed class ThemedDialog : Window
{
    public ThemedDialog(Window? owner, string title, string message, string? acceptLabel = null)
    {
        Title = title;
        if (owner?.IsVisible == true) Owner = owner;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Width = Math.Min(480, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 32);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = Brush("#EAF0FA");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        ShowInTaskbar = false;

        var panel = new Grid { Margin = new Thickness(22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = "◈  " + title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#3EC1F7"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 17)
        };
        heading.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        panel.Children.Add(heading);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(80, MaxHeight - 170)
        };
        scroll.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#BCCAE0"), LineHeight = 19, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = acceptLabel is null ? "Entendido" : "Cancelar", IsCancel = true, IsDefault = true, MinWidth = 100 };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        if (acceptLabel is not null)
        {
            var accept = new Button { Content = acceptLabel, Background = Brush("#238EC0"), MinWidth = 110, Margin = new Thickness(10, 0, 0, 0) };
            accept.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(accept);
        }
        Grid.SetRow(buttons, 2);
        panel.Children.Add(buttons);
        Content = new Border { Background = Brush("#0A1020"), BorderBrush = Brush("#29344D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = panel };
        Loaded += (_, _) => cancel.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    public static bool Confirm(Window? owner, string title, string message, string acceptLabel = "Continuar") => new ThemedDialog(owner, title, message, acceptLabel).ShowDialog() == true;
    public static void Notice(Window? owner, string title, string message) => new ThemedDialog(owner, title, message).ShowDialog();
}
