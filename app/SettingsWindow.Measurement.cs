using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SoundboardLab.Audio;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private VoiceMeasurementSession? voiceMeasurement;
    private CancellationTokenSource? fileMeasurement;
    private Button? voiceMeasureButton;
    private Panel? voiceMeasurePanel;
    private DecibelMeter? voiceMeter;
    private TextBlock? voiceMeasuredLabel,voiceWarning;
    private ComboBox? voiceChoice;
    private double measuredVoice=double.NegativeInfinity;
    private bool measurementClosing;
    internal void BeginVoiceMeasurement()=>ToggleVoiceMeasurement();
    private void ConfigureVoiceMeasurement(Panel group,ComboBox choice,Button button)
    {
        voiceChoice=choice;voiceMeasureButton=button;
        var panel=new StackPanel {Visibility=Visibility.Collapsed,Margin=new(24,8,0,12),HorizontalAlignment=HorizontalAlignment.Left,MaxWidth=380};voiceMeasurePanel=panel;
        AutomationProperties.SetAutomationId(panel,"VoiceMeasurementPanel");
        panel.Children.Add(new TextBlock {Text="Speak into your microphone as if you are talking to others in a voice application.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,6)});
        voiceMeter=new();panel.Children.Add(voiceMeter);
        voiceMeasuredLabel=new TextBlock {Text="Measured voice volume: 18.0 dB",HorizontalAlignment=HorizontalAlignment.Center};AutomationProperties.SetAutomationId(voiceMeasuredLabel,"MeasuredVoiceVolume");panel.Children.Add(voiceMeasuredLabel);
        voiceWarning=new TextBlock {Text="Is your microphone muted?",Foreground=Brushes.Red,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,5,0,0)};panel.Children.Add(voiceWarning);group.Children.Add(panel);
    }
    private void ToggleVoiceMeasurement()
    {
        if(voiceMeasurement is not null){StopVoiceMeasurement(true);return;}
        try{
            measurementClosing=false;measuredVoice=double.NegativeInfinity;
            voiceMeasurePanel!.Visibility=Visibility.Visible;
            voiceMeasureButton!.Content=new System.Windows.Shapes.Path {Data=Geometry.Parse("M0,0 H14 V14 H0 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,103,171)),Width=14,Height=14};
            voiceMeasureButton.ToolTip="Stop measurement";AutomationProperties.SetName(voiceMeasureButton,"Stop measurement");
            string? device=draft.CaptureDeviceIds.FirstOrDefault()??draft.CaptureDeviceId;
            voiceMeasurement=new(device,(level,measured)=>Dispatcher.BeginInvoke(()=>{
                if(voiceMeasurement is null||measurementClosing)return;measuredVoice=measured;
                double current=double.IsFinite(level)?Math.Max(18,level+112):18,average=double.IsFinite(measured)?Math.Max(18,measured+112):18;
                voiceMeter!.Level=current;voiceMeter.Measured=average;voiceMeter.InvalidateVisual();
                voiceMeasuredLabel!.Text=Localization.Text("Measured voice volume")+": "+average.ToString("0.0")+" dB";
                voiceWarning!.Visibility=average<42?Visibility.Visible:Visibility.Collapsed;
            }),ex=>Dispatcher.BeginInvoke(()=>{if(measurementClosing||!IsVisible)return;StopVoiceMeasurement(false);Dialogs.Error(this,ex);}));
        }catch(Exception ex){StopVoiceMeasurement(false);Dialogs.Error(this,ex);}
    }
    private void StopVoiceMeasurement(bool useResult)
    {
        measurementClosing=true;var session=voiceMeasurement;voiceMeasurement=null;session?.Dispose();
        if(voiceMeasurePanel is not null)voiceMeasurePanel.Visibility=Visibility.Collapsed;
        if(voiceMeasureButton is not null){voiceMeasureButton.Content=MeasurementIcon();voiceMeasureButton.ToolTip="Measure voice volume";AutomationProperties.SetName(voiceMeasureButton,Localization.Text("Measure voice volume"));}
        if(useResult&&double.IsFinite(measuredVoice)&&measuredVoice+112>=42){draft.TargetLufs=Math.Clamp(measuredVoice,-70,0);voiceChoice?.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget();Dirty();}
    }
    private void StopMeasurements(){StopVoiceMeasurement(false);fileMeasurement?.Cancel();}
    private async Task MeasureFixedAdjustmentInline(Button button,ComboBox choice,Panel results,TextBlock measured,TextBlock status,TextBlock count)
    {
        if(fileMeasurement is not null){fileMeasurement.Cancel();return;}
        using var cancellation=new CancellationTokenSource();fileMeasurement=cancellation;results.Visibility=Visibility.Visible;
        button.Content=new System.Windows.Shapes.Path {Data=Geometry.Parse("M0,0 H14 V14 H0 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,103,171)),Width=14,Height=14};
        AutomationProperties.SetName(button,"Stop file measurement");status.Text="Status: Analyzing";count.Text="Analyzed files: 0";
        try{
            double level=await main.MeasureLibraryLoudnessForPreferences((done,total)=>count.Text=Localization.Text("Analyzed files")+": "+done,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();draft.FixedAdjustmentDb=Math.Clamp(draft.TargetLufs-level,-30,30);choice.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget();
            measured.Text=Localization.Text("Measured volume")+": "+(level+112).ToString("0.0")+" dB";status.Text=Localization.Text("Status")+": "+Localization.Text("Completed");Dirty();
        }catch(OperationCanceledException){status.Text="Status: Canceled";}catch(Exception ex){status.Text="Status: "+ex.Message;}finally{fileMeasurement=null;button.Content=MeasurementIcon();AutomationProperties.SetName(button,"Measure volume adjustment");}
    }
}
