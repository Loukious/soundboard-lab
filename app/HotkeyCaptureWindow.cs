using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundboardLab.Services;

namespace SoundboardLab;

internal sealed class HotkeyCaptureWindow : Window
{
    private readonly HotkeyCaptureState state;
    private readonly TextBox captureButton = new() { IsReadOnly=true,IsReadOnlyCaretVisible=true,MinWidth = 0, Height = 23, Margin = new(0),Padding=new(4,0,2,0),VerticalContentAlignment=VerticalAlignment.Center };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, MinHeight=16, Margin = new(4, 5, 4, 8) };
    private readonly Func<string, HotkeyConflict> validate;
    private readonly Func<string, bool> confirm;
    private IDisposable? recording;
    internal string Gesture => state.Gesture;

    internal HotkeyCaptureWindow(string initial, GlobalHotkeyService hotkeys, bool distinguishSides,
        Func<string, HotkeyConflict> validate, Func<string, bool>? confirm = null)
    {
        state = new(initial, distinguishSides); this.validate = validate;
        this.confirm = confirm ?? (text => MessageBox.Show(this, text, "Edit hotkey", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        Title = initial.Length==0?"Set hotkey":"Edit hotkey"; Width = initial.Length==0?266:294; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = SystemColors.ControlBrush;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SoundboardLab;component/SettingsResources.xaml", UriKind.Relative) });
        var body = new StackPanel { Margin = new(8) }; Content = body;
        var group=new GroupBox {Header="Press a key",Padding=new(6,4,6,5),Margin=new(0),Foreground=new SolidColorBrush(Color.FromRgb(0,35,74))};body.Children.Add(group);
        var inputRow=new Grid();inputRow.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});inputRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});group.Content=inputRow;
        AutomationProperties.SetAutomationId(captureButton, "HotkeyCapture"); captureButton.Text = state.Gesture;
        var chrome=new FrameworkElementFactory(typeof(Border));chrome.Name="CaptureChrome";chrome.SetValue(Border.BackgroundProperty,Brushes.White);chrome.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(220,220,220)));chrome.SetValue(Border.BorderThicknessProperty,new Thickness(0,0,0,1));chrome.SetValue(Border.PaddingProperty,new Thickness(4,0,2,0));
        var value=new FrameworkElementFactory(typeof(ScrollViewer));value.Name="PART_ContentHost";value.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);chrome.AppendChild(value);
        var captureTemplate=new ControlTemplate(typeof(TextBox)){VisualTree=chrome};var focus=new Trigger {Property=IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(0,120,215)),"CaptureChrome"));focus.Setters.Add(new Setter(Border.BorderThicknessProperty,new Thickness(0,0,0,2),"CaptureChrome"));captureTemplate.Triggers.Add(focus);captureButton.Template=captureTemplate;
        inputRow.Children.Add(captureButton);
        var remove = new Button { Content = "X", ToolTip="Remove hotkey",Width=20,MinWidth=0,Height=21,MinHeight=0,Padding=new(0), Margin = new(6,0,0,0),Visibility=initial.Length>0?Visibility.Visible:Visibility.Collapsed };Grid.SetColumn(remove,1);
        AutomationProperties.SetAutomationId(remove, "RemoveHotkey");
        remove.Click += (_, _) => { state.Remove(); Refresh(); }; inputRow.Children.Add(remove);
        body.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true }; AutomationProperties.SetAutomationId(ok, "AcceptHotkey");
        ok.Click += (_, _) => { if (ValidateSelection()) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); body.Children.Add(buttons);
        captureButton.PreviewKeyDown += (_, e) => {
            // Fallback for routed events/accessibility; physical keys are consumed
            // by the hook before WPF's default Enter/Escape button handling.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0) Record(new(vk, ModifierFlags(), true, e.IsRepeat)); e.Handled = true;
        };
        captureButton.PreviewMouseDown += (_, e) => {
            // A first left click enters the capture field; subsequent clicks bind it.
            bool ready = captureButton.IsKeyboardFocusWithin; captureButton.Focus();
            if (ready || e.ChangedButton != MouseButton.Left) {
                int key = e.ChangedButton switch { MouseButton.Left => 256, MouseButton.Right => 257, MouseButton.Middle => 258, MouseButton.XButton1 => 259, _ => 260 };
                Record(new(key, ModifierFlags(), true));
            }
            e.Handled = true;
        };
        captureButton.PreviewMouseWheel += (_, e) => { Record(new(e.Delta > 0 ? 261 : 262, ModifierFlags(), true)); e.Handled = true; };
        captureButton.ContextMenu = null;
        Loaded += (_, _) => {
            captureButton.Focus();captureButton.SelectAll();
            try { recording = hotkeys.BeginCapture(input => {
                if (!IsActive || !captureButton.IsKeyboardFocusWithin) return false;
                Record(input); return true;
            }); } catch (Exception ex) { message.Text = ex.Message; }
        };
        Closed += (_, _) => { recording?.Dispose(); recording = null; };
    }
    private static int ModifierFlags()
    {
        int flags = 0;
        foreach (var (key, bit) in new[] { (Key.LeftCtrl, 1), (Key.LeftShift, 2), (Key.LeftAlt, 4), (Key.LWin, 8), (Key.RightCtrl, 16), (Key.RightShift, 32), (Key.RightAlt, 64), (Key.RWin, 128) })
            if (Keyboard.IsKeyDown(key)) flags |= bit;
        return flags;
    }
    internal void Record(HotkeyInput input) { state.Record(input); Refresh(); }
    private void Refresh() { captureButton.Text = state.Removed?"":state.Display;captureButton.SelectAll(); message.Text = ""; }
    internal bool ValidateSelection()
    {
        if (!state.CanAccept) { message.Text = "You must specify a key. Modifier like Ctrl, Alt or Shift don't count."; return false; }
        var conflict = validate(state.Gesture);
        if (conflict.Blocked) { message.Text = conflict.Message; return false; }
        return !conflict.Overwrite || confirm(conflict.Message);
    }
}
