using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SoundboardLab.Audio;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private Window CreateSetupModeWindow(DeviceRow device,Action refresh)
    {
        var dialog=new Window {Title=device.Name,Width=458,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground};
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/SoundboardLab;component/SettingsResources.xaml",UriKind.Relative)});
        var panel=new StackPanel {Margin=new(10)};dialog.Content=panel;
        panel.Children.Add(new Border {Background=new SolidColorBrush(Color.FromRgb(235,177,79)),Padding=new(8),Margin=new(-10,-10,-10,10),Child=new TextBlock {Text="⚠  Change only if you experience compatibility issues.",Foreground=Brushes.Black,FontSize=16,TextWrapping=TextWrapping.Wrap}});
        string current=CaptureDeviceConfiguration.GetEffectSlot(device.Id);
        var value=new TextBlock {Text=CaptureDeviceConfiguration.IsInstalled(device.Id)?"Good":"Repair required",Foreground=CaptureDeviceConfiguration.IsInstalled(device.Id)?Brushes.Green:Brushes.Red,VerticalAlignment=VerticalAlignment.Center};
        var row=Row(panel,"Setup mode:",78);var controls=new StackPanel {Orientation=Orientation.Horizontal};Grid.SetColumn(controls,1);row.Children.Add(controls);
        var mode=new ComboBox {ItemsSource=new[]{"PostMix","Stream","Mode","Endpoint"},SelectedItem=current,Width=105};AutomationProperties.SetAutomationId(mode,"CaptureSetupMode");controls.Children.Add(mode);
        var applyMode=new Button {Content="Apply"};applyMode.Click+=async(_,_)=>{
            dialog.IsEnabled=false;
            try{await main.ChangeCaptureSetupModeAsync(device.Id,(string)mode.SelectedItem);current=(string)mode.SelectedItem;refresh();value.Text=CaptureDeviceConfiguration.IsInstalled(device.Id)?"Good":"Repair required";value.Foreground=CaptureDeviceConfiguration.IsInstalled(device.Id)?Brushes.Green:Brushes.Red;applyModeState();}
            catch(Exception ex){Dialogs.Error(dialog,ex);}
            finally{dialog.IsEnabled=true;}
        };controls.Children.Add(applyMode);
        void applyModeState()=>applyMode.IsEnabled=mode.SelectedItem as string!=current;
        mode.SelectionChanged+=(_,_)=>applyModeState();applyModeState();
        var status=Row(panel,"Status:",44);Grid.SetColumn(value,1);status.Children.Add(value);
        var edit=Button("Windows Audio Preferences",(_,_)=>{try{Process.Start(new ProcessStartInfo("control.exe","mmsys.cpl,,1"){UseShellExecute=true});}catch(Exception ex){Dialogs.Error(dialog,ex);}});edit.HorizontalAlignment=HorizontalAlignment.Left;panel.Children.Add(edit);
        var description=new TextBlock {Text="Apply configures this recording device. Reopen recording applications after changing the mode. If Windows keeps the old configuration, use Restart Win-Audio from the device menu.",TextWrapping=TextWrapping.Wrap,Margin=new(2,10,2,14)};panel.Children.Add(description);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};buttons.Children.Add(Button("OK",(_,_)=>dialog.Close()));buttons.Children.Add(Button("Cancel",(_,_)=>dialog.Close()));panel.Children.Add(buttons);
        Localization.Apply(dialog);return dialog;
    }
}
