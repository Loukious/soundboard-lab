using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SoundboardLab.Services;
using SoundboardLab.Audio;
using SoundboardLab.Models;

namespace SoundboardLab;
public partial class MainWindow
{
    private TrayIcon? tray;
    private bool exitRequested;
    private int statusTicks;
    private bool? appliedStartup;
    private string? appliedDucking;
    private PlaybackDestination lastDestination=PlaybackDestination.Both;
    private static readonly Dictionary<string,string> PaletteDefaults=new();
    private string[] RenderedMenuLabels()=>MainMenu.Items.OfType<MenuItem>().Select(item=>string.Concat(Descendants(item).OfType<TextBlock>().Where(x=>x.IsVisible).Select(x=>new System.Windows.Documents.TextRange(x.ContentStart,x.ContentEnd).Text))).ToArray();
    private void ApplyInterfaceSettings()
    {
        Localization.Load(Settings.Language);Localization.Apply(this);
        string theme=Settings.Theme;
        if(theme=="System default"){using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");theme=key?.GetValue("AppsUseLightTheme") is int value&&value==0?"Dark":"Light";}
        foreach(string key in Application.Current.Resources.Keys.OfType<string>().Where(x=>x.StartsWith("Palette_")).ToArray()){
            if(!PaletteDefaults.TryGetValue(key,out string? original))PaletteDefaults[key]=original="#"+key[8..];
            string mapped=original;
            if(theme=="Light")mapped=original.ToUpperInvariant() switch {"#252525"=>"#FFFFFF","#202020"=>"#F0F0F0","#333333"=>"#ECECEC","#292929"=>"#F0F0F0","#353535"=>"#F8F8F8","#2C2C2C"=>"#F5F5F5","#2B2B2B"=>"#F0F0F0","#414141" or "#444444" or "#454545" or "#474747" or "#484848" or "#4D4D4D"=>"#CCE8FF","#555555"=>"#B8B8B8","#777777"=>"#777777",_=>original};
            else if(theme=="Navy")mapped=original.ToUpperInvariant() switch {"#252525"=>"#17202E","#202020"=>"#111A27","#333333"=>"#25344A","#292929"=>"#213047","#353535"=>"#30425A","#414141" or "#444444" or "#454545" or "#474747" or "#484848"=>"#405C7E",_=>original};
            Application.Current.Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(mapped));
        }
        var colors=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){["Blue"]="#007DB8",["Red"]="#C83939",["Green"]="#388B45",["Orange"]="#D7781A",["Purple"]="#9966BB",["Yellow"]="#D0AD00",["White"]="#DDDDDD",["Black"]="#111111",["Teal"]="#008D8D"};
        Application.Current.Resources["Accent"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.GetValueOrDefault(Settings.ToolbarColor,Settings.ToolbarColor)));
        Application.Current.Resources["Text"]=new SolidColorBrush(theme=="Light"?Color.FromRgb(25,25,25):Color.FromRgb(230,230,230));
        Application.Current.Resources["Muted"]=new SolidColorBrush(theme=="Light"?Color.FromRgb(95,95,95):Color.FromRgb(155,155,155));
        Application.Current.Resources["Panel"]=Application.Current.Resources["Palette_333333"];
        var panel=(Grid)Content;double factor=Settings.Zoom switch {"150%"=>1.5,"200%"=>2,_=>1};panel.LayoutTransform=new ScaleTransform(factor,factor);MinWidth=760*factor;MinHeight=450*factor;
        double size=Settings.ToolbarSize switch {"Medium"=>1.3,"Large"=>1.65,_=>1};panel.RowDefinitions[1].Height=new GridLength(33*size);
        foreach(var button in Descendants(TransportPanel).OfType<Button>()){button.Width=29*size;button.Height=29*size;foreach(var shape in Descendants(button).OfType<System.Windows.Shapes.Path>()){shape.Width=15*size;shape.Height=15*size;}}
        Topmost=Settings.AlwaysOnTop;SoundGrid.CanUserSortColumns=!Settings.DisableSorting;
        if(Settings.DisableSorting)ClearSoundSorting();
        SoundGrid.AlternatingRowBackground=Settings.AlternatingRowColors?new SolidColorBrush(theme=="Light"?Color.FromRgb(240,246,252):Color.FromRgb(42,47,55)):(Brush)Application.Current.Resources["Palette_252525"];
        TransportPanel.ColumnDefinitions[4].Width=new GridLength(Settings.ShowMicrophoneLevel?110:0);MicrophonePanel.Visibility=Settings.ShowMicrophoneLevel?Visibility.Visible:Visibility.Collapsed;
        MicrophoneMeter.Visibility=Settings.ShowMicrophoneLevel?Visibility.Visible:Visibility.Collapsed;
        tray??=new(this,RestoreFromTray,()=>{exitRequested=true;Close();});tray.SetVisible(Settings.ShowTrayIcon);
        if(Settings.ShowMicrophoneLevel)_=GuardAsync(EnsureMicrophoneActiveAsync);
        ApplyDateFormat();RefreshCategories();RefreshHotbar();
    }
    internal async Task<double> MeasureLibraryLoudnessForPreferences(Action<int,int> progress,CancellationToken token)
    {
        string[] files=library.Document.Entries.Select(x=>x.FilePath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var levels=new List<double>();int done=0;
        foreach(string file in files){token.ThrowIfCancellationRequested();var result=await AudioFiles.AnalyzeAsync(file,32,token);if(double.IsFinite(result.LoudnessLufs))levels.Add(result.LoudnessLufs);progress(++done,files.Length);}
        if(levels.Count==0)throw new InvalidOperationException("Add sound files containing audible audio before analyzing.");
        return 10*Math.Log10(levels.Average(x=>Math.Pow(10,x/10)));
    }
    private async Task AddGeneratedSoundAsync(string path)
    {
        var before=library.Document.Entries.Select(x=>x.Id).ToHashSet();await AddPathsAsync([path]);
        foreach(var sound in library.Document.Entries.Where(x=>!before.Contains(x.Id)).ToArray()){
            sound.Category=Settings.RecordingCategory switch {"Selected category"=>selectedCategory=="*"?"":selectedCategory,"My Sounds"=>"",_=>Settings.RecordingCategory};
            if(sound.Category.Length>0)library.AddCategory(sound.Category);if(Settings.RecordingInsertPosition=="Beginning")library.Document.Entries.Move(library.Document.Entries.IndexOf(sound),0);
        }
    }
    private async Task StartRecordingAsync(RecordingSource source,int? duration=null)
    {
        if(audio.IsRecording)throw new InvalidOperationException("A recording is already in progress.");
        await PlayNotificationAsync(source==RecordingSource.Microphone?"record-microphone":"record-speakers",true);
        string folder=string.IsNullOrWhiteSpace(Settings.RecordingFolder)?Path.Combine(stateDirectory,"recordings"):Settings.RecordingFolder;Directory.CreateDirectory(folder);
        audio.RecordingOptions=new(Settings.RecordingFormat,int.Parse(Settings.RecordingBitrate.Split(' ')[0])*1000,Settings.NormalizeRecordings,Settings.RecordingTargetDb,Settings.TrimRecordingSilence);
        int seconds=duration??Settings.RecordingLimitSeconds;audio.StartRecording(source,Path.Combine(folder,"Recording-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".wav"),seconds==0?null:TimeSpan.FromSeconds(seconds));
    }
    private async Task PlayTransientAsync(string path,PlaybackDestination target,bool fromHotkey=false)
    {
        await StopAsync();if(target!=PlaybackDestination.Speakers)await EnsureMicrophoneActiveAsync();
        audio.RoutingOptions=new(Settings.KaraokeDelayMs,Settings.VoiceActivationEnabled?Settings.VoiceActivationPath:"",Settings.Normalize?Settings.TargetLufs:null,Settings.FixedAdjustmentDb);
        await autoKeys.BeginPlaybackAsync(lifetime.Token,fromHotkey,target!=PlaybackDestination.Speakers);await audio.PlayAsync(path,target,lifetime.Token);playingEntry=null;hadPlayback=true;userStopped=false;UpdateDucking();
    }
    internal async Task ConfigureCaptureDevicesAsync(IEnumerable<string> selection,bool repair=false)
    {
        if(audio.IsRecording)throw new InvalidOperationException("Stop recording before changing recording-device configuration.");
        await StopForwardingAsync();
        await StopAsync();StopMicMonitor();
        string[] ids=selection.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(ids.Length==0)throw new ArgumentException("Check at least one recording device.");
        await DeviceSetupClient.RunAsync(new("configure",ids,repair));StopMicMonitor();
    }
    internal async Task RestartWindowsAudioAsync(){await StopForwardingAsync();StopMicMonitor();await DeviceSetupClient.RunAsync(new("restart",[]));RefreshStatus();}
    internal async Task ChangeCaptureSetupModeAsync(string endpoint,string slot)
    {
        if(slot is not ("PostMix" or "Stream" or "Mode" or "Endpoint"))throw new ArgumentException("Invalid setup mode.");
        if(audio.IsRecording)throw new InvalidOperationException("Stop recording before changing recording-device configuration.");
        await StopForwardingAsync();await StopAsync();StopMicMonitor();
        await DeviceSetupClient.RunAsync(new("install",[endpoint],true,slot));
    }
    private void ApplyStartupSetting()
    {
        if(Settings.ReduceOtherApplications&&appliedDucking!=Settings.OtherApplicationsVolume){ApplicationVolumeDucker.ConfigurePreference(Settings.OtherApplicationsVolume);appliedDucking=Settings.OtherApplicationsVolume;}
        if(appliedStartup==Settings.RunOnStartup)return;
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(Settings.RunOnStartup)key.SetValue("SoundboardLab","\""+Environment.ProcessPath+"\"",RegistryValueKind.String);
        else key.DeleteValue("SoundboardLab",false);
        appliedStartup=Settings.RunOnStartup;
    }
    private void RestoreFromTray(){Show();WindowState=WindowState.Normal;Activate();}
    private void MinimizeWindow(){WindowState=WindowState.Minimized;if(Settings.ShowTrayIcon&&Settings.MinimizeToTray)Hide();}
    private void ApplyDateFormat()
    {
        string? format=Settings.DateFormat switch {"ISO 8601 - 2016-10-30"=>"yyyy-MM-dd","English (en_US) - 10/30/2016"=>"MM/dd/yyyy","English (en_GB) - 30/10/2016"=>"dd/MM/yyyy","Deutsch (de_DE) - 30.10.2016"=>"dd.MM.yyyy",_=>null};
        foreach(var column in SoundGrid.Columns.OfType<DataGridTextColumn>().Where(x=>x.Binding is System.Windows.Data.Binding binding&&binding.Path?.Path is "LastPlayed" or "AddedOn"))if(column.Binding is System.Windows.Data.Binding old)column.Binding=new System.Windows.Data.Binding(old.Path.Path){StringFormat=format??CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern};
    }
    private void SwitchPlayMode(bool hotkey)
    {
        int[] modes=hotkey?Settings.ModeSwitch switch {"default and speakers"=>[0,1],"default and microphone"=>[0,2],_=>[0,1,2]}:[0,1,2];
        int index=Array.IndexOf(modes,DestinationBox.SelectedIndex);DestinationBox.SelectedIndex=modes[(index+1)%modes.Length];
        if(hotkey)Notify("mode-"+Destination.ToString().ToLowerInvariant());
    }
    private void MainWindow_StateChanged(object? sender,EventArgs e){if(initialized&&WindowState==WindowState.Minimized&&Settings.ShowTrayIcon&&Settings.MinimizeToTray)Hide();}
}
