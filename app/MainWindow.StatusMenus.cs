using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SoundboardLab.Services;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SoundboardLab;

public partial class MainWindow
{
    private DispatcherTimer? statusMenuTimer;
    private FrameworkElement? pendingStatusMenu;
    private void ConfigureStatusMenus()
    {
        var hotkeyMenu=new ContextMenu();HotkeysBox.ContextMenu=hotkeyMenu;
        HotkeysBox.PreviewMouseRightButtonDown+=(_,_)=>CancelStatusMenu();
        hotkeyMenu.Opened+=(_,_)=>PopulateHotkeyStatusMenu(hotkeyMenu);
        PopulateHotkeyStatusMenu(hotkeyMenu);
        var autoMenu=new ContextMenu();AutoKeysBox.ContextMenu=autoMenu;
        AutoKeysBox.PreviewMouseRightButtonDown+=(_,_)=>CancelStatusMenu();
        autoMenu.Opened+=(_,_)=>PopulateAutoKeyStatusMenu(autoMenu);
        PopulateAutoKeyStatusMenu(autoMenu);
    }
    private void PopulateHotkeyStatusMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        Add(menu,Toggle("Hotkeys enabled",()=>HotkeysBox.IsChecked==true,value=>HotkeysBox.IsChecked=value),
            Toggle("Remember hotkey status throughout application starts",()=>Settings.RememberHotkeyStatus,value=>{Settings.RememberHotkeyStatus=value;preferenceStore.Save();}),
            Toggle("Pass hotkeys",()=>Settings.PassHotkeys,value=>{Settings.PassHotkeys=value;ConfigureHotkeys();preferenceStore.Save();}),
            new Separator(),Item("Hotkey overview",HotkeyOverview_Click),new Separator(),Item("Preferences",HotkeyPreferences_Click));
        Localization.Apply(menu);
    }
    private void PopulateAutoKeyStatusMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        Add(menu,Toggle("Auto Keys enabled",()=>AutoKeysBox.IsChecked==true,value=>AutoKeysBox.IsChecked=value),new Separator());
        foreach(var rule in Settings.AutoKeys){
            string label=rule.Key+" ➜ "+rule.Type;
            if(rule.Applications.Length>0)label+=" ["+rule.Applications.Replace('\n',',')+"]";
            menu.Items.Add(Toggle(label,()=>rule.Enabled,value=>{rule.Enabled=value;ConfigureAutoKeys();preferenceStore.Save();}));
        }
        if(Settings.AutoKeys.Count>0)menu.Items.Add(new Separator());
        Add(menu,Item("Troubleshooting",(_,_)=>ShowText("Auto Keys troubleshooting",AutoKeyTroubleshooting())),new Separator(),Item("Preferences",HotkeyPreferences_Click));
        Localization.Apply(menu);
    }
    private string AutoKeyTroubleshooting()
    {
        var lines=new List<string>{"Auto Keys: "+(Settings.AutoKeysEnabled?"enabled":"disabled"),"Play mode: "+Destination};
        if(Destination==Audio.PlaybackDestination.Speakers)lines.Add("Auto Keys are not pressed for speaker-only playback.");
        if(Settings.AutoKeys.Count==0){lines.Add(string.IsNullOrWhiteSpace(Settings.AutoKeyGesture)?"No Auto Keys are configured.":"Auto key: "+Settings.AutoKeyGesture);return string.Join("\n",lines);}
        var context=AutoKeyConditions.Capture();
        foreach(var rule in Settings.AutoKeys){
            string reason=!rule.Enabled?"disabled":!Settings.AutoKeysEnabled?"Auto Keys disabled":Destination==Audio.PlaybackDestination.Speakers?"speaker-only mode":!AutoKeyConditions.Matches(rule,true,true,context)?"application condition not met":"ready";
            if(rule.OnlyHotkey)reason+=" (hotkey playback only)";
            lines.Add(rule.Key+": "+reason);
        }
        return string.Join("\n",lines);
    }
    private void HotkeyPreferences_Click(object sender,RoutedEventArgs e)
    {
        var preferences=new SettingsWindow(this,audio,preferenceStore,ApplySettingsAsync){Owner=this};
        preferences.SelectPage("Hotkeys");preferences.ShowDialog();
    }
    private static void OpenStatusMenu(FrameworkElement target)
    {
        if(target.ContextMenu is not {} menu)return;
        menu.PlacementTarget=target;menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;
    }
    private void AutoKeys_Pressed(object sender,MouseButtonEventArgs e)
    {
        CancelStatusMenu();e.Handled=true;
        if(e.ClickCount%2==0)AutoKeysBox.IsChecked=AutoKeysBox.IsChecked!=true;
    }
    private void StatusIcon_Released(object sender,MouseButtonEventArgs e)
    {
        if(sender is not FrameworkElement target)return;
        e.Handled=true;
        // Wait through the double-click interval so a single-click menu cannot
        // steal the second click of the original's double-click toggle.
        if(e.ClickCount<2){
            CancelStatusMenu();pendingStatusMenu=target;
            statusMenuTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(GetDoubleClickTime())};
            statusMenuTimer.Tick+=(_,_)=>{var anchor=pendingStatusMenu;CancelStatusMenu();if(anchor is not null&&!closing&&IsActive&&anchor.IsVisible)OpenStatusMenu(anchor);};
            statusMenuTimer.Start();
        }
    }
    private void CancelStatusMenu(){statusMenuTimer?.Stop();statusMenuTimer=null;pendingStatusMenu=null;}
    [DllImport("user32.dll")]private static extern uint GetDoubleClickTime();
}
