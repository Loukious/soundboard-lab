using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundboardLab;
using SoundboardLab.Audio;
using SoundboardLab.Models;
using SoundboardLab.Services;

internal static class Program
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private)!.GetValue(owner)!;
    private static object? Invoke(object owner,string name,params object?[] args)=>owner.GetType().GetMethod(name,Private)!.Invoke(owner,args);
    [STAThread]private static int Main(string[] args)
    {
        var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        app.Startup-=typeof(App).GetMethod("App_Startup",Private)!.CreateDelegate<StartupEventHandler>(app);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());int result=0;
        Dispatcher.CurrentDispatcher.BeginInvoke(async()=>{try{await Capture(args.FirstOrDefault()??"docs/screenshots");}catch(Exception ex){Console.Error.WriteLine(ex);result=1;}finally{Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);}});
        Dispatcher.Run();return result;
    }
    private static async Task Save(Window window,string path,double width,double height)
    {
        var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var background=new DrawingVisual();using(var paint=background.RenderOpen()){paint.DrawRectangle(window.Background,null,new Rect(0,0,width,height));}
        var client=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);client.Render(background);client.Render(content);
        var frame=NativeWindowFrame.Capture(window,width,height);
        var drawing=new DrawingVisual();using(var paint=drawing.RenderOpen()){
            paint.DrawImage(frame.Image,new Rect(0,0,frame.Image.Width,frame.Image.Height));
            // Keep the content pixels aligned at 96 DPI instead of resampling
            // them for fractional system-frame measurements at desktop DPI.
            paint.DrawImage(client,new Rect(Math.Round(frame.Client.X),Math.Round(frame.Client.Y),width,height));
        }
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(frame.Image.Width),(int)Math.Ceiling(frame.Image.Height),96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);png.Save(output);Console.WriteLine("Captured "+path);
        if(window.IsVisible||window.IsActive)throw new Exception("Screenshot rendering unexpectedly showed or activated a window.");
    }
    private static async Task Capture(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        string state=Path.Combine(Path.GetTempPath(),"SoundboardLab-screenshots-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(state);
        var settings=new AppPreferences {SpeakerVolume=.75,DefaultPlaybackMode="Speakers",RememberPlaybackState=true,HotkeysEnabled=false,AutoKeysEnabled=false,ReduceOtherApplications=false,CheckDevicesOnStartup=false,ShowMicrophoneLevel=false,ShowTrayIcon=false,NotifyFirstSound=false,Theme="Dark",Language="English (en_US)",RecordingFolder=@"C:\Soundboard Lab\Recordings",VoiceActivationPath=@"C:\Soundboard Lab\Sounds\activation.wav",RemoteEnabled=false};
        File.WriteAllText(Path.Combine(state,"preferences.json"),JsonSerializer.Serialize(settings));MainWindow? main=null;
        try{
            main=new MainWindow(["--data-dir",state,"--pipe-name","SoundboardLab.Screenshots."+Guid.NewGuid().ToString("N")]){ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000};
            Application.Current.MainWindow=main;new WindowInteropHelper(main).EnsureHandle();Invoke(main,"Window_Loaded",main,new RoutedEventArgs());Field<DispatcherTimer>(main,"statusTimer").Stop();Field<DispatcherTimer>(main,"saveTimer").Stop();
            var library=Field<LibraryStore>(main,"library");
            string[] titles=["Welcome back","Mission complete","Retro jump","Small celebration","Drum roll","Soft notification","Hello there","Applause cue"];
            string[] categories=["Voice","Games","Games","Reactions","Music","Reactions","Voice","Reactions"];
            string[] colors=["#007DB8","#72A84F","#D7781A","#9966BB"];
            var wave=new float[288000];for(int frame=0;frame<wave.Length/2;frame++){double t=frame/48000d,envelope=Math.Pow(Math.Sin(Math.PI*t/3),2)*(.5+.5*Math.Sin(t*18));wave[frame*2]=wave[frame*2+1]=.35f*(float)(Math.Sin(t*2*Math.PI*440)*envelope);}
            for(int i=0;i<titles.Length;i++){
                string file=Path.Combine(state,titles[i]+".wav");AudioFiles.WriteWav(file,wave);var sound=library.AddFiles([file],categories[i]).Single();sound.DurationSeconds=3;sound.PlayCount=(i+1)*3;sound.Tags=categories[i].ToLowerInvariant();sound.AddedOn=new DateTime(2026,10,16,12,0,0,DateTimeKind.Utc);sound.Hotkey="Ctrl+"+(i+1);
                if(i<6)library.Document.HotbarPins.Add(new(){SoundId=sound.Id,Page=0,Slot=i,Color=colors[i%4]});
            }
            foreach(var category in library.Document.Categories){category.IconColor=colors[Array.IndexOf(new[]{"Voice","Games","Reactions","Music"},category.Name)%4];}
            library.Save(Path.Combine(state,"Demo library.json"));Invoke(main,"RefreshLibrary");await Dispatcher.Yield(DispatcherPriority.Background);Field<DataGrid>(main,"SoundGrid").SelectedIndex=0;
            await Save(main,Path.Combine(output,"main-window.png"),1020,660);
            Field<FrameworkElement>(main,"SearchPanel").Visibility=Visibility.Visible;
            await Save(main,Path.Combine(output,"search-panel.png"),1020,660);
            Field<Slider>(main,"SpeakerSlider").Value=0;
            await Save(main,Path.Combine(output,"muted-speakers.png"),1020,660);
            foreach(var (level,name) in new[]{(.2,"low"),(.5,"mid"),(.8,"high")}){
                Field<Slider>(main,"SpeakerSlider").Value=level;
                await Save(main,Path.Combine(output,"volume-"+name+".png"),1020,660);
            }
            Field<Slider>(main,"SpeakerSlider").Value=.75;Field<FrameworkElement>(main,"SearchPanel").Visibility=Visibility.Collapsed;
            var prefs=Field<PreferenceStore>(main,"preferenceStore");prefs.Settings.ShowHotbar=true;Field<FrameworkElement>(main,"HotbarPanel").Visibility=Visibility.Visible;Invoke(main,"RefreshHotbar");
            await Save(main,Path.Combine(output,"hotbar.png"),1020,660);
            var type=typeof(MainWindow).Assembly.GetType("SoundboardLab.SettingsWindow")!;
            var preferences=(Window)Activator.CreateInstance(type,[main,Field<AudioService>(main,"audio"),prefs,new Func<Task>(()=>Task.CompletedTask),null])!;
            try{
                foreach(string page in new[]{"Interface","Hotkeys","Recorder"}){Invoke(preferences,"SelectPage",page);await Save(preferences,Path.Combine(output,"preferences-"+page.ToLowerInvariant()+".png"),560,685);}
                Invoke(preferences,"SelectPage","Audio");await Save(preferences,Path.Combine(output,"preferences-audio.png"),560,685);
                var tabs=Field<TabControl>(preferences,"tabs");var scroll=(ScrollViewer)((TabItem)tabs.SelectedItem).Content;scroll.ScrollToEnd();
                await Save(preferences,Path.Combine(output,"audio-forwarding.png"),560,685);
            }finally{preferences.Close();}
            using(var hooks=new GlobalHotkeyService()){
                var captureType=typeof(MainWindow).Assembly.GetType("SoundboardLab.HotkeyCaptureWindow")!;
                foreach(string initial in new[]{"","Ctrl+F2"}){
                    var capture=(Window)Activator.CreateInstance(captureType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object?[]{initial,hooks,false,new Func<string,HotkeyConflict>(_=>default),null},null)!;
                    try{await Save(capture,Path.Combine(output,initial.Length==0?"set-hotkey.png":"edit-hotkey.png"),capture.Width-16,145);}finally{capture.Close();}
                }
            }
            var editorType=typeof(MainWindow).Assembly.GetType("SoundboardLab.EditorWindow")!;
            var editor=(Window)Activator.CreateInstance(editorType,[library.Document.Entries[0].FilePath,new Func<string,Task>(_=>Task.CompletedTask),prefs.Settings])!;
            try{await (Task)Invoke(editor,"LoadAsync")!;editor.Title=@"Sound Editor - C:\Soundboard Lab\Sounds\Welcome back.wav";await Save(editor,Path.Combine(output,"sound-editor.png"),680,410);}finally{editor.Close();}
            if(main.IsVisible||main.IsActive)throw new Exception("Screenshot rendering unexpectedly showed a window.");
        }finally{if(main is not null){main.Close();await Dispatcher.Yield(DispatcherPriority.Background);}Directory.Delete(state,true);}
    }
}
