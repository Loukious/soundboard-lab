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
    private readonly Button captureButton = new() { MinWidth = 200, Height = 35, Margin = new(0, 8, 0, 8) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) };
    private readonly Func<string, HotkeyConflict> validate;
    private readonly Func<string, bool> confirm;
    private IDisposable? recording;
    internal string Gesture => state.Gesture;

    internal HotkeyCaptureWindow(string initial, GlobalHotkeyService hotkeys, bool distinguishSides,
        Func<string, HotkeyConflict> validate, Func<string, bool>? confirm = null)
    {
        state = new(initial, distinguishSides); this.validate = validate;
        this.confirm = confirm ?? (text => MessageBox.Show(this, text, "Edit hotkey", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        Title = "Edit hotkey"; Width = 370; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = SystemColors.ControlBrush;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SoundboardLab;component/SettingsResources.xaml", UriKind.Relative) });
        var body = new StackPanel { Margin = new(12) }; Content = body;
        body.Children.Add(new TextBlock { Text = "Press a key" });
        AutomationProperties.SetAutomationId(captureButton, "HotkeyCapture"); captureButton.Content = state.Display;
        captureButton.Click += (_, _) => captureButton.Focus();
        body.Children.Add(captureButton);
        var remove = new Button { Content = "Remove hotkey", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0) };
        AutomationProperties.SetAutomationId(remove, "RemoveHotkey");
        remove.Click += (_, _) => { state.Remove(); Refresh(); }; body.Children.Add(remove);
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
            captureButton.Focus();
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
    private void Refresh() { captureButton.Content = state.Display; message.Text = ""; }
    internal bool ValidateSelection()
    {
        if (!state.CanAccept) { message.Text = "You must specify a key. Modifier like Ctrl, Alt or Shift don't count."; return false; }
        var conflict = validate(state.Gesture);
        if (conflict.Blocked) { message.Text = conflict.Message; return false; }
        return !conflict.Overwrite || confirm(conflict.Message);
    }
}
