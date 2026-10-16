using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using SoundboardLab.Audio;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed partial class EditorWindow : Window
{
    private string input;
    private readonly Func<string,Task> add;
    private readonly AppPreferences settings;
    private readonly AudioService preview=new();
    private readonly Grid root=new();
    private readonly EditorWaveform waveform=new();
    private readonly ScrollViewer scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Visible,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly Menu menu=new();
    private readonly TextBlock details=new(){Margin=new(8,5,0,0),Foreground=new SolidColorBrush(Color.FromRgb(175,190,200)),FontSize=11};
    private readonly Grid fileDetails=new(){Margin=new(8,5,0,0)};
    private readonly List<TextBlock> detailValues=[];
    private readonly Dictionary<string,MenuItem> commands=[];
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(16)};
    private readonly PlaybackPositionSmoother smoother=new();
    private readonly string previewPath=Path.Combine(Path.GetTempPath(),"SoundboardLab-editor-"+Guid.NewGuid()+".wav");
    private EditorDocument? document;
    private Button saveButton=null!,saveAsButton=null!;
    private bool overwritePrompt=true,showSelection=true,closeAfterSaving,saving,allowClose,disposed,preparingPreview;
    private int previewVersion;
    private double playStart;
    private int sourceRate=48000,sourceChannels=2;
    private int exportBitrate;

    public EditorWindow(string input,Func<string,Task> add,AppPreferences settings)
    {
        this.input=input;this.add=add;this.settings=settings;exportBitrate=int.Parse(settings.RecordingBitrate.Split(' ')[0])*1000;
        Style=(Style)Application.Current.FindResource(typeof(Window));Icon=Application.Current.MainWindow.Icon;
        Width=624;Height=402;MinWidth=520;MinHeight=350;Title="Sound Editor - "+input;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Content=root;foreach(var height in new[]{"23","26","*","58","50"})root.RowDefinitions.Add(new(){Height=height=="*"?new(1,GridUnitType.Star):new(double.Parse(height,CultureInfo.InvariantCulture))});
        root.Children.Add(menu);BuildMenus();
        var toolbar=new DockPanel {LastChildFill=false,Background=(Brush)FindResource("Palette_252525")};Grid.SetRow(toolbar,1);root.Children.Add(toolbar);
        Toolbar(toolbar,"Play from the beginning","HeadphonesIcon",async()=>await PreviewAsync(false));
        Toolbar(toolbar,"Play selected section","M0,0 H16 V16 H0 Z M4,3 V13 L13,8 Z",async()=>await PreviewAsync(true));
        Toolbar(toolbar,"Stop playback","StopIcon",()=>{StopPreview();return Task.CompletedTask;});ToolbarSeparator(toolbar);
        Toolbar(toolbar,"Cut out selected range","M1,1 L15,15 M15,1 L1,15 M4,12 A3,3 0 1 1 1,9 M12,12 A3,3 0 1 0 15,9",()=>Edit("cut"),true);
        Toolbar(toolbar,"Crop selected range","M4,0 V12 H16 M0,4 H12 V16",()=>Edit("crop"),true);
        Toolbar(toolbar,"Undo","M7,0 L0,6 7,12 V8 C14,7 16,12 12,16 C22,7 15,3 7,4 Z",()=>Edit("undo"));
        Toolbar(toolbar,"Redo","M9,0 L16,6 9,12 V8 C2,7 0,12 4,16 C-6,7 1,3 9,4 Z",()=>Edit("redo"));ToolbarSeparator(toolbar);
        Toolbar(toolbar,"Zoom in","M6,0 H10 V6 H16 V10 H10 V16 H6 V10 H0 V6 H6 Z",()=>Zoom(1.5));
        Toolbar(toolbar,"Zoom out","M0,6 H16 V10 H0 Z",()=>Zoom(1/1.5));
        scroll.Resources.MergedDictionaries.Add(new(){Source=new Uri("/SoundboardLab;component/EditorResources.xaml",UriKind.Relative)});scroll.Content=waveform;Grid.SetRow(scroll,2);root.Children.Add(scroll);AutomationProperties.SetAutomationId(waveform,"EditorWaveform");AutomationProperties.SetName(waveform,"Audio waveform selection");
        waveform.SelectionChanged=()=>{UpdateSelection();UpdateCommands();};
        scroll.SizeChanged+=(_,_)=>LayoutWaveform();scroll.ScrollChanged+=(_,_)=>{waveform.Offset=scroll.HorizontalOffset;waveform.Viewport=scroll.ViewportWidth;waveform.InvalidateVisual();};
        var information=new StackPanel {Background=(Brush)FindResource("Palette_292929")};information.Children.Add(details);BuildFileDetails();information.Children.Add(fileDetails);Grid.SetRow(information,3);root.Children.Add(information);
        AutomationProperties.SetAutomationId(fileDetails,"EditorFileDetails");
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,7,8,0)};Grid.SetRow(footer,4);root.Children.Add(footer);
        saveAsButton=FooterButton(footer,"Save as...",async()=>await SaveAsync(true));saveButton=FooterButton(footer,"Save",async()=>await SaveAsync(false));FooterButton(footer,"Cancel",()=>{Close();return Task.CompletedTask;});
        Loaded+=async(_,_)=>await LoadAsync();PreviewKeyDown+=HandleKey;Closing+=ClosingEditor;
        timer.Tick+=(_,_)=>{if(document is null)return;double position=smoother.Update(preview.Position.TotalSeconds,preview.Duration.TotalSeconds,preview.IsPlaying,preview.TimelineVersion,Environment.TickCount64/1000d);double next=preview.IsPlaying?playStart+position:document.SelectionStart;if(waveform.Playhead!=next){waveform.Playhead=next;waveform.InvalidateVisual();}UpdateCommands();};timer.Start();
        Closed+=(_,_)=>{disposed=true;previewVersion++;timer.Stop();preview.Dispose();if(!preparingPreview&&File.Exists(previewPath))File.Delete(previewPath);};UpdateCommands();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    protected override void OnSourceInitialized(EventArgs e){base.OnSourceInitialized(e);int dark=1;DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,20,ref dark,4);}
    private async Task LoadAsync()
    {
        try{details.Text="Loading audio...";var samples=await AudioFiles.DecodeAsync(input);if(disposed)return;document=new(samples);waveform.Document=document;
            try{using var reader=new AudioFileReader(input);sourceRate=reader.WaveFormat.SampleRate;sourceChannels=reader.WaveFormat.Channels;}catch{ /* The canonical decoder also supports Vorbis and Opus. */ }
            preview.SelectDevices(null,string.IsNullOrWhiteSpace(settings.RenderDeviceId)?null:settings.RenderDeviceId);preview.SpeakerVolume=(float)settings.SpeakerVolume;RefreshDocument();
        }catch(Exception ex){details.Text=ex.Message;Dialogs.Error(this,ex);}
    }
    private MenuItem Command(string text,Func<Task> action,string gesture="",string? key=null)
    {
        var item=new MenuItem {Header=text,InputGestureText=gesture};item.Click+=async(_,_)=>await Guard(action);if(key is not null)commands[key]=item;return item;
    }
    private MenuItem Toggle(string text,bool initial,Action<bool> changed){var item=new MenuItem {Header=text,IsCheckable=true,IsChecked=initial};item.Click+=(_,_)=>changed(item.IsChecked);return item;}
    private void BuildMenus()
    {
        var file=new MenuItem {Header="_File"};menu.Items.Add(file);
        file.Items.Add(Command("Save",()=>SaveAsync(false),"Ctrl+S","save"));file.Items.Add(Command("Save as...",()=>SaveAsync(true),"Ctrl+Alt+S","save-as"));file.Items.Add(Command("Save and close",async()=>{if(await SaveAsync(false,true)){allowClose=true;Close();}},"Ctrl+Shift+S","save-close"));file.Items.Add(new Separator());
        file.Items.Add(Toggle("Show overwrite confirmation prompt",true,x=>overwritePrompt=x));file.Items.Add(Toggle("Show selected range",true,x=>{showSelection=x;UpdateSelection();}));file.Items.Add(Toggle("Close window after saving",false,x=>closeAfterSaving=x));file.Items.Add(new Separator());file.Items.Add(Command("Close window",()=>{Close();return Task.CompletedTask;},"Escape"));
        var edit=new MenuItem {Header="_Edit"};menu.Items.Add(edit);
        edit.Items.Add(Command("Undo",()=>Edit("undo"),"Ctrl+Z","undo"));edit.Items.Add(Command("Redo",()=>Edit("redo"),"Ctrl+Y","redo"));edit.Items.Add(new Separator());edit.Items.Add(Command("Cut out selected range",()=>Edit("cut"),"Del","cut"));edit.Items.Add(Command("Crop selected range",()=>Edit("crop"),"Ctrl+T","crop"));edit.Items.Add(new Separator());edit.Items.Add(Command("Select everything",()=>Edit("select-all"),"Ctrl+A","all"));edit.Items.Add(Command("Deselect",()=>Edit("deselect"),"Ctrl+D","deselect"));
        var view=new MenuItem {Header="_View"};menu.Items.Add(view);view.Items.Add(Command("Zoom in",()=>Zoom(1.5),"Num+"));view.Items.Add(Command("Zoom out",()=>Zoom(1/1.5),"Num-"));view.Items.Add(new Separator());view.Items.Add(Command("Default zoom",()=>{waveform.Zoom=400;LayoutWaveform();return Task.CompletedTask;},"Num/"));
        var play=new MenuItem {Header="_Play"};menu.Items.Add(play);play.Items.Add(Command("Play from the beginning",()=>PreviewAsync(false),"Shift+Enter","play"));play.Items.Add(Command("Play selected section",()=>PreviewAsync(true),"Enter","play-selection"));play.Items.Add(new Separator());play.Items.Add(Command("Stop playback",()=>{StopPreview();return Task.CompletedTask;},key:"stop"));
        var effects=new MenuItem {Header="Effe_cts"};menu.Items.Add(effects);foreach(var (label,key) in new[]{("Fade-in","fade-in"),("Fade-out","fade-out"),("Reverse","reverse")})effects.Items.Add(Command(label,()=>Edit(key),key:key));
    }
    private void Toolbar(Panel panel,string title,string geometry,Func<Task> action,bool stroke=false)
    {
        var shape=geometry.EndsWith("Icon",StringComparison.Ordinal)?(Geometry)Application.Current.MainWindow.FindResource(geometry):Geometry.Parse(geometry);
        var path=new System.Windows.Shapes.Path {Data=shape,Width=16,Height=16,Stretch=Stretch.Uniform,Fill=stroke?Brushes.Transparent:(Brush)FindResource("Accent"),Stroke=stroke?(Brush)FindResource("Accent"):null,StrokeThickness=stroke?1.5:0};
        var button=new Button {Style=(Style)Application.Current.MainWindow.FindResource("TransportButton"),Content=path,ToolTip=title};AutomationProperties.SetName(button,title);button.Click+=async(_,_)=>await Guard(action);panel.Children.Add(button);
    }
    private static void ToolbarSeparator(Panel panel)=>panel.Children.Add(new Border {Width=1,Height=20,Background=Brushes.Gray,Margin=new(5,2,5,2)});
    private Button FooterButton(Panel panel,string title,Func<Task> action){var button=new Button {Content=title,Width=88,Height=24,Padding=new(4,1,4,1),Margin=new(4,2,4,2),VerticalAlignment=VerticalAlignment.Top};button.Click+=async(_,_)=>await Guard(action);panel.Children.Add(button);return button;}
    private async Task Guard(Func<Task> action){try{await action();}catch(Exception ex){Dialogs.Error(this,ex);}}
    private void LayoutWaveform(){waveform.Width=Math.Max(Math.Max(1,scroll.ViewportWidth),(document?.Duration??0)*waveform.Zoom);waveform.Height=Math.Max(100,scroll.ActualHeight-SystemParameters.HorizontalScrollBarHeight);waveform.Viewport=scroll.ViewportWidth;waveform.InvalidateVisual();}
    private Task Zoom(double factor){waveform.Zoom=Math.Clamp(waveform.Zoom*factor,5,6400);LayoutWaveform();return Task.CompletedTask;}
    private void BuildFileDetails()
    {
        foreach(double width in new[]{72d,68,58})fileDetails.ColumnDefinitions.Add(new(){Width=new(width)});fileDetails.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        foreach(int row in Enumerable.Range(0,3))fileDetails.RowDefinitions.Add(new(){Height=new(15)});
        foreach(var (label,row,column) in new[]{("Size:",0,0),("Duration:",0,2),("Format:",1,0),("Channels:",1,2),("Sample rate:",2,0),("Volume:",2,2)}){
            var caption=new TextBlock {Text=label,FontSize=11,Foreground=details.Foreground};Grid.SetRow(caption,row);Grid.SetColumn(caption,column);fileDetails.Children.Add(caption);
            var value=new TextBlock {FontSize=11,Foreground=details.Foreground};Grid.SetRow(value,row);Grid.SetColumn(value,column+1);fileDetails.Children.Add(value);detailValues.Add(value);
        }
    }
    private void RefreshDocument()
    {
        if(document is null)return;Title="Sound Editor - "+input+(document.Modified?" *":"");LayoutWaveform();
        double volume=LoudnessMeter.Measure(document.Samples)+112;long size=File.Exists(input)?new FileInfo(input).Length:document.Samples.Length*2;
        details.Visibility=Visibility.Collapsed;
        string[] values=[$"{Math.Ceiling(size/1024d):0} KB",$"{(int)document.Duration/60}:{(int)document.Duration%60:00} +{(int)(document.Duration%1*1000):000} ms",Path.GetExtension(input).TrimStart('.').ToUpperInvariant(),sourceChannels==1?"mono":"stereo",$"{sourceRate} Hz",double.IsFinite(volume)?volume.ToString("0.0")+" dB":"Silence"];
        for(int index=0;index<values.Length;index++)detailValues[index].Text=values[index];
        UpdateSelection();UpdateCommands();
    }
    private void UpdateSelection(){waveform.ShowSelection=showSelection;waveform.InvalidateVisual();}
    private void UpdateCommands()
    {
        foreach(var (key,item) in commands)item.IsEnabled=document is not null&&!saving&&(key switch {"undo"=>document.CanUndo,"redo"=>document.CanRedo,"cut" or "crop"=>document.HasSelection,"save" or "save-close"=>document.Modified,"stop"=>preview.IsPlaying,_=>true});
        if(saveButton is not null)saveButton.IsEnabled=document?.Modified==true&&!saving;if(saveAsButton is not null)saveAsButton.IsEnabled=document is not null&&!saving;
    }
    private Task Edit(string command)
    {
        if(document is null||saving)return Task.CompletedTask;StopPreview();
        switch(command){case "undo":document.Undo();break;case "redo":document.Redo();break;case "cut":document.Cut();break;case "crop":document.Crop();break;case "select-all":document.SelectAll();break;case "deselect":document.Deselect();break;default:document.Effect(command);break;}
        waveform.Playhead=document.SelectionStart;RefreshDocument();return Task.CompletedTask;
    }
    internal object State()=>new {loaded=document is not null,duration=document?.Duration,selectionStart=document?.SelectionStart,selectionEnd=document?.SelectionEnd,modified=document?.Modified,canUndo=document?.CanUndo,canRedo=document?.CanRedo,playing=preview.IsPlaying,zoom=waveform.Zoom,saving};
}
