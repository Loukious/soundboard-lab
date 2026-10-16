using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SoundboardLab;
using SoundboardLab.Audio;
using SoundboardLab.Models;
using SoundboardLab.Services;
using Localization=SoundboardLab.Localization;

internal static class Program
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int checks;
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private|BindingFlags.Public)!.GetValue(owner)!;
    private static object? Invoke(object owner,string name,params object?[] args)=>owner.GetType().GetMethod(name,Private)!.Invoke(owner,args);
    private static void Check(bool condition,string description){if(!condition)throw new Exception(description);checks++;Console.WriteLine("PASS "+description);}
    private static async Task Until(Func<bool> condition,string description)
    {
        for(int attempt=0;attempt<100;attempt++){if(condition())return;await Task.Delay(20);}throw new Exception(description);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
    [STAThread]
    private static int Main(string[] args)
    {
        // Load the real application resources but never call Application.Run:
        // that would invoke App_Startup and show an interactive window.
        var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        // WPF queues Startup from its constructor even without Application.Run.
        // Remove the production handler before pumping that queue.
        app.Startup-=typeof(App).GetMethod("App_Startup",Private)!.CreateDelegate<StartupEventHandler>(app);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());int exitCode=0;
        Dispatcher.CurrentDispatcher.BeginInvoke(async()=>{
            try{await Run(args.Contains("--layout-only"),args.Contains("--preference-devices"));}catch(Exception ex){Console.Error.WriteLine(ex);exitCode=1;}
            finally{Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);}
        });
        Dispatcher.Run();return exitCode;
    }
    private static async Task Run(bool layoutOnly,bool preferenceDevices)
    {
        string temp=Path.Combine(Path.GetTempPath(),"SoundboardLab-headless-controls-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        string path=Path.Combine(temp,"clip.wav");AudioFiles.WriteWav(path,new float[384000]);
        File.WriteAllText(Path.Combine(temp,"preferences.json"),"""{"SpeakerVolume":0,"ClipVolume":1,"DefaultPlaybackMode":"Speakers","HotkeysEnabled":false,"RememberHotkeyStatus":true,"AutoKeysEnabled":false,"ReduceOtherApplications":false,"CheckDevicesOnStartup":false,"ShowMicrophoneLevel":false,"ShowTrayIcon":false,"NotifyFirstSound":false,"StartMinimized":false}""");
        string pipe="SoundboardLab.Headless.Controls."+Guid.NewGuid().ToString("N");using var occupiedPipe=new RemoteControlServer(pipe);occupiedPipe.Start();
        MainWindow? window=null;
        try {
            window=new MainWindow(["--data-dir",temp,"--pipe-name",pipe]){ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000};
            // EnsureHandle creates a hidden HWND only; Show/Activate are never used.
            new WindowInteropHelper(window).EnsureHandle();
            Invoke(window,"Window_Loaded",window,new RoutedEventArgs());
            var timer=Field<DispatcherTimer>(window,"statusTimer");var saveTimer=Field<DispatcherTimer>(window,"saveTimer");
            Check(Field<bool>(window,"initialized")&&timer.IsEnabled&&saveTimer.IsEnabled,"a remote pipe conflict does not abort application startup or its timers");
            timer.Stop();saveTimer.Stop();
            var content=(FrameworkElement)window.Content;
            void Layout(){content.Measure(new Size(1020,660));content.Arrange(new Rect(0,0,1020,660));content.UpdateLayout();}
            Layout();
            Check(!window.IsVisible&&!window.IsActive,"real WPF controls initialize without showing or activating the test window");
            var library=Field<LibraryStore>(window,"library");var sound=library.AddFiles([path]).Single();sound.DurationSeconds=4;
            await Task.Delay(30);Layout();
            var grid=Field<DataGrid>(window,"SoundGrid");var volume=Field<Slider>(window,"SpeakerSlider");var engine=Field<AudioService>(window,"audio");
            await CheckMissingSoundsAndColors(window,library,sound,temp);
            await CheckSearchAndCategoryLayout(window,library,sound,temp);
            await CheckPreferenceResponsiveness(window);
            if(preferenceDevices){int callerThread=Environment.CurrentManagedThreadId,workerThread=0;var clock=System.Diagnostics.Stopwatch.StartNew();var discovered=await Task.Run(()=>{workerThread=Environment.CurrentManagedThreadId;return PreferenceDeviceSnapshot.Read(CancellationToken.None);});Check(workerThread!=callerThread&&discovered.Devices.All(x=>!string.IsNullOrWhiteSpace(x.Id)),"the actual read-only Windows device snapshot completes on its own background thread");Console.WriteLine($"Background device discovery: {clock.Elapsed.TotalMilliseconds:F1} ms for {discovered.Devices.Length} endpoints. No audio stream or volume change.");}
            if(layoutOnly){CheckHotkeyEditor(window,library,sound);CheckMenusAndSpecialHotkeys(window);await CheckPreferencePages(window,temp);Console.WriteLine($"PASS {checks} headless WPF layout checks. No capture, rendering, window activation or OS input.");return;}
            int frameworkDoubleClicks=0;var frameworkControl=new Control();frameworkControl.MouseDoubleClick+=(_,_)=>frameworkDoubleClicks++;
            for(int click=1;click<=20;click++){
                var press=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.MouseDownEvent};
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press,click);frameworkControl.RaiseEvent(press);
            }
            Check(frameworkDoubleClicks==1,"the real WPF double-click event reproduces the missing-counts bug for a 1-through-20 burst");
            void PressCell(int clickCount,DataGridCell? target=null){
                Layout();var cell=target??Descendants<DataGridCell>(grid).First(x=>ReferenceEquals(x.DataContext,sound)&&x.Column.Header as string=="Tag");
                var press=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent};
                // Set WPF's internal click counter on a routed event; never
                // submit input to Windows or run a control's focus handler.
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press,clickCount);
                cell.RaiseEvent(press);
            }
            Check(!volume.IsMoveToPointEnabled&&volume.LargeChange<volume.Maximum-volume.Minimum,"volume track clicks use the captured absolute-position handler and a bounded keyboard step");
            foreach(double target in new[]{.25,.5,.75}){
                volume.Value=.5;Layout();var track=(Track)volume.Template.FindName("PART_Track",volume);
                double x=track.Thumb.ActualWidth/2+(track.ActualWidth-track.Thumb.ActualWidth)*target;
                VolumeSliderFeedback.SetValueFromPointer(volume,track,new Point(x,track.ActualHeight/2));Layout();
                Check(Math.Abs(volume.Value-target)<.01&&Math.Abs(engine.SpeakerVolume-target)<.01,$"volume track geometry and playback gain preserve {target:P0} positions");
            }
            volume.Value=.5;Slider.IncreaseLarge.Execute(null,volume);
            Check(volume.Value>.5&&volume.Value<1,"a volume track step does not jump directly to maximum");
            volume.Value=0;
            Button FindButton(string name)=>Descendants<Button>(content).First(x=>AutomationProperties.GetName(x)==name);
            grid.SelectedItem=null;
            await (Task)Invoke(window,"HandleActionAsync","play",true)!;
            Check(engine.IsPlaying&&ReferenceEquals(grid.SelectedItem,sound),"the Start playback hotkey starts the first sound on a fresh session without a selected row");
            var positionBeforePreferences=engine.Position;
            await CheckPreferenceResponsiveness(window);
            Check(engine.IsPlaying&&engine.Position>positionBeforePreferences,"muted speaker playback and its timeline continue while Preferences opens and a device scan is held");
            await (Task)Invoke(window,"StopAsync")!;
            grid.SelectedItem=null;FindButton("Play selected file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>engine.IsPlaying,"Play button did not start the selected sound");
            Check(ReferenceEquals(grid.SelectedItem,sound)&&engine.IsPlaying,"the actual Play button starts the first sound when no row was selected");
            FindButton("Stop playback").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>!engine.IsPlaying,"Stop button did not stop playback");
            grid.SelectedItem=sound;
            PressCell(1);PressCell(2);
            await Until(()=>engine.IsPlaying,"Double-click handler did not start playback");
            Check(engine.IsPlaying,"two routed row presses start playback without sending desktop input");
            FindButton("Stop playback").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>!engine.IsPlaying,"Stop after double click failed");
            for(int i=0;i<4;i++)FindButton("Play selected file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>engine.IsPlaying,"rapid Play requests did not settle");await Task.Delay(60);
            Check(engine.IsPlaying,"rapid Play clicks settle on one active playback");
            var player=Field<object>(engine,"player");var pcm=Field<float[]>(engine,"samples");int count=sound.PlayCount;
            var categories=Field<TreeView>(window,"CategoryTree");object categoryRoot=categories.Items[0];
            // This computed column has no Binding.Path. Statistics updates must
            // remain safe while it is sorted, as well as for ordinary fields.
            Invoke(window,"ApplySoundSort",grid.Columns.First(x=>x.SortMemberPath=="@Hotkey"),System.ComponentModel.ListSortDirection.Ascending);
            var transition=Field<SemaphoreSlim>(window,"playTransition");var cacheGate=Field<SemaphoreSlim>(Field<object>(engine,"cache"),"gate");
            await transition.WaitAsync();await cacheGate.WaitAsync();Task<bool>? replay=null;
            try{
                lock(Field<object>(engine,"gate")){
                    int version=engine.TimelineVersion;var worker=Field<Task>(engine,"playbackTask");
                    replay=(Task<bool>)Invoke(window,"PlayEntryAsync",sound,Missing.Value,false)!;
                    Check(replay.IsCompletedSuccessfully&&replay.Result,"replaying the active sound bypasses both playback setup and the decoder/cache gate");
                    Check(Field<int>(engine,"requestedSeek")==0&&engine.TimelineVersion==version&&ReferenceEquals(worker,Field<Task>(engine,"playbackTask")),"replay queues the seek bar's zero-position seek on the existing worker");
                }
            }
            finally{cacheGate.Release();transition.Release();if(replay is not null)await replay;}
            count=sound.PlayCount;
            for(int click=0;click<20;click++){
                FindButton("Play selected file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(10);
            }
            Check(sound.PlayCount==count+20&&engine.IsPlaying&&ReferenceEquals(player,Field<object>(engine,"player"))&&ReferenceEquals(pcm,Field<float[]>(engine,"samples")),"every rapid Play click restarts cached audio on the same output stream");
            Check(ReferenceEquals(categoryRoot,categories.Items[0]),"playback statistics preserve the existing category tree throughout rapid clicks");
            await Until(()=>Field<TextBlock>(window,"LibraryCount").Text.Contains(sound.PlayCount+" plays"),"queued play-count display did not refresh");
            Check(true,"lightweight playback updates keep the displayed play count current");
            count=sound.PlayCount;
            for(int click=1;click<=20;click++){
                PressCell(click);
                await Task.Delay(10);
            }
            Check(sound.PlayCount==count+10&&ReferenceEquals(player,Field<object>(engine,"player")),"a raw click-count burst from 1 through 20 triggers all ten pairs, including counts above two");
            count=sound.PlayCount;PressCell(1);PressCell(3);PressCell(5);
            Check(sound.PlayCount==count,"single presses and unpaired odd counts do not start extra sounds");
            var header=Descendants<DataGridColumnHeader>(grid).First(x=>x.Column is not null);
            var headerPress=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent};
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(headerPress,4);header.RaiseEvent(headerPress);
            Check(sound.PlayCount==count,"repeated column-header clicks never replay the selected sound");
            PressCell(4,Descendants<DataGridCell>(grid).First(x=>ReferenceEquals(x.DataContext,sound)&&x.Column.SortMemberPath=="@Hotkey"));
            Check(sound.PlayCount==count,"repeated hotkey-cell presses do not start audio before the hotkey editor opens");
            Invoke(window,"UpdatePlayback",new PlaybackProgressEventArgs(TimeSpan.Zero,engine.Duration,false,true,engine.TimelineVersion-1));
            Check(Field<bool>(window,"hadPlayback")&&engine.IsPlaying,"a delayed completion event from an older trigger cannot finish the current playback");
            await CheckHotkeyPriority(window,library,sound);
            await CheckPlayModeHotkeys(window);
            sound.FilePath=Path.Combine(temp,"missing.wav");FindButton("Play selected file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status=Field<TextBlock>(window,"StatusText");await Until(()=>status.Visibility==Visibility.Visible,"playback failure was not made visible");
            Check(!engine.IsPlaying&&status.Visibility==Visibility.Visible&&!string.IsNullOrWhiteSpace(status.Text),"a failed replacement stops the old sound and displays an inline error");
            CheckHotkeyEditor(window,library,sound);
            CheckMenusAndSpecialHotkeys(window);
            await CheckPreferencePages(window,temp);
            Check(!window.IsVisible&&!window.IsActive,"playback and error checks leave the test window hidden and inactive");
        }
        finally {
            if(window is not null){await (Task)Invoke(window,"StopAsync")!;bool closed=false;window.Closed+=(_,_)=>closed=true;window.Close();await Until(()=>closed,"Hidden test window did not close");}
            Directory.Delete(temp,true);
        }
        Console.WriteLine($"PASS {checks} headless WPF control checks. No window shown, no mouse or keyboard input sent; playback remained muted.");
    }
    private static async Task CheckSearchAndCategoryLayout(MainWindow window,LibraryStore library,SoundEntry existing,string temp)
    {
        var root=(FrameworkElement)window.Content;var grid=Field<DataGrid>(window,"SoundGrid");
        async Task Layout(){root.Measure(new Size(1020,660));root.Arrange(new Rect(0,0,1020,660));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ContextIdle);root.UpdateLayout();}
        Check(Field<object?>(window,"applicationSessionVolume") is null&&typeof(AudioService).GetProperty("ApplicationVolume",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null) is null,"hidden UI checks never start the application's Windows Mixer stream or volume controller");
        var added=new List<SoundEntry>();foreach(string title in new[]{"Amber tone","Amber bell","Blue tone"}){string file=Path.Combine(temp,title+".wav");File.Copy(existing.FilePath,file);var entry=library.AddFiles([file]).Single();entry.Title=title;added.Add(entry);}
        var search=Field<TextBox>(window,"SearchBox");Field<FrameworkElement>(window,"SearchPanel").Visibility=Visibility.Visible;await Layout();
        var searchPanel=Field<FrameworkElement>(window,"SearchPanel");var searchControls=Field<FrameworkElement>(window,"SearchControls");
        Check(searchPanel.ActualHeight==26&&search.ActualWidth==236&&searchControls.HorizontalAlignment==HorizontalAlignment.Right&&Math.Abs(searchControls.TranslatePoint(new Point(searchControls.ActualWidth,0),searchPanel).X-(searchPanel.ActualWidth-4))<1,"search uses the reference's compact right-aligned field and 26-pixel row");
        Check(Field<Button>(window,"SearchNextButton").ActualWidth==24&&Field<Button>(window,"CloseSearchButton").ActualWidth==24,"search navigation and close use the reference's compact bordered button size");
        search.Text="Amber";await Layout();
        Check(grid.Items.Count==4&&ReferenceEquals(grid.SelectedItem,added[0]),"incremental search selects the first match while preserving every row");
        Field<Button>(window,"SearchNextButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReferenceEquals(grid.SelectedItem,added[1]),"the actual Find next button advances to the next match");
        Field<Button>(window,"SearchPreviousButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReferenceEquals(grid.SelectedItem,added[0]),"the actual Find previous button selects the previous match");
        Invoke(window,"FindSearchMatch",-1);Check(ReferenceEquals(grid.SelectedItem,added[1]),"search navigation wraps around the match list");
        search.Text="3";Check(ReferenceEquals(grid.SelectedItem,added[1]),"a single numeric query finds the original sound index");
        search.Text="A";Check(!Field<Button>(window,"SearchNextButton").IsEnabled&&grid.Items.Count==4,"single-letter typing waits for more text without filtering the library");
        search.Text="no such sound";Check(!Field<Button>(window,"SearchNextButton").IsEnabled&&added.All(x=>x.PlayCount==0),"a search without matches disables navigation and never plays a sound");
        library.AddCategory("Search Amber");library.AddCategory("Search Blue");added[0].Category=added[1].Category="Search Amber";added[2].Category="Search Blue";await Layout();search.Text="Amber";
        Invoke(window,"CategoryTree_Changed",Field<TreeView>(window,"CategoryTree"),new RoutedPropertyChangedEventArgs<object>(existing,new TreeViewItem {Tag="Search Blue"}));
        Check(grid.Items.Count==1&&!Field<Button>(window,"SearchNextButton").IsEnabled,"switching categories refreshes navigation for an existing search query");
        Invoke(window,"CategoryTree_Changed",Field<TreeView>(window,"CategoryTree"),new RoutedPropertyChangedEventArgs<object>(existing,new TreeViewItem {Tag="Search Amber"}));
        Check(grid.Items.Count==2&&Field<Button>(window,"SearchNextButton").IsEnabled,"search navigation re-enables when the selected category contains matches");
        Invoke(window,"CategoryTree_Changed",Field<TreeView>(window,"CategoryTree"),new RoutedPropertyChangedEventArgs<object>(existing,new TreeViewItem {Tag="*"}));
        Invoke(window,"ResetSearch");Check(search.Text==""&&Field<FrameworkElement>(window,"SearchPanel").Visibility==Visibility.Collapsed&&grid.Items.Count==4,"resetting search closes the panel and retains the complete list");
        library.Remove(added);library.RemoveCategory("Search Amber");library.RemoveCategory("Search Blue");await Layout();
        var settings=Field<PreferenceStore>(window,"preferenceStore").Settings;var speaker=Field<Slider>(window,"SpeakerSlider");var clip=Field<Slider>(window,"ClipSlider");
        speaker.Value=.37;clip.Value=1.5;
        Check(speaker.ToolTip is ToolTip {Content:"37%",IsOpen:false}&&clip.ToolTip is ToolTip {Content:"150%",IsOpen:false},"speaker and microphone sliders maintain percentage feedback without opening test popups");
        var volumeTip=(ToolTip)speaker.ToolTip;
        Check(volumeTip.Placement==PlacementMode.RelativePoint&&ReferenceEquals(volumeTip.PlacementTarget,speaker)&&volumeTip.Background is SolidColorBrush {Color:{R:51,G:51,B:51}}&&volumeTip.Template is not null,"volume feedback uses a grey template positioned relative to the pointer, rather than the thumb");
        var track=(Track)speaker.Template.FindName("PART_Track",speaker);
        var moveVolume=typeof(MainWindow).Assembly.GetType("SoundboardLab.VolumeSliderFeedback")!.GetMethod("SetValueFromPointer",BindingFlags.Static|BindingFlags.NonPublic)!;
        void MoveVolume(double x,double y)=>moveVolume.Invoke(null,[speaker,track,new Point(x,y)]);
        speaker.Value=.8;await Layout();
        double thumbWidth=track.Thumb.ActualWidth,travel=track.ActualWidth-thumbWidth;
        // WPF can change Value before arranging the thumb at its new position.
        // Reproduce that real mouse-down ordering without capturing the mouse.
        speaker.Value=.2;MoveVolume(thumbWidth/2+travel*.2,track.ActualHeight/2);
        Check(Math.Abs(speaker.Value-.2)<.0001,"a track click stays at 20 percent while the thumb's layout still reflects 80 percent");
        foreach(double ratio in new[]{.4,.65,.15,.9,.2}){
            MoveVolume(thumbWidth/2+travel*ratio,-200);
            Check(Math.Abs(speaker.Value-ratio)<.0001,"successive volume positions remain accurate before layout catches up: "+ratio);
        }
        MoveVolume(track.ActualWidth*.3,-200);double above=speaker.Value;MoveVolume(track.ActualWidth*.7,300);
        Check(above>0&&speaker.Value>above,"volume pointer projection continues horizontally far above and below the slider, without injecting mouse input");
        MoveVolume(-500,-200);bool atStart=speaker.Value==0;MoveVolume(track.ActualWidth+500,300);
        Check(atStart&&speaker.Value==1,"captured volume drags clamp at either end outside the track");
        speaker.Value=0;await Layout();var volumeIcon=Field<HeadphonesVolumeIcon>(window,"SpeakerVolumeIcon");
        byte[] VolumeIconPixels(){var drawing=new DrawingVisual();using(var paint=drawing.RenderOpen())paint.DrawRectangle(new VisualBrush(volumeIcon){Stretch=Stretch.Fill},null,new Rect(0,0,16,16));var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(16,16,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);var pixels=new byte[16*16*4];bitmap.CopyPixels(pixels,16*4,0);return pixels;}
        int RedPixels(){var pixels=VolumeIconPixels();return Enumerable.Range(0,256).Count(i=>pixels[i*4+2]>pixels[i*4]+40&&pixels[i*4+2]>pixels[i*4+1]+40);}
        Check(volumeIcon.IsMuted&&RedPixels()>5,"zero speaker volume renders the reference's red mute cross over its copied headphones artwork");
        speaker.Value=.5;await Layout();Check(!volumeIcon.IsMuted&&RedPixels()==0,"restoring speaker volume removes the headphones mute cross");
        var iconSamples=new Dictionary<int,byte[]>();
        foreach(int percent in new[]{0,1,20,33,34,50,66,67,80,100}){speaker.Value=percent/100d;await Layout();iconSamples[percent]=VolumeIconPixels();}
        Check(iconSamples[1].SequenceEqual(iconSamples[20])&&iconSamples[1].SequenceEqual(iconSamples[33]),"the copied low-volume icon remains unchanged from 1 through 33 percent");
        Check(iconSamples[34].SequenceEqual(iconSamples[50])&&iconSamples[34].SequenceEqual(iconSamples[66]),"the copied medium-volume icon covers 34 through 66 percent");
        Check(iconSamples[67].SequenceEqual(iconSamples[80])&&iconSamples[67].SequenceEqual(iconSamples[100]),"the copied high-volume icon covers 67 through 100 percent");
        Check(!iconSamples[0].SequenceEqual(iconSamples[1])&&!iconSamples[33].SequenceEqual(iconSamples[34])&&!iconSamples[66].SequenceEqual(iconSamples[67]),"rendered headphones artwork changes at the original 0/1, 33/34 and 66/67 percent boundaries");
        speaker.Value=.4;settings.LinearVolumeSlider=false;speaker.Value=.5;Check(((ToolTip)speaker.ToolTip).Content as string=="25%","nonlinear slider feedback displays the applied gain");settings.LinearVolumeSlider=true;speaker.Value=0;clip.Value=1;
        foreach(float level in new[]{.2f,.4f,.7f}){
            Invoke(window,"ApplyApplicationVolume",level);
            Check(Math.Abs(speaker.Value-level)<.0001&&Math.Abs(volumeIcon.Volume-level)<.0001&&Math.Abs(settings.SpeakerVolume-level)<.0001&&Field<AudioService>(window,"audio").SpeakerVolume==level,"incoming Mixer level updates the slider, icon, saved gain, and audio state at "+level);
        }
        settings.LinearVolumeSlider=false;Invoke(window,"ApplyApplicationVolume",.25f);Check(Math.Abs(speaker.Value-.5)<.0001,"incoming Mixer gain respects the nonlinear volume setting");settings.LinearVolumeSlider=true;speaker.Value=0;
        bool initialized=Field<bool>(window,"initialized");typeof(MainWindow).GetField("initialized",Private)!.SetValue(window,false);
        try{
            foreach(string name in new[]{"HotkeysBox","AutoKeysBox"}){
                var box=Field<CheckBox>(window,name);bool? previous=box.IsChecked;box.IsChecked=true;await Layout();
                var chrome=(Border)box.Template.FindName("CheckChrome",box);var icon=(FrameworkElement)box.Content;double top=icon.TranslatePoint(new Point(),chrome).Y,bottom=chrome.ActualHeight-top-icon.ActualHeight;
                Check(top>=3&&bottom>=3&&Math.Abs(top-bottom)<=1,name+" has a visible enabled highlight above and below its icon");box.IsChecked=previous;
            }
        }finally{typeof(MainWindow).GetField("initialized",Private)!.SetValue(window,initialized);}
        var originalCategories=library.Document.Categories.ToArray();for(int i=0;i<40;i++)library.AddCategory("Layout "+i);
        await Layout();await Layout();var tree=Field<TreeView>(window,"CategoryTree");
        var number=((Grid)((TreeViewItem)tree.Items[0]).Header).Children.OfType<TextBlock>().First();var header=Descendants<ContentPresenter>(Field<Button>(window,"CategoryCountHeader")).First();
        double countCenter=number.TranslatePoint(new Point(number.ActualWidth/2,0),root).X,headerCenter=header.TranslatePoint(new Point(header.ActualWidth/2,0),root).X;
        Check(Descendants<ScrollBar>(tree).Any(x=>x.Orientation==Orientation.Vertical&&x.Visibility==Visibility.Visible)&&Math.Abs(countCenter-headerCenter)<1.1,"the category # header follows the actual count column when its scrollbar appears");
        var category=new CategoryEntry {Name="Voice"};
        var heading=(Grid)Invoke(window,"CategoryHeader","Voice",0,false,category)!;
        Check(Descendants<TextBlock>(heading).Any(x=>x.Text=="V"),"a new category renders its first letter instead of a folder");
        category.IconMode="Stock";category.StockIcon="Folder";heading=(Grid)Invoke(window,"CategoryHeader","Voice",0,false,category)!;
        Check(Descendants<TextBlock>(heading).Any(x=>x.Text=="V"),"legacy default folder categories also render generated initials");
        category.IconModeExplicit=true;heading=(Grid)Invoke(window,"CategoryHeader","Voice",0,false,category)!;
        Check(Descendants<System.Windows.Shapes.Path>(heading).Any(),"explicitly chosen stock icons remain available");
        var editor=(Window)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("SoundboardLab.CategoryEditWindow")!,[new CategoryEntry {Name="Voice"},new Func<Window,string,string?>((_,_)=>null)])!;
        try{
            var body=(FrameworkElement)editor.Content;body.Measure(new Size(370,306));body.Arrange(new Rect(0,0,370,306));body.UpdateLayout();
            var generated=Descendants<RadioButton>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="CategoryIconGenerated");
            var text=Descendants<TextBlock>(generated).Single(x=>x.Text=="Generated");
            Check(text.ActualWidth>=text.DesiredSize.Width-.5&&text.TranslatePoint(new Point(text.ActualWidth,0),(FrameworkElement)generated.Parent).X<=((FrameworkElement)generated.Parent).ActualWidth+.5,"the Generated option fits its label inside the category editor");
        }finally{editor.Close();}
        foreach(var categoryToRemove in library.Document.Categories.Except(originalCategories).ToArray())library.Document.Categories.Remove(categoryToRemove);
        await Layout();grid.SelectedItem=existing;
        Check(!window.IsVisible&&!window.IsActive,"search, category and slider checks leave the real application hidden and inactive");
    }
    private static async Task CheckMissingSoundsAndColors(MainWindow window,LibraryStore library,SoundEntry existing,string temp)
    {
        var grid=Field<DataGrid>(window,"SoundGrid");var root=(FrameworkElement)window.Content;
        async Task Layout(){root.Measure(new Size(1020,660));root.Arrange(new Rect(0,0,1020,660));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ContextIdle);root.UpdateLayout();}
        string path=Path.Combine(temp,"removed.wav");File.Copy(existing.FilePath,path);var missing=library.AddFiles([path]).Single();
        library.Save(Path.Combine(temp,"appearance.json"));int undoCount=Field<Stack<string>>(library,"undo").Count;File.Delete(path);await Layout();grid.SelectedItem=missing;
        bool played=await (Task<bool>)Invoke(window,"PlayEntryAsync",missing,Missing.Value,false)!;await Layout();
        Check(!played&&missing.IsMissing&&missing.PlayCount==0&&!Field<AudioService>(window,"audio").IsPlaying,"playing a deleted file marks its row missing without starting audio or counting a play");
        Check(!library.HasUnsavedChanges&&Field<Stack<string>>(library,"undo").Count==undoCount&&!System.Text.Json.JsonSerializer.Serialize(library.Document).Contains("IsMissing"),"missing-file discovery does not dirty, persist or add undo entries to the library");
        var row=Descendants<DataGridRow>(grid).Single(x=>ReferenceEquals(x.Item,missing));
        var label=Descendants<DataGridCell>(row).Where(x=>x.Column.Header as string=="Tag").SelectMany(Descendants<TextBlock>).Single();
        Color muted=((SolidColorBrush)Application.Current.FindResource("Muted")).Color;
        Check(label.TextDecorations.Any(x=>x.Location==TextDecorationLocation.Strikethrough)&&((SolidColorBrush)label.Foreground).Color==muted,"the selected missing sound is struck through and grey in the actual rendered Tag cell");
        grid.SelectedItem=existing;await Layout();
        Check(((SolidColorBrush)row.Foreground).Color==muted,"a missing sound remains grey when selection moves to another row");
        missing.FilePath=existing.FilePath;await Layout();
        Check(!missing.IsMissing&&label.TextDecorations.Count==0,"relinking a missing entry restores its normal text decoration");
        library.Remove([missing]);await Layout();grid.SelectedItem=existing;
        var color=grid.ContextMenu.Items.OfType<MenuItem>().Single(x=>x.Header as string=="Color");
        var options=color.Items.OfType<MenuItem>().ToArray();
        Check(options.First().Header as string=="Open color tool"&&options.Last().Header as string=="Reset color"&&options.Length==23,"the sound color submenu offers the recovered 21 populated slots between Open color tool and Reset color");
        Check(options.Where(x=>x.Tag is string).All(x=>x.Icon is Border {Background:SolidColorBrush fill}&&fill.Color==(Color)ColorConverter.ConvertFromString((string)x.Tag)),"every numbered sound color has a matching visible color swatch");
        options.Single(x=>x.Header as string=="Color 14").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Layout();grid.SelectedItem=null;await Layout();
        row=Descendants<DataGridRow>(grid).Single(x=>ReferenceEquals(x.Item,existing));
        Check(existing.Color=="#F2E71F"&&((SolidColorBrush)row.Background).Color==Color.FromRgb(242,231,31)&&((SolidColorBrush)row.Foreground).Color==Colors.Black,"choosing a bright palette color paints the sound row and uses readable black text");
        grid.SelectedItem=existing;options.Single(x=>x.Header as string=="Color 3").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));grid.SelectedItem=null;await Layout();
        Check(((SolidColorBrush)row.Background).Color==Color.FromRgb(9,78,124)&&((SolidColorBrush)row.Foreground).Color==Colors.White,"choosing a dark palette color uses readable white text");
        grid.SelectedItem=existing;options.Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Layout();
        Check(existing.Color=="#334155","Reset color restores the default row appearance");
        Invoke(window,"SetSoundColorSlot",23,"#123456");Invoke(window,"PopulateSoundColorMenu",color,new Func<IEnumerable<SoundEntry>>(()=>[existing]));
        Check(color.Items.OfType<MenuItem>().Single(x=>x.Header as string=="Color 24").Tag as string=="#123456","a populated spare palette slot appears with its original slot number");
        var preferences=new PreferenceStore(temp);preferences.Load();Check(preferences.Settings.SoundColorSlots[23]=="#123456","custom palette slots survive preference reload");
        Invoke(window,"SetSoundColorSlot",23,"");Invoke(window,"PopulateSoundColorMenu",color,new Func<IEnumerable<SoundEntry>>(()=>[existing]));
        var tool=(Window)Invoke(window,"CreateSoundColorTool")!;
        try{var panel=(FrameworkElement)tool.Content;panel.Measure(new Size(354,300));panel.Arrange(new Rect(0,0,354,300));panel.UpdateLayout();Check(Descendants<Button>(panel).Count(x=>AutomationProperties.GetAutomationId(x).StartsWith("SoundColorSlot"))==24,"Color tool renders all 24 configurable palette slots without opening a window");}finally{tool.Close();}
        grid.SelectedItem=existing;Field<TextBlock>(window,"StatusText").Visibility=Visibility.Collapsed;
    }
    private static void CheckHotkeyEditor(MainWindow window,LibraryStore library,SoundEntry sound)
    {
        var prefs=Field<PreferenceStore>(window,"preferenceStore");var settings=prefs.Settings;
        var hooks=Field<GlobalHotkeyService>(window,"hotkeys");
        string target=(string)Invoke(window,"SoundHotkeyTarget",sound)!;
        Window Editor(string current="")=>(Window)Invoke(window,"CreateHotkeyEditor",window,current,target,null,null)!;
        using var isolatedHooks=new GlobalHotkeyService();
        var dialog=Editor();
        try {
            var body=(FrameworkElement)dialog.Content;body.Measure(new Size(350,400));body.Arrange(new Rect(0,0,350,400));body.UpdateLayout();
            Check(dialog.Title=="Set hotkey"&&dialog.Width==266&&Descendants<GroupBox>(body).Any(x=>x.Header as string=="Press a key")&&Descendants<Button>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="RemoveHotkey").Visibility==Visibility.Collapsed,"a new hotkey uses the compact reference Set hotkey group with no remove button");
            var capture=Descendants<TextBox>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="HotkeyCapture");Check(capture.IsReadOnly&&capture.IsReadOnlyCaretVisible&&capture.ActualHeight==23,"the capture field looks like the reference input while recording chords instead of accepting typed shortcut text");
            Invoke(dialog,"Record",new HotkeyInput(162,1,true));
            Check(!(bool)Invoke(dialog,"ValidateSelection")!&&capture.Text=="Ctrl","a modifier preview is visible and cannot be committed as a sound binding");
            Invoke(dialog,"Record",new HotkeyInput(75,1,true));
            Check((bool)Invoke(dialog,"ValidateSelection")!&&capture.Text=="Ctrl+K","a captured chord validates without typing its name");
            foreach(var key in new[]{Key.Escape,Key.Enter,Key.Tab}){
                var press=new KeyEventArgs(Keyboard.PrimaryDevice,HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                // The event goes directly to the hidden control, never to Windows.
                capture.RaiseEvent(press);string expected=key==Key.Enter?"Enter":key.ToString();
                Check(press.Handled&&capture.Text.EndsWith(expected),$"the real capture control records {key} without invoking dialog navigation");
            }
            Descendants<Button>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="RemoveHotkey").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((bool)Invoke(dialog,"ValidateSelection")!&&Field<HotkeyCaptureState>(dialog,"state").Gesture=="","the actual Remove hotkey button creates a valid empty assignment");
            Check(!hooks.IsStarted&&settings.RowHotkeys.Count==0&&sound.Hotkey.Length==0,"editing and closing an unshown draft does not install hooks or mutate stored bindings");
        } finally{dialog.Close();}

        // Inject only the confirmation callback; no message box or input is used.
        var type=typeof(MainWindow).Assembly.GetType("SoundboardLab.HotkeyCaptureWindow")!;
        bool allowOverwrite=false;int confirmations=0;
        var duplicate=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,
            new object?[]{"Ctrl+K",isolatedHooks,false,new Func<string,HotkeyConflict>(_=>new(false,true)),new Func<string,bool>(_=>{confirmations++;return allowOverwrite;})},null)!;
        try{
            Check(!(bool)Invoke(duplicate,"ValidateSelection")!&&confirmations==1,"declining overwrite keeps the hotkey dialog open");
            var editBody=(FrameworkElement)duplicate.Content;editBody.Measure(new Size(278,200));editBody.Arrange(new Rect(0,0,278,200));editBody.UpdateLayout();
            var remove=Descendants<Button>(editBody).Single(x=>AutomationProperties.GetAutomationId(x)=="RemoveHotkey");var field=Descendants<TextBox>(editBody).Single(x=>AutomationProperties.GetAutomationId(x)=="HotkeyCapture");
            Check(duplicate.Title=="Edit hotkey"&&duplicate.Width==294&&remove.Visibility==Visibility.Visible&&remove.Content as string=="X"&&ReferenceEquals(remove.Parent,field.Parent),"an assigned hotkey uses the reference Edit hotkey width and inline X removal control");
            allowOverwrite=true;Check((bool)Invoke(duplicate,"ValidateSelection")!&&confirmations==2,"accepting overwrite permits the captured assignment");
        }finally{duplicate.Close();}

        settings.ActionHotkeys["stop"]="Ctrl+F24";
        var protectedDialog=Editor();
        try{
            Invoke(protectedDialog,"Record",new HotkeyInput(135,1,true));
            Check(!(bool)Invoke(protectedDialog,"ValidateSelection")!,"the real sound editor rejects a binding already held by a special action");
        }finally{protectedDialog.Close();}
        settings.ActionHotkeys.Clear();
        Invoke(window,"AssignHotkey",target,"Ctrl+1");
        Check(hooks.Bindings.Any(x=>x.Gesture=="Ctrl+1"&&!x.IsSequence),"a saved single-digit chord is configured as an ordinary hotkey even when Alt+Numcode is disabled");
        Invoke(window,"AssignHotkey","category:*","Ctrl+1");
        Check(library.Document.RootCategory.Hotkey=="Ctrl+1"&&settings.RowHotkeys.Values.All(x=>x!="Ctrl+1"),"confirmed reassignment moves the binding from a sound row to My Sounds");
        Invoke(window,"AssignHotkey","category:*","");
        Check(library.Document.RootCategory.Hotkey.Length==0&&!hooks.Bindings.Any(x=>x.Gesture=="Ctrl+1"),"removing the root-category binding updates the configured hooks immediately");
        settings.SameHotkeysInAllCategories=false;
        Invoke(window,"AssignHotkey","sound:"+sound.Id,"Ctrl+K");
        Check(sound.Hotkey=="Ctrl+K"&&hooks.Bindings.Any(x=>x.Gesture=="Ctrl+K"&&x.Action=="play:"+sound.Id),"per-sound assignment configures the selected sound immediately");
        Invoke(window,"AssignHotkey","hotbar:0","Ctrl+K");
        var recovered=new PreferenceStore((string)typeof(MainWindow).GetProperty("StateDirectory",Private)!.GetValue(window)!);recovered.Load();
        Check(sound.Hotkey==""&&settings.HotbarHotkeys[0]=="Ctrl+K"&&recovered.Settings.HotbarHotkeys[0]=="Ctrl+K","hotbar reassignment clears the previous sound binding and persists the slot shortcut");
        Invoke(window,"AssignHotkey","hotbar:0","");
        settings.SameHotkeysInAllCategories=true;settings.RowHotkeys.Clear();sound.Hotkey="Ctrl+L";
        var draft=System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        draft.ActionHotkeys["stop"]="Ctrl+L";
        Check(sound.Hotkey=="Ctrl+L"&&settings.ActionHotkeys.Count==0&&settings.RowHotkeys.Count==0,"a Preferences hotkey draft leaves live sound and special bindings unchanged before Apply");
        var restore=(Action)Invoke(window,"PrepareSpecialHotkeyAssignments",draft)!;
        Check(draft.RowHotkeys.GetValueOrDefault(0)==""&&settings.RowHotkeys.GetValueOrDefault(0)==""&&sound.Hotkey=="","applying a special binding clears both the legacy fallback and its row in the draft");
        restore();
        Check(settings.RowHotkeys.Count==0&&settings.HotbarHotkeys.Count==0&&sound.Hotkey=="Ctrl+L","a failed Preferences apply restores the exact previous row/slot tables and the sound shortcut");
        sound.Hotkey="";
    }
    private static async Task CheckHotkeyPriority(MainWindow window,LibraryStore library,SoundEntry sound)
    {
        await (Task)Invoke(window,"StopAsync")!;
        var settings=Field<PreferenceStore>(window,"preferenceStore").Settings;
        var hooks=Field<GlobalHotkeyService>(window,"hotkeys");var engine=Field<AudioService>(window,"audio");
        var indicator=Field<CheckBox>(window,"HotkeysBox");
        Check(indicator.ToolTip.ToString()!.Contains("disabled",StringComparison.OrdinalIgnoreCase)&&indicator.Opacity<1,"disabled hotkeys are visible in the actual status indicator and its tooltip");
        // Check the status-icon click handler without initializing its OS hooks
        // or playing notifications. No pointer event is sent to Windows.
        typeof(MainWindow).GetField("initialized",Private)!.SetValue(window,false);
        try {
            MouseButtonEventArgs Press(int count){var e=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent};typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e,count);return e;}
            var single=Press(1);Invoke(window,"Hotkeys_Pressed",indicator,single);
            Check(single.Handled&&indicator.IsChecked==false,"a single click on H no longer accidentally disables or enables global hotkeys");
            var pair=Press(2);Invoke(window,"Hotkeys_Pressed",indicator,pair);
            Check(pair.Handled&&indicator.IsChecked==true&&!hooks.IsStarted,"a double-click toggles H without a desktop click or test hook installation");
            indicator.IsChecked=false;
        }finally{typeof(MainWindow).GetField("initialized",Private)!.SetValue(window,true);}
        settings.RowHotkeys[0]="F2";settings.HotbarHotkeys[0]="F2";settings.ActionHotkeys["play"]="F2";library.Document.RootCategory.Hotkey="F2";
        // Finish the queued structural refresh while OS hooks stay disabled,
        // then exercise enabled dispatch directly without installing those hooks.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var definitions=(List<HotkeyBinding>)Invoke(window,"BuildHotkeyBindings")!;
        hooks.Configure(definitions,new(){Enabled=true,SuppressMatchingKeys=true});
        Check(hooks.ResolveAction("F2")=="play"&&!hooks.IsStarted,"real application bindings resolve special F2 over hotbar, root category and sound rows without installing hooks");
        Field<DataGrid>(window,"SoundGrid").SelectedItem=sound;int count=sound.PlayCount;
        bool consumed=(bool)Invoke(hooks,"ProcessKeyboardInput",new HotkeyInput(113,0,true))!;
        await Until(()=>engine.IsPlaying&&sound.PlayCount==count+1,"background F2 did not reach the real playback action");
        Check(consumed&&engine.IsPlaying&&!window.IsVisible&&!window.IsActive,"the actual hook dispatch path plays special F2 while the application window is hidden and inactive");
        var press=new KeyEventArgs(Keyboard.PrimaryDevice,HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!,Environment.TickCount,Key.F2){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        Check((bool)Invoke(window,"TryHandleGlobalHotkey",press,ModifierKeys.None)!&&press.Handled,"the main-window priority gate consumes a bound F2 before its Rename shortcut");
        Check((bool)Invoke(hooks,"ProcessKeyboardInput",new HotkeyInput(113,0,true,true))!&&sound.PlayCount==count+1,"holding F2 remains consumed without repeating audio or falling through to Rename");
        hooks.Configure(definitions,new(){Enabled=true,SuppressMatchingKeys=false});
        var pass=new KeyEventArgs(Keyboard.PrimaryDevice,press.InputSource,Environment.TickCount,Key.F2){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        Check(!(bool)Invoke(window,"TryHandleGlobalHotkey",pass,ModifierKeys.None)!&&!pass.Handled,"Pass hotkeys deliberately preserves foreground shortcut propagation");
        hooks.Configure(definitions,new(){Enabled=false,SuppressMatchingKeys=true});
        var disabled=new KeyEventArgs(Keyboard.PrimaryDevice,press.InputSource,Environment.TickCount,Key.F2){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        Check(!(bool)Invoke(hooks,"ProcessKeyboardInput",new HotkeyInput(113,0,true))!&&!(bool)Invoke(window,"TryHandleGlobalHotkey",disabled,ModifierKeys.None)!,"disabled F2 neither plays globally nor suppresses the normal local shortcut");
        await (Task)Invoke(window,"StopAsync")!;
        settings.RowHotkeys.Clear();settings.HotbarHotkeys.Clear();settings.ActionHotkeys.Clear();library.Document.RootCategory.Hotkey="";
        Invoke(window,"ConfigureHotkeys");
    }
    private static async Task CheckPlayModeHotkeys(MainWindow window)
    {
        var prefs=Field<PreferenceStore>(window,"preferenceStore").Settings;
        var engine=Field<AudioService>(window,"audio");var mode=Field<ComboBox>(window,"DestinationBox");
        var menu=Field<Button>(window,"ModeButton").ContextMenu!;
        string capture=prefs.CaptureDeviceId;var captures=prefs.CaptureDeviceIds;bool resume=prefs.AllowResumeInAnotherMode;
        try{
            // If a regression tries the stale microphone mode, it must fail on
            // this nonexistent endpoint before any physical capture can start.
            prefs.CaptureDeviceId="headless-test-no-capture-endpoint";prefs.CaptureDeviceIds=[prefs.CaptureDeviceId];
            await (Task)Invoke(window,"StopAsync")!;
            typeof(MainWindow).GetField("lastDestination",Private)!.SetValue(window,PlaybackDestination.Both);
            mode.SelectedIndex=2;
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await (Task)Invoke(window,"HandleActionAsync","play",true)!;
            Check(mode.SelectedIndex==1&&engine.IsPlaying&&Field<PlaybackDestination>(window,"lastDestination")==PlaybackDestination.Speakers,"changing play mode through its actual menu applies immediately to Start playback hotkeys");
            engine.Pause();prefs.AllowResumeInAnotherMode=true;
            typeof(MainWindow).GetField("lastDestination",Private)!.SetValue(window,PlaybackDestination.Microphone);
            await (Task)Invoke(window,"HandleActionAsync","play",true)!;
            Check(engine.IsPlaying&&Field<PlaybackDestination>(window,"lastDestination")==PlaybackDestination.Speakers,"resuming with Start playback honors the newly selected mode rather than resuming the old route");
            await (Task)Invoke(window,"StopAsync")!;
            typeof(MainWindow).GetField("lastDestination",Private)!.SetValue(window,PlaybackDestination.Microphone);
            await (Task)Invoke(window,"HandleActionAsync","playCurrent",true)!;
            Check(engine.IsPlaying&&Field<PlaybackDestination>(window,"lastDestination")==PlaybackDestination.Speakers,"Play current file again uses the selected play mode immediately");
        }finally{await (Task)Invoke(window,"StopAsync")!;prefs.CaptureDeviceId=capture;prefs.CaptureDeviceIds=captures;prefs.AllowResumeInAnotherMode=resume;mode.SelectedIndex=1;}
    }
    private static PreferenceDeviceSnapshot FixtureDevices()=>new([new("fixture-input","Microphone (Test audio)",true),new("fixture-output","Speakers (Test audio)",false)],"fixture-input",new HashSet<string>{"fixture-input"});
    private static async Task CheckPreferenceResponsiveness(MainWindow main)
    {
        var store=Field<PreferenceStore>(main,"preferenceStore");var audio=Field<AudioService>(main,"audio");
        int reads=0,readerThread=0,uiThread=Environment.CurrentManagedThreadId;
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var dialog=new SettingsWindow(main,audio,store,()=>Task.CompletedTask,null,token=>{Interlocked.Increment(ref reads);readerThread=Environment.CurrentManagedThreadId;entered.TrySetResult();release.Wait(token);return FixtureDevices();});
        Console.WriteLine($"Preferences constructor: {watch.Elapsed.TotalMilliseconds:F1} ms; no endpoint enumeration.");
        try{
            Check(reads==0&&Field<AudioService?>(dialog,"preview") is null,"constructing Preferences neither enumerates audio devices nor creates a preview audio engine");
            var refresh=dialog.RefreshDevicesAsync(true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(readerThread!=uiThread&&!refresh.IsCompleted,"a blocked device scan runs on a worker and leaves the Preferences dispatcher available");
            Check(ReferenceEquals(refresh,dialog.RefreshDevicesAsync(true))&&reads==1,"concurrent device refresh requests share a single snapshot operation");
            int ticks=0;var pulse=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};pulse.Tick+=(_,_)=>ticks++;pulse.Start();
            try{dialog.SelectPage("Hotkeys");Field<AppPreferences>(dialog,"draft").PlaybackDelayMs=47;await Task.Delay(100);Check(ticks>=2,"UI timer ticks and page changes continue while device enumeration is deliberately held");}
            finally{pulse.Stop();}
            release.Set();await refresh;await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Field<AppPreferences>(dialog,"draft").PlaybackDelayMs==47&&Field<Button>(dialog,"applyButton").IsEnabled,"late device discovery preserves and marks unsaved edits from other Preferences pages");
            dialog.SelectPage("Devices");var body=(FrameworkElement)dialog.Content;body.Measure(new Size(560,685));body.Arrange(new Rect(0,0,560,685));body.UpdateLayout();
            var deviceStatus=Descendants<TextBlock>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="RecordingDeviceStatus");
            Localization.Apply(dialog);Check(deviceStatus.Text=="Repair required"||deviceStatus.Text=="Good","a translation pass after discovery keeps the completed device status instead of restoring Loading");
            var deviceTable=Descendants<ListView>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="RecordingDevices");
            var row=deviceTable.Items[0];var use=row.GetType().GetProperty("Use")!;use.SetValue(row,false);Localization.Apply(dialog);
            Check(deviceStatus.Text=="Repair required"&&Field<AppPreferences>(dialog,"draft").CaptureDeviceIds.Count==0,"unchecking a recording device updates both its draft selection and status through later translation passes");
            use.SetValue(row,true);Localization.Apply(dialog);
            Check(deviceStatus.Text=="Good"&&Field<AppPreferences>(dialog,"draft").CaptureDeviceIds.SequenceEqual(new[]{"fixture-input"}),"checking an installed recording device remains Good after translation and stores the selected endpoint");
            Check(!dialog.IsVisible&&!dialog.IsActive,"asynchronous Preferences checks never show or activate a window");
        }finally{release.Set();dialog.Close();}
        int attempts=0;
        var retry=new SettingsWindow(main,audio,store,()=>Task.CompletedTask,null,_=>++attempts==1?throw new IOException("device scan fixture"):FixtureDevices());
        try{await retry.RefreshDevicesAsync(true);Check(Field<PreferenceDeviceSnapshot?>(retry,"deviceSnapshot") is null,"device enumeration failure is handled inside Preferences without a blocking dialog");await retry.RefreshDevicesAsync(true);Check(attempts==2&&Field<PreferenceDeviceSnapshot?>(retry,"deviceSnapshot") is not null,"a failed device refresh can be retried successfully");}finally{retry.Close();}
        using var timeoutRelease=new ManualResetEventSlim();int timeoutReads=0;
        var timeout=new SettingsWindow(main,audio,store,()=>Task.CompletedTask,null,token=>{if(Interlocked.Increment(ref timeoutReads)==1)timeoutRelease.Wait(token);return FixtureDevices();}){DeviceDiscoveryTimeout=TimeSpan.FromMilliseconds(60)};
        try{
            await timeout.RefreshDevicesAsync(true).WaitAsync(TimeSpan.FromSeconds(3));timeout.SelectPage("Devices");var body=(FrameworkElement)timeout.Content;body.Measure(new Size(560,685));body.Arrange(new Rect(0,0,560,685));body.UpdateLayout();Localization.Apply(timeout);
            Check(Descendants<TextBlock>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="RecordingDeviceStatus").Text=="Could not load devices","a stalled Windows device scan times out and retains its error status after translation");
            await timeout.RefreshDevicesAsync(true);timeoutRelease.Set();await Task.Delay(40);
            Check(Field<PreferenceDeviceSnapshot?>(timeout,"deviceSnapshot") is not null&&timeoutReads==2,"a timed-out scan permits retry and its late result cannot replace the retry snapshot");
        }finally{timeoutRelease.Set();timeout.Close();}
        using var closingRelease=new ManualResetEventSlim();var closingEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing=new SettingsWindow(main,audio,store,()=>Task.CompletedTask,null,token=>{closingEntered.TrySetResult();closingRelease.Wait(token);return FixtureDevices();});
        var pending=closing.RefreshDevicesAsync(true);await closingEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));closing.Close();
        try{await pending.WaitAsync(TimeSpan.FromSeconds(3));Check(Field<PreferenceDeviceSnapshot?>(closing,"deviceSnapshot") is null,"closing Preferences cancels pending device discovery without applying its late results");}finally{closingRelease.Set();}
    }
    private static async Task CheckPreferencePages(MainWindow main,string dataDirectory)
    {
        var dialog=new SettingsWindow(main,Field<AudioService>(main,"audio"),Field<PreferenceStore>(main,"preferenceStore"),()=>Task.CompletedTask,null,_=>FixtureDevices());
        try{
            await dialog.RefreshDevicesAsync(true);
            var body=(FrameworkElement)dialog.Content;
            void Layout(string page){Invoke(dialog,"SelectPage",page);body.Measure(new Size(560,685));body.Arrange(new Rect(0,0,560,685));body.UpdateLayout();}
            T Control<T>(string id) where T:FrameworkElement=>Descendants<T>(body).Single(x=>AutomationProperties.GetAutomationId(x)==id);
            void Capture(string name){var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(560,685,96,96,PixelFormats.Pbgra32);bitmap.Render(body);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));string output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","preferences-"+name+".png"));using var stream=File.Create(output);png.Save(stream);}
            Layout("Audio");var forwardingSource=Control<ComboBox>("ForwardingSource");
            Check(forwardingSource.Items.Cast<string>().SequenceEqual(new[]{"Foreground app","Whole PC"})&&Control<ToggleButton>("ToggleAudioForwarding").IsChecked!=true,"forwarding preferences offer foreground-app and whole-PC capture and start switched off");
            Check(Control<Button>("PushAudioForwarding").Content as string=="Hold to forward"&&!Control<Button>("StopAudioForwarding").IsEnabled&&Control<TextBlock>("AudioForwardingStatus").Text=="Off","forwarding preferences provide held and stop controls with the live Off state");
            Check(Control<Slider>("ForwardingVolume").Minimum==0&&Control<Slider>("ForwardingVolume").Maximum==2&&Control<Slider>("ForwardingVolume").IsMoveToPointEnabled&&Control<Slider>("ForwardingVolume").LargeChange==.1&&!Control<CheckBox>("ForwardingMuteVoice").IsChecked.GetValueOrDefault(),"forwarding has a proportional volume control with bounded steps and optional voice muting");
            Layout("Interface");var graph=Control<FrameworkElement>("VolumeSliderGraph");var linear=Control<CheckBox>("LinearVolumeSlider");
            linear.IsChecked=true;Check((bool)graph.GetType().GetProperty("Linear")!.GetValue(graph)!&&graph.ActualWidth==100&&graph.ActualHeight==26,"Interface shows a compact linear volume-response graph beside the option");
            Capture("interface");linear.IsChecked=false;Check(!(bool)graph.GetType().GetProperty("Linear")!.GetValue(graph)!,"unchecking linear volume changes the same graph to the curved gain response");
            Layout("Recorder");var folder=Control<TextBox>("RecordingFolder");Check(folder.Text==Path.Combine(dataDirectory,"recordings")&&Path.IsPathFullyQualified(folder.Text),"Recorder displays its effective default save path instead of an empty field");
            string[] ids=["RecordingFormat","RecordingBitrate","RecordingCategory","RecordingInsertPosition","RecordingTargetDb"];
            var inputs=ids.Select(id=>Control<ComboBox>(id)).ToArray();var limit=Control<TextBox>("RecordingLimitSeconds");
            double left=inputs[0].TransformToAncestor(body).Transform(new Point()).X;
            Check(inputs.All(x=>Math.Abs(x.TransformToAncestor(body).Transform(new Point()).X-left)<1)&&Math.Abs(limit.TransformToAncestor(body).Transform(new Point()).X-left)<1,"all recorder dropdowns and the time limit share one aligned value column: "+string.Join(", ",inputs.Select(x=>AutomationProperties.GetAutomationId(x)+"="+x.TransformToAncestor(body).Transform(new Point()).X))+", limit="+limit.TransformToAncestor(body).Transform(new Point()).X);
            var format=inputs[0];var bitrate=inputs[1];format.SelectedItem="M4A";Check(!bitrate.IsEnabled,"M4A disables the Recorder bitrate field");format.SelectedItem="MP3";Check(bitrate.IsEnabled,"MP3 enables the Recorder bitrate field");
            var normalize=Control<CheckBox>("NormalizeRecordings");normalize.IsChecked=false;Check(!inputs[4].IsEnabled,"recording normalization target disables when automatic normalization is unchecked");normalize.IsChecked=true;inputs[4].Text="92.0 dB";
            Check(Field<AppPreferences>(dialog,"draft").RecordingTargetDb==92,"the inline Recorder dB dropdown updates the normalization target");
            limit.Text="300";Check(Control<TextBlock>("RecordingDuration").Text=="(5:00)","the Recorder time limit displays its minutes and seconds inline");Capture("recorder");
            Layout("Editor");var external=Control<CheckBox>("UseExternalEditor");external.IsChecked=false;
            var executable=Control<TextBox>("ExternalEditorPath");var arguments=Control<TextBox>("ExternalEditorArguments");var fields=Control<StackPanel>("ExternalEditorFields");
            Check(!executable.IsEnabled&&!arguments.IsEnabled&&Descendants<Button>(fields).All(x=>!x.IsEnabled)&&((SolidColorBrush)executable.Background).Color==Color.FromRgb(240,240,240),"unchecked external editor greys and disables executable, Browse and parameter inputs");Capture("editor");
            executable.Text="C:\\example\\editor.exe";arguments.Text="--edit {file}";external.IsChecked=true;
            Check(executable.IsEnabled&&arguments.IsEnabled&&Descendants<Button>(fields).All(x=>x.IsEnabled)&&executable.Text.EndsWith("editor.exe")&&arguments.Text=="--edit {file}","enabling external editor restores every input and preserves its saved values");
            Layout("Devices");var table=Control<ListView>("RecordingDevices");var columns=(GridView)table.View;
            Check(columns.Columns.Select(x=>x.Header as string).SequenceEqual(new[]{"Use","Device","Boost","Status"})&&table.ActualHeight>300,"Devices restores the four original columns and tall recording-device list");
            var menu=table.ContextMenu!;Check(menu.Items.OfType<MenuItem>().Select(x=>x.Uid).SequenceEqual(new[]{"Set boost","Set setup mode","Windows Audio Preferences","Restart Win-Audio"})&&menu.Items[2] is Separator,"the Devices context menu matches the recovered original actions and separator");
            table.SelectedItem=null;menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Check(menu.Items.OfType<MenuItem>().Where(x=>x.Visibility==Visibility.Visible).Select(x=>x.Uid).SequenceEqual(new[]{"Windows Audio Preferences","Restart Win-Audio"}),"the blank-area device menu omits device-specific commands like the original");
            Check(Control<Button>("RepairRecordingDevices").Content is StackPanel&&Control<Button>("RefreshRecordingDevices").ActualWidth==24&&!Descendants<Button>(body).Any(x=>x.Content as string=="Restart Win-Audio"),"Devices has the shield Repair and compact Refresh footer, with restart in its context menu");
            Check(Control<Button>("RepairRecordingDevices").TransformToAncestor(body).Transform(new Point(0,23)).Y<640,"the Devices footer remains visible without scrolling the Preferences page");
            if(table.Items.Count>0){
                table.SelectedIndex=0;menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));Check(menu.Items.OfType<MenuItem>().All(x=>x.Visibility==Visibility.Visible&&x.IsEnabled),"selecting a device restores its boost and setup commands without opening a desktop popup");
                var row=(ListViewItem)table.ItemContainerGenerator.ContainerFromIndex(0);Check(row.ActualHeight==68&&Descendants<TextBlock>(row).Any(x=>x.Text.Length>0)&&Descendants<System.Windows.Shapes.Path>(row).Any(),"recording-device rows render a microphone icon and separate endpoint/controller labels");
                var setup=(Window)Invoke(dialog,"CreateSetupModeWindow",table.Items[0],new Action(()=>{}))!;
                try{
                    var setupBody=(FrameworkElement)setup.Content;setupBody.Measure(new Size(438,400));setupBody.Arrange(new Rect(0,0,438,400));setupBody.UpdateLayout();
                    var mode=Descendants<ComboBox>(setupBody).Single();var apply=Descendants<Button>(setupBody).Single(x=>x.Content as string=="Apply");
                    Check(mode.Items.Cast<string>().SequenceEqual(new[]{"PostMix","Stream","Mode","Endpoint"})&&!apply.IsEnabled,"device setup exposes the four mixer installation slots and does not apply an unchanged mode");
                    mode.SelectedIndex=(mode.SelectedIndex+1)%mode.Items.Count;
                    Check(apply.IsEnabled&&!setup.IsVisible&&!setup.IsActive,"choosing another setup mode enables Apply without running setup or opening the test dialog");
                }finally{setup.Close();}
            }
            Capture("devices");
            Check(!dialog.IsVisible&&!dialog.IsActive,"Preferences layout and dependency checks leave the dialog hidden and inactive");
        }finally{dialog.Close();}
    }
    private static void CheckMenusAndSpecialHotkeys(MainWindow window)
    {
        var mainMenu=Field<Menu>(window,"MainMenu");
        var file=(MenuItem)mainMenu.Items[0];var mode=Field<Button>(window,"ModeButton").ContextMenu!;
        var hotkeys=Field<CheckBox>(window,"HotkeysBox");var auto=Field<CheckBox>(window,"AutoKeysBox");
        foreach(var menu in new ItemsControl[]{file,mode,hotkeys.ContextMenu!,auto.ContextMenu!}){
            var preferences=menu.Items.OfType<MenuItem>().Single(x=>x.Uid=="Preferences");
            preferences.ApplyTemplate();preferences.Measure(new Size(400,40));preferences.Arrange(new Rect(0,0,400,40));preferences.UpdateLayout();
            var presenter=(ContentPresenter)preferences.Template.FindName("MenuIcon",preferences);
            Check(preferences.Icon is System.Windows.Shapes.Path path&&!path.Data.Bounds.IsEmpty&&ReferenceEquals(presenter.Content,preferences.Icon)&&presenter.ActualWidth>0,"Preferences has a rendered gear icon in "+(menu==file?"File":menu==mode?"play-mode":menu==hotkeys.ContextMenu?"Hotkeys":"Auto Keys")+" menu");
        }
        Check(hotkeys.ContextMenu!.Items.OfType<MenuItem>().Any(x=>x.Uid=="Pass hotkeys")&&auto.ContextMenu!.Items.OfType<MenuItem>().Any(x=>x.Uid=="Troubleshooting"),"both status icons have their functional right-click menu options");
        MouseButtonEventArgs Press(UIElement source,MouseButton button,int count){var e=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=source};typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e,count);return e;}
        foreach(var icon in new[]{hotkeys,auto}){
            var release=Press(icon,MouseButton.Left,1);Invoke(window,"StatusIcon_Released",icon,release);
            Check(release.Handled&&ReferenceEquals(Field<FrameworkElement>(window,"pendingStatusMenu"),icon)&&!icon.ContextMenu!.IsOpen,"a single left click schedules the same status menu without opening or focusing a test popup");
            typeof(MainWindow).GetField("initialized",Private)!.SetValue(window,false);
            bool previous=icon.IsChecked==true;
            try{Invoke(window,icon==hotkeys?"Hotkeys_Pressed":"AutoKeys_Pressed",icon,Press(icon,MouseButton.Left,2));
                Check(Field<DispatcherTimer?>(window,"statusMenuTimer") is null&&icon.IsChecked!=previous,"double-click cancels the pending menu and toggles the status switch");
                icon.IsChecked=previous;
            }finally{typeof(MainWindow).GetField("initialized",Private)!.SetValue(window,true);Invoke(window,"CancelStatusMenu");}
        }
        var edits=new List<string>();int editorCalls=0;
        Func<Window,string,string,IEnumerable<HotkeyAssignment>?,bool?,string?> editor=(_,_,target,_,_)=>{edits.Add(target);return ++editorCalls==1?"Ctrl+F21":"Ctrl+F22";};
        var type=typeof(MainWindow).Assembly.GetType("SoundboardLab.SettingsWindow")!;
        var preferencesWindow=(Window)Activator.CreateInstance(type,[window,Field<AudioService>(window,"audio"),Field<PreferenceStore>(window,"preferenceStore"),new Func<Task>(()=>Task.CompletedTask),editor])!;
        try{
            Invoke(preferencesWindow,"SelectPage","Hotkeys");
            var body=(FrameworkElement)preferencesWindow.Content;
            void Layout(){body.Measure(new Size(560,700));body.Arrange(new Rect(0,0,560,700));body.UpdateLayout();}
            Layout();var table=Descendants<ListView>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="SpecialHotkeyActions");
            var columns=((GridView)table.View).Columns;
            Check(columns.Count==3&&columns[0].Header as string=="Action"&&columns[1].Header as string==""&&columns[1].Width==40&&columns[2].Header as string=="Hotkey","Special Hotkeys has separate Action, aligned value and Hotkey columns like the reference");
            var plainActions=new HashSet<string>{"selectPrevious","selectNext","selectPreviousCategory","selectNextCategory","jumpBack1","jumpBack2","jumpForward1","jumpForward2","playModeSwitch","autoKeysToggle","hotkeysToggle","hotbarSelectPreviousPage","hotbarSelectNextPage"};
            foreach(var action in SpecialHotkeyActions.All){
                var glyph=new SpecialActionIcon {Action=action.Key,Width=16,Height=16};glyph.Measure(new Size(16,16));glyph.Arrange(new Rect(0,0,16,16));glyph.UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(16,16,96,96,PixelFormats.Pbgra32);bitmap.Render(glyph);var pixels=new byte[1024];bitmap.CopyPixels(pixels,64,0);
                Check(plainActions.Contains(action.Key)?glyph.Visibility==Visibility.Collapsed:glyph.Visibility==Visibility.Visible&&Enumerable.Range(0,256).Any(i=>pixels[i*4+3]>0),"special-action icon renders the reference artwork/blank state for "+action.Key);
                if(action.Key is "recordBegin" or "hotkeysDisable" or "autoKeysDisable")Check(Enumerable.Range(0,256).Any(i=>pixels[i*4+2]>pixels[i*4]+40),"recording/disabled action retains its red artwork for "+action.Key);
            }
            for(int column=0;column<2;column++){
                Layout();var row=(ListViewItem)table.ItemContainerGenerator.ContainerFromIndex(column);
                var presenter=Descendants<GridViewRowPresenter>(row).Single();
                var cell=(UIElement)VisualTreeHelper.GetChild(presenter,column==0?0:2);
                var e=Press(cell,MouseButton.Left,2);cell.RaiseEvent(e);
                Check(e.Handled&&edits.Count==column+1&&edits.Last()==(column==0?"action:stop":"action:play"),"double-clicking the actual "+(column==0?"Action":"Hotkey")+" column opens Set hotkey for the clicked row");
            }
            var header=Descendants<GridViewColumnHeader>(table).First();
            Invoke(preferencesWindow,"SpecialHotkey_Pressed",table,Press(header,MouseButton.Left,2));
            Invoke(preferencesWindow,"SpecialHotkey_Pressed",table,Press(table,MouseButton.Left,2));
            Invoke(preferencesWindow,"SpecialHotkey_Pressed",table,Press(table,MouseButton.Right,2));
            Check(editorCalls==2,"header, blank-area and right-button double-clicks do not open a hotkey editor");
            var rows=Field<System.Collections.IEnumerable>(preferencesWindow,"hotkeyRows").Cast<object>().ToArray();
            string Key(object row)=>(string)row.GetType().GetProperty("Hotkey")!.GetValue(row)!;
            Check(Key(rows[0])=="Ctrl+F21"&&Key(rows[1])=="Ctrl+F22"&&Field<PreferenceStore>(window,"preferenceStore").Settings.ActionHotkeys.Count==0,"column double-click edits stay in the Preferences draft until Apply");
            Check(!preferencesWindow.IsVisible&&!preferencesWindow.IsActive,"Preferences interaction checks leave the real window unshown and inactive");
        }finally{preferencesWindow.Close();}
        var settings=Field<PreferenceStore>(window,"preferenceStore").Settings;settings.ActionHotkeys["stop"]="Ctrl+F23";
        bool allow=false;int prompts=0;
        Func<Window,string,string,IEnumerable<HotkeyAssignment>?,bool?,string?> duplicateEditor=(owner,current,target,actions,sides)=>{
            using var hooks=new GlobalHotkeyService();var assignments=actions!.ToArray();
            var editor=new HotkeyCaptureWindow("Ctrl+F23",hooks,false,key=>HotkeyAssignments.Check(assignments,target,key,false),text=>{prompts++;Check(text=="Hotkey is already in use. Overwrite?","a duplicate special key uses the original overwrite question");return allow;});
            try{return editor.ValidateSelection()?editor.Gesture:null;}finally{editor.Close();}
        };
        var duplicatePreferences=new SettingsWindow(window,Field<AudioService>(window,"audio"),Field<PreferenceStore>(window,"preferenceStore"),()=>Task.CompletedTask,duplicateEditor);
        try{
            duplicatePreferences.SelectPage("Hotkeys");var body=(FrameworkElement)duplicatePreferences.Content;body.Measure(new Size(560,700));body.Arrange(new Rect(0,0,560,700));body.UpdateLayout();
            var table=Descendants<ListView>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="SpecialHotkeyActions");table.SelectedIndex=1;
            string Key(int index)=>(string)table.Items[index].GetType().GetProperty("Hotkey")!.GetValue(table.Items[index])!;
            Invoke(duplicatePreferences,"SetSpecialHotkey",table);Check(prompts==1&&Key(0)=="Ctrl+F23"&&Key(1)=="","declining the special-action overwrite leaves both draft assignments unchanged");
            allow=true;Invoke(duplicatePreferences,"SetSpecialHotkey",table);
            Check(prompts==2&&Key(0)==""&&Key(1)=="Ctrl+F23"&&settings.ActionHotkeys["stop"]=="Ctrl+F23"&&!settings.ActionHotkeys.ContainsKey("play"),"accepting overwrite transfers the key between draft actions and preserves live bindings until Apply");
        }finally{duplicatePreferences.Close();settings.ActionHotkeys.Remove("stop");}
    }
}
