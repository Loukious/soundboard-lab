using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SoundboardLab.Models;
using SoundboardLab.Services;
namespace SoundboardLab;

internal sealed class AutoKeyEditorWindow:Window
{
    public AutoKeyEntry Value {get;private set;}
    public AutoKeyEditorWindow(AutoKeyEntry? original=null,AppPreferences? preferences=null)
    {
        Value=original is null?new():JsonSerializer.Deserialize<AutoKeyEntry>(JsonSerializer.Serialize(original))!;
        Title="Auto Keys";Width=380;Height=430;MinWidth=360;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=System.Windows.Media.Brushes.WhiteSmoke;
        Resources.MergedDictionaries.Add(new(){Source=new Uri("/SoundboardLab;component/SettingsResources.xaml",UriKind.Relative)});
        var root=new DockPanel {Margin=new(12)};Content=root;var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
        var panel=new StackPanel();root.Children.Add(new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var what=new StackPanel();panel.Children.Add(new GroupBox {Header="What to press",Content=what});var key=new TextBox {Text=Value.Key,Margin=new(6)};what.Children.Add(key);
        var conditions=new StackPanel();panel.Children.Add(new GroupBox {Header="Conditions",Content=conditions});
        var enabled=new CheckBox {Content="Enabled",IsChecked=Value.Enabled};var only=new CheckBox {Content="Press only if sound is played by hotkey",IsChecked=Value.OnlyHotkey};conditions.Children.Add(enabled);conditions.Children.Add(only);
        conditions.Children.Add(new TextBlock {Text="How to press:"});var how=new ComboBox {ItemsSource=new[]{"Press and hold","Press before and after"},SelectedItem=Value.Type};conditions.Children.Add(how);
        conditions.Children.Add(new TextBlock {Text="When to press:",Margin=new(0,8,0,0)});var when=new ComboBox {ItemsSource=new[]{"Always","Foreground application is fullscreen","Certain application is running","Certain application is in foreground"},SelectedItem=Value.Condition};conditions.Children.Add(when);
        var apps=new TextBox {Text=Value.Applications,AcceptsReturn=true,MinHeight=54,ToolTip="Application names separated by commas or new lines"};conditions.Children.Add(apps);
        var add=new Button {Content="Add application…"};add.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog {Filter="Applications|*.exe"};if(dialog.ShowDialog(this)==true)apps.Text+=(apps.Text.Length>0?", ":"")+System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);};conditions.Children.Add(add);
        void Update(){bool show=when.SelectedItem?.ToString()?.StartsWith("Certain")??false;apps.Visibility=add.Visibility=show?Visibility.Visible:Visibility.Collapsed;}
        when.SelectionChanged+=(_,_)=>Update();Update();
        TextBox? legacyApps=null;IntegerSpinner? lead=null,tail=null;
        if(preferences is not null){var advanced=new StackPanel();panel.Children.Add(new Expander {Header="Timing and application filter",Content=advanced,Margin=new(6)});advanced.Children.Add(new TextBlock {Text="Only in applications:"});legacyApps=new TextBox {Text=preferences.AutoKeyProcesses};advanced.Children.Add(legacyApps);advanced.Children.Add(new TextBlock {Text="Key press lead time (ms):"});lead=new("AutoKeyDelayMs","Key press lead time",0,10000){Value=preferences.AutoKeyDelayMs};advanced.Children.Add(lead);advanced.Children.Add(new TextBlock {Text="Key release delay (ms):"});tail=new("PushToTalkTailMs","Key release delay",0,10000){Value=preferences.PushToTalkTailMs};advanced.Children.Add(tail);}
        var ok=new Button {Content="OK",IsDefault=true};ok.Click+=(_,_)=>{try{_=AutoKeyService.ParseGesture(key.Text);Value.Key=key.Text;Value.Enabled=enabled.IsChecked==true;Value.OnlyHotkey=only.IsChecked==true;Value.Type=how.SelectedItem?.ToString()??"Press and hold";Value.Condition=when.SelectedItem?.ToString()??"Always";Value.Applications=apps.Text;if(preferences is not null){preferences.AutoKeyProcesses=legacyApps?.Text??preferences.AutoKeyProcesses;preferences.AutoKeyDelayMs=lead?.Value??preferences.AutoKeyDelayMs;preferences.PushToTalkTailMs=tail?.Value??preferences.PushToTalkTailMs;}DialogResult=true;}catch(Exception ex){Dialogs.Error(this,ex);}};buttons.Children.Add(ok);var cancel=new Button {Content="Cancel",IsCancel=true};buttons.Children.Add(cancel);
    }
}
