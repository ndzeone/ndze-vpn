using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NdzeVpn.Views;

/// <summary>Minimal themed single-line prompt, built in code so it needs no XAML of its own.</summary>
public static class InputDialog
{
    public static string? Ask(string title, string label, string initial = "")
    {
        var owner = Application.Current.MainWindow;

        var box = new TextBox
        {
            Text = initial,
            Style = (Style)Application.Current.FindResource("Input.Text"),
            Margin = new Thickness(0, 8, 0, 18)
        };

        var ok = new Button
        {
            Content = "Сохранить",
            Style = (Style)Application.Current.FindResource("Btn.Primary"),
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "Отмена",
            Style = (Style)Application.Current.FindResource("Btn.Outline"),
            IsCancel = true,
            Margin = new Thickness(0, 0, 10, 0)
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var header = new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("T.H2") };
        var caption = new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("T.Body"), Margin = new Thickness(0, 12, 0, 0) };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(header);
        panel.Children.Add(caption);
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var root = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        root.SetResourceReference(Border.BackgroundProperty, "B.Elevated");
        root.SetResourceReference(Border.BorderBrushProperty, "B.Border");

        var window = new Window
        {
            Owner = owner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = root
        };
        window.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };

        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true ? box.Text : null;
    }
}
