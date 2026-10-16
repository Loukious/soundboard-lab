using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private static Button AudioIconButton(FrameworkElement icon,string name,string id,RoutedEventHandler click)
    {
        var button=Button("",click);button.Content=icon;button.Width=24;button.Height=24;button.MinWidth=0;button.MinHeight=0;button.Padding=new(2);
        button.ToolTip=name;AutomationProperties.SetName(button,name);AutomationProperties.SetAutomationId(button,id);return button;
    }
    private static FrameworkElement MeasurementIcon()
    {
        var icon=new Grid {Width=17,Height=13};
        icon.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M1,12 L16,2 V12 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,103,171))});
        icon.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M12,7 V10"),Stroke=Brushes.White,StrokeThickness=2});return icon;
    }
    private void BuildAudio(Panel page)
    {
        var group=Group(page,"Volume normalization");
        Explain(group,"Adjusts the sound file volume on playback to let others hear you and your sounds with the same loudness.");
        var dynamic=new RadioButton {Content="Dynamic volume adjustment (recommended)",GroupName="Normalization",IsChecked=draft.Normalize};
        var fixedVolume=new RadioButton {Content="Fixed volume adjustment (advanced)",GroupName="Normalization",IsChecked=!draft.Normalize};
        AutomationProperties.SetAutomationId(dynamic,"DynamicNormalization");AutomationProperties.SetAutomationId(fixedVolume,"FixedNormalization");
        var voice=Row(group,"Voice volume:",80);var voiceControls=new StackPanel {Orientation=Orientation.Horizontal};Grid.SetColumn(voiceControls,1);voice.Children.Add(voiceControls);
        var db=new ComboBox {IsEditable=true,Width=76,ItemsSource=Enumerable.Range(0,17).Select(i=>(59+i*3)+" dB")};
        AutomationProperties.SetAutomationId(db,"VoiceVolume");
        db.SetBinding(ComboBox.TextProperty,new Binding(nameof(AppPreferences.TargetLufs)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,Converter=new VoiceDbConverter(),ValidatesOnExceptions=true});voiceControls.Children.Add(db);
        var voiceButton=AudioIconButton(MeasurementIcon(),"Measure voice volume","MeasureVoiceVolume",(_,_)=>ToggleVoiceMeasurement());voiceControls.Children.Add(voiceButton);
        ConfigureVoiceMeasurement(group,db,voiceButton);
        Grid.SetIsSharedSizeScope(group,true);
        NormalizationHelpRow(group,dynamic,"DynamicNormalizationHelp","When to choose dynamic volume adjustment?",DynamicNormalizationHelp);
        NormalizationHelpRow(group,fixedVolume,"FixedNormalizationHelp","When to choose fixed volume adjustment?",FixedNormalizationHelp);
        var fixedPanel=new StackPanel {Margin=new(24,0,0,0)};group.Children.Add(fixedPanel);
        var fixedRow=Row(fixedPanel,"Volume adjustment by:",128);var fixedControls=new StackPanel {Orientation=Orientation.Horizontal};Grid.SetColumn(fixedControls,1);fixedRow.Children.Add(fixedControls);
        var fixedChoice=new ComboBox {IsEditable=true,Width=76,ItemsSource=Enumerable.Range(0,21).Select(i=>(-30+i*3)+" dB")};
        AutomationProperties.SetAutomationId(fixedChoice,"FixedAdjustmentDb");fixedChoice.ToolTip="Adjustment in dB (-30 to +30)";
        fixedChoice.SetBinding(ComboBox.TextProperty,new Binding(nameof(AppPreferences.FixedAdjustmentDb)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,Converter=new AdjustmentDbConverter(),ValidatesOnExceptions=true});fixedControls.Children.Add(fixedChoice);
        var results=new StackPanel {Visibility=Visibility.Collapsed,Margin=new(2,4,0,0)};
        var measured=new TextBlock {Text="Measured volume:"};var status=new TextBlock {Text="Status:"};var count=new TextBlock {Text="Analyzed files: 0"};
        AutomationProperties.SetAutomationId(measured,"MeasuredFileVolume");AutomationProperties.SetAutomationId(status,"FileMeasurementStatus");AutomationProperties.SetAutomationId(count,"AnalyzedFileCount");
        results.Children.Add(measured);results.Children.Add(status);results.Children.Add(count);
        fixedControls.Children.Add(AudioIconButton(MeasurementIcon(),"Measure volume adjustment","MeasureFixedAdjustment",async(sender,_)=>await MeasureFixedAdjustmentInline((Button)sender,fixedChoice,results,measured,status,count)));fixedPanel.Children.Add(results);
        void UpdateNormalization(){draft.Normalize=dynamic.IsChecked==true;fixedPanel.Visibility=draft.Normalize?Visibility.Collapsed:Visibility.Visible;Dirty();}
        dynamic.Checked+=(_,_)=>UpdateNormalization();fixedVolume.Checked+=(_,_)=>UpdateNormalization();UpdateNormalization();
        group=Group(page,"Block voice");Check(group,"Block voice while playing sounds on microphone",nameof(AppPreferences.BlockVoice));
        group=Group(page,"Play voice activation sound");Explain(group,"Triggers other application's voice activation by playing the below selected sound to others before the real sound is being played.");
        Check(group,"Enable",nameof(AppPreferences.VoiceActivationEnabled));
        var activationRow=new Grid();activationRow.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});activationRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});activationRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});group.Children.Add(activationRow);
        var activation=new TextBox {MinWidth=70};AutomationProperties.SetAutomationId(activation,"VoiceActivationPath");activation.SetBinding(TextBox.TextProperty,Bind(nameof(AppPreferences.VoiceActivationPath)));activationRow.Children.Add(activation);
        var browse=Button("Browse",(_,_)=>{var dialog=new OpenFileDialog {Filter="Audio files|*.wav;*.mp3;*.flac;*.ogg;*.m4a|All files|*.*"};if(dialog.ShowDialog(this)==true)activation.Text=dialog.FileName;});
        AutomationProperties.SetAutomationId(browse,"BrowseVoiceActivation");Grid.SetColumn(browse,1);activationRow.Children.Add(browse);
        var listen=AudioIconButton(new System.Windows.Shapes.Path {Data=(Geometry)main.FindResource("HeadphonesIcon"),Fill=new SolidColorBrush(Color.FromRgb(0,103,171)),Width=17,Height=17,Stretch=Stretch.Uniform},"Preview voice activation sound","PreviewVoiceActivation",async(_,_)=>{try{await PreviewSoundAsync(activation.Text);}catch(Exception ex){Dialogs.Error(this,ex);}});
        Grid.SetColumn(listen,2);activationRow.Children.Add(listen);
        group=Group(page,"Push-to-Talk delay");Explain(group,"Delays the playback of sounds so you have time to press your voice application's Push-to-Talk hotkey.");
        DelayField(group,"Delay in ms:",nameof(AppPreferences.PlaybackDelayMs),70,0);
        group=Group(page,"Karaoke");Explain(group,"Let sounds begin later on the microphone when playing sounds on speakers and microphone.");
        DelayField(group,"Delay sound on microphone in ms:",nameof(AppPreferences.KaraokeDelayMs),190,-10000);
        group=Group(page,"Reduce volume of other applications");
        var reductionDescription=new WrapPanel {Margin=new(0,0,0,12)};group.Children.Add(reductionDescription);
        reductionDescription.Children.Add(new TextBlock {Text="Reduce the volume of other applications while playing a sound.",TextWrapping=TextWrapping.Wrap,MaxWidth=420,VerticalAlignment=VerticalAlignment.Center});
        reductionDescription.Children.Add(HelpButton("VolumeReductionHelp","Reduce volume of other applications",VolumeReductionHelp));
        var reduce=Check(group,"Enable",nameof(AppPreferences.ReduceOtherApplications));
        var options=new StackPanel {Margin=new(20,0,0,0)};group.Children.Add(options);bool changing=false;
        foreach(var name in new[]{"Mute all other sounds","Reduce the volume of other sounds by 80%","Reduce the volume of other sounds by 50%"}){
            var radio=new RadioButton {Content=name,GroupName="OtherVolume",Tag=name};
            radio.Checked+=(_,_)=>{if(!changing&&reduce.IsChecked==true){draft.OtherApplicationsVolume=name;Dirty();}};options.Children.Add(radio);
        }
        void UpdateReduction(){changing=true;foreach(RadioButton radio in options.Children){radio.IsEnabled=reduce.IsChecked==true;radio.IsChecked=radio.IsEnabled&&draft.OtherApplicationsVolume==(string)radio.Tag;}changing=false;}
        reduce.Checked+=(_,_)=>UpdateReduction();reduce.Unchecked+=(_,_)=>UpdateReduction();UpdateReduction();
    }
    private static void DelayField(Panel parent,string label,string property,double labelWidth,int minimum)
    {
        var row=Row(parent,label,labelWidth);var spinner=new IntegerSpinner(property,label,minimum,10000);spinner.SetBinding(IntegerSpinner.ValueProperty,Bind(property));Grid.SetColumn(spinner,1);row.Children.Add(spinner);
    }
}

internal sealed class AdjustmentDbConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)=>((double)value).ToString("0.0",culture)+" dB";
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)
    {
        string text=(value?.ToString()??"").Replace("dB","",StringComparison.OrdinalIgnoreCase).Trim();
        if(!double.TryParse(text,NumberStyles.Float,culture,out double db)||!double.IsFinite(db)||db is <-30 or >30)throw new FormatException("Enter an adjustment from -30 to +30 dB.");return db;
    }
}
