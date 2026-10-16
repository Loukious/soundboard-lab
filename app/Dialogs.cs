using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoundboardLab;

internal static class Dialogs
{
    public static Window Window(string title, double width = 560, double height = 420) => new() { Title = title, Width = width, Height = height, MinWidth = 420, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var window = Window(title, 560, 230); window.Owner = owner; window.ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(3, 0, 3, 10) });
        var text = new TextBox { Text = initial }; panel.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var ok = new Button { Content = "Save", IsDefault = true };
        cancel.Click += (_, _) => window.DialogResult = false; ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons); window.Content = panel;
        window.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        return window.ShowDialog() == true ? text.Text : null;
    }
    public static void ShowText(Window owner, string title, string value)
    {
        var window = Window(title, 700, 520); window.Owner = owner;
        window.Content = new TextBox { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(16) };
        window.ShowDialog();
    }
    public static Button Button(Panel panel, string label, RoutedEventHandler handler) { var button = new Button { Content = label }; button.Click += handler; panel.Children.Add(button); return button; }
    public static TextBox Field(Panel panel, string label, string value, int lines = 1)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new(5, 10, 0, 2) });
        var text = new TextBox { Text = value, AcceptsReturn = lines > 1, MinHeight = lines > 1 ? 90 : 30, TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap }; panel.Children.Add(text); return text;
    }
    public static CheckBox Check(Panel panel, string label, bool value) { var check = new CheckBox { Content = label, IsChecked = value }; panel.Children.Add(check); return check; }
    public static double Number(TextBox text, double min, double max)
    {
        if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"Enter a number between {min} and {max}.");
        return value;
    }
    public static void Error(Window owner, Exception ex) => MessageBox.Show(owner, ex.Message, "Soundboard Lab", MessageBoxButton.OK, MessageBoxImage.Information);
}
