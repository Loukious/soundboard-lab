using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using SoundboardLab.Audio;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private Action? updateForwarding;
    private bool forwardingPointerHeld,forwardingKeyHeld;
    private void BuildForwarding(Panel page)
    {
        var group=Group(page,"Audio forwarding");
        Explain(group,"Temporarily send app or Windows playback to your selected microphone. Forwarding starts off each time you open the app.");
        Choice(group,"Audio source:",["Foreground app","Whole PC"],draft.ForwardingSource,nameof(AppPreferences.ForwardingSource));
        var gainRow=Row(group,"Forwarding volume:");var gain=new Slider {Minimum=0,Maximum=2,TickFrequency=.01,IsSnapToTickEnabled=false,Width=180,HorizontalAlignment=HorizontalAlignment.Left};gain.SetBinding(RangeBase.ValueProperty,Bind(nameof(AppPreferences.ForwardingVolume)));AutomationProperties.SetAutomationId(gain,"ForwardingVolume");Grid.SetColumn(gain,1);gainRow.Children.Add(gain);gain.ValueChanged+=(_,_)=>Dirty();
        Check(group,"Mute my voice while forwarding",nameof(AppPreferences.ForwardingMuteVoice));
        var help=new StackPanel {Orientation=Orientation.Horizontal,Margin=new(2,2,0,8)};help.Children.Add(new TextBlock {Text="Toggle and push-to-forward hotkeys are in the Hotkeys tab.",TextWrapping=TextWrapping.Wrap,MaxWidth=430});help.Children.Add(HelpButton("ForwardingHelp","Audio forwarding","Foreground app captures the app selected when forwarding starts, including its child processes. Switching windows does not change the source. If starting from Preferences, the last foreground app is used.\n\nWhole PC captures Windows playback and excludes this application's playback. It also includes sound from voice-chat applications, so disable microphone monitoring to avoid a feedback loop.\n\nToggle audio forwarding stays on until toggled off. Push to forward audio works only while the assigned key or mouse button is held. Release a required modifier to stop a held shortcut. No recording is saved and Windows volume is not changed."));group.Children.Add(help);
        var controls=new StackPanel {Orientation=Orientation.Horizontal};group.Children.Add(controls);
        var toggle=new ToggleButton {Content="Toggle forwarding",Style=(Style)FindResource("ForwardingToggle"),MinWidth=138,Height=25,Margin=new(2)};AutomationProperties.SetAutomationId(toggle,"ToggleAudioForwarding");controls.Children.Add(toggle);
        var push=Button("Hold to forward",(_,_)=>{});AutomationProperties.SetAutomationId(push,"PushAudioForwarding");controls.Children.Add(push);
        var stop=Button("Stop",async(_,_)=>await main.StopForwardingAsync());AutomationProperties.SetAutomationId(stop,"StopAudioForwarding");controls.Children.Add(stop);
        var status=new TextBlock {Margin=new(2,7,0,0),TextWrapping=TextWrapping.Wrap};AutomationProperties.SetAutomationId(status,"AudioForwardingStatus");group.Children.Add(status);
        bool supported=AudioService.SupportsAudioForwarding;
        toggle.Click+=async(_,_)=>{await main.ToggleForwardingAsync(draft);Refresh();};
        push.PreviewMouseLeftButtonDown+=async(_,e)=>{e.Handled=true;forwardingPointerHeld=true;push.CaptureMouse();await main.HoldForwardingAsync("preferences-mouse",true,draft);};
        push.PreviewMouseLeftButtonUp+=async(_,e)=>{e.Handled=true;await ReleasePointer();};
        push.LostMouseCapture+=async(_,_)=>await ReleasePointer();
        push.PreviewKeyDown+=async(_,e)=>{if(e.Key!=Key.Space)return;e.Handled=true;if(e.IsRepeat)return;forwardingKeyHeld=true;await main.HoldForwardingAsync("preferences-key",true,draft);};
        push.PreviewKeyUp+=async(_,e)=>{if(e.Key!=Key.Space)return;e.Handled=true;await ReleaseKey();};
        push.LostKeyboardFocus+=async(_,_)=>await ReleaseKey();
        async Task ReleasePointer(){if(!forwardingPointerHeld)return;forwardingPointerHeld=false;if(push.IsMouseCaptured)push.ReleaseMouseCapture();await main.HoldForwardingAsync("preferences-mouse",false);}
        async Task ReleaseKey(){if(!forwardingKeyHeld)return;forwardingKeyHeld=false;await main.HoldForwardingAsync("preferences-key",false);}
        async void OnUnloaded(object? sender,RoutedEventArgs e){await ReleasePointer();await ReleaseKey();}
        push.Unloaded+=OnUnloaded;
        void Refresh(){toggle.IsEnabled=push.IsEnabled=supported;toggle.IsChecked=main.ForwardingLatched;stop.IsEnabled=main.ForwardingActive||main.ForwardingStarting;status.Text=supported?main.ForwardingStatus:"Audio forwarding requires Windows build 20348 or later.";}
        updateForwarding=Refresh;Refresh();
    }
}
