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
            try{await Run(args.Contains("--layout-only"));}catch(Exception ex){Console.Error.WriteLine(ex);exitCode=1;}
            finally{Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);}
        });
        Dispatcher.Run();return exitCode;
    }
    private static async Task Run(bool layoutOnly)
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
            if(layoutOnly){CheckHotkeyEditor(window,library,sound);CheckMenusAndSpecialHotkeys(window);CheckPreferencePages(window,temp);Console.WriteLine($"PASS {checks} headless WPF layout checks. No capture, rendering, window activation or OS input.");return;}
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
            Check(volume.IsMoveToPointEnabled&&volume.LargeChange<volume.Maximum-volume.Minimum,"volume track clicks use the pointed position and a bounded step");
            foreach(double target in new[]{.25,.5,.75}){
                volume.Value=.5;Layout();var track=(Track)volume.Template.FindName("PART_Track",volume);
                double x=track.Thumb.ActualWidth/2+(track.ActualWidth-track.Thumb.ActualWidth)*target;
                volume.Value=track.ValueFromPoint(new Point(x,track.ActualHeight/2));Layout();
                Check(Math.Abs(volume.Value-target)<.01&&Math.Abs(engine.SpeakerVolume-target)<.01,$"volume track geometry and playback gain preserve {target:P0} positions");
            }
            volume.Value=.5;Slider.IncreaseLarge.Execute(null,volume);
            Check(volume.Value>.5&&volume.Value<1,"a volume track step does not jump directly to maximum");
            volume.Value=0;
            Button FindButton(string name)=>Descendants<Button>(content).First(x=>AutomationProperties.GetName(x)==name);
            grid.SelectedItem=null;
            await (Task)Invoke(window,"HandleActionAsync","play",true)!;
            Check(engine.IsPlaying&&ReferenceEquals(grid.SelectedItem,sound),"the Start playback hotkey starts the first sound on a fresh session without a selected row");
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
            CheckPreferencePages(window,temp);
            Check(!window.IsVisible&&!window.IsActive,"playback and error checks leave the test window hidden and inactive");
        }
        finally {
            if(window is not null){await (Task)Invoke(window,"StopAsync")!;bool closed=false;window.Closed+=(_,_)=>closed=true;window.Close();await Until(()=>closed,"Hidden test window did not close");}
            Directory.Delete(temp,true);
        }
        Console.WriteLine($"PASS {checks} headless WPF control checks. No window shown, no mouse or keyboard input sent; playback remained muted.");
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
            Check(dialog.Title=="Edit hotkey"&&!Descendants<TextBox>(body).Any()&&Descendants<Button>(body).Any(x=>x.Content as string=="Remove hotkey"),"the shared editor has a capture control and explicit removal instead of an editable shortcut string");
            var capture=Descendants<Button>(body).Single(x=>AutomationProperties.GetAutomationId(x)=="HotkeyCapture");
            Invoke(dialog,"Record",new HotkeyInput(162,1,true));
            Check(!(bool)Invoke(dialog,"ValidateSelection")!&&capture.Content as string=="Ctrl","a modifier preview is visible and cannot be committed as a sound binding");
            Invoke(dialog,"Record",new HotkeyInput(75,1,true));
            Check((bool)Invoke(dialog,"ValidateSelection")!&&capture.Content as string=="Ctrl+K","a captured chord validates without typing its name");
            foreach(var key in new[]{Key.Escape,Key.Enter,Key.Tab}){
                var press=new KeyEventArgs(Keyboard.PrimaryDevice,HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                // The event goes directly to the hidden control, never to Windows.
                capture.RaiseEvent(press);string expected=key==Key.Enter?"Enter":key.ToString();
                Check(press.Handled&&capture.Content!.ToString()!.EndsWith(expected),$"the real capture control records {key} without invoking dialog navigation");
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
    private static void CheckPreferencePages(MainWindow main,string dataDirectory)
    {
        var type=typeof(MainWindow).Assembly.GetType("SoundboardLab.SettingsWindow")!;
        var dialog=(Window)Activator.CreateInstance(type,[main,Field<AudioService>(main,"audio"),Field<PreferenceStore>(main,"preferenceStore"),new Func<Task>(()=>Task.CompletedTask),null])!;
        try{
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
            for(int column=0;column<2;column++){
                Layout();var row=(ListViewItem)table.ItemContainerGenerator.ContainerFromIndex(column);
                var presenter=Descendants<GridViewRowPresenter>(row).Single();
                var cell=(UIElement)VisualTreeHelper.GetChild(presenter,column);
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
    }
}
