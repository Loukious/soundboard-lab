using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundboardLab.Audio;
using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

public partial class MainWindow : Window
{
    private readonly string[] arguments;
    private readonly string stateDirectory;
    private readonly LibraryStore library;
    private readonly PreferenceStore preferenceStore;
    private readonly AudioService audio = new();
    private readonly AudioService notifications = new();
    private readonly GlobalHotkeyService hotkeys = new();
    private readonly AutoKeyCoordinator autoKeys = new();
    private readonly RemoteControlServer remote;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim playTransition = new(1, 1);
    private ICollectionView? view;
    private string selectedCategory = "*";
    private SoundEntry? playingEntry;
    private string? playingPath;
    private bool initialized, changingTree, closing, userStopped, hadPlayback, randomPlayback, playingFromHotkey, firstSoundPlayed;
    private ApplicationVolumeDucker? ducker;
    private bool changingHotbar;
    private WasapiCapture? micMonitor;
    private readonly List<WasapiCapture> extraMicMonitors=[];
    private string? micMonitorDevice;
    private float microphonePeak;
    private readonly Queue<string> messages = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? playRequest;
    internal string StateDirectory=>stateDirectory;
    internal IEnumerable<string> CategoryNames=>library.Document.Categories.Select(x=>x.Name);
    private AppPreferences Settings => preferenceStore.Settings;
    private SoundEntry? Selected => SoundGrid.SelectedItem as SoundEntry;
    private PlaybackDestination Destination => Enum.TryParse<PlaybackDestination>((DestinationBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var mode) ? mode : PlaybackDestination.Both;

    public MainWindow(string[] args)
    {
        arguments = args;
        int pipeIndex=Array.IndexOf(args,"--pipe-name");
        remote=new(pipeIndex>=0 && pipeIndex+1<args.Length?args[pipeIndex+1]:RemoteControlServer.DefaultPipeName);
        int dataIndex = Array.IndexOf(args, "--data-dir");
        stateDirectory = dataIndex >= 0 && dataIndex + 1 < args.Length ? Path.GetFullPath(args[dataIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundboardLab");
        Directory.CreateDirectory(stateDirectory);
        library = new(stateDirectory); preferenceStore = new(stateDirectory);
        try { preferenceStore.Load();if(!Settings.RememberPlaybackState){Settings.Repeat=false;Settings.RepeatList=false;Settings.ContinuePlayback=false;Settings.DefaultPlaybackMode="Both";}if(!Settings.RememberHotkeyStatus)Settings.HotkeysEnabled=true; } catch (Exception ex) { Log("Settings could not be loaded: " + ex.Message); }
        PreferenceStore.UpgradeHotbarDefaults(Settings,library.Document.HotbarPins);
        SoundAssets.Initialize(Settings,stateDirectory);
        appliedStartup=Settings.RunOnStartup;
        appliedDucking=Settings.ReduceOtherApplications?Settings.OtherApplicationsVolume:null;
        InitializeComponent();
        CategoryTree.LayoutUpdated+=(_,_)=>AlignCategoryCountHeader();
        VolumeSliderFeedback.Attach(SpeakerSlider,value=>Settings.LinearVolumeSlider?value:value*value);
        VolumeSliderFeedback.Attach(ClipSlider,value=>Settings.LinearVolumeSlider?value:value*value/2);
        InitializeForwarding();
        Icon=System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/SoundboardLab;component/assets/app-icon.ico"));
        ConfigureColumnMenu();
        ConfigureMenus();
        Point dragOrigin = default;
        SoundGrid.PreviewMouseLeftButtonDown += (_, e) => dragOrigin = e.GetPosition(SoundGrid);
        SoundGrid.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(SoundGrid) - dragOrigin).Length > 10 && Selected is { } selected) DragDrop.DoDragDrop(SoundGrid, new DataObject("SoundEntryId", selected.Id), DragDropEffects.Move); };
        SoundGrid.AllowDrop = true;
        SoundGrid.Drop += (_, e) => { if (e.Data.GetData("SoundEntryId") is Guid id && ItemsControl.ContainerFromElement(SoundGrid, e.OriginalSource as DependencyObject) is DataGridRow row && row.Item is SoundEntry target) { var entry = library.Document.Entries.FirstOrDefault(x => x.Id == id); if (entry is not null) { ClearSoundSorting(); library.Document.Entries.Move(library.Document.Entries.IndexOf(entry), library.Document.Entries.IndexOf(target)); e.Handled = true; } } };
        Point categoryOrigin=default;
        CategoryTree.PreviewMouseLeftButtonDown+=(_,e)=>categoryOrigin=e.GetPosition(CategoryTree);
        CategoryTree.PreviewMouseMove+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed && (e.GetPosition(CategoryTree)-categoryOrigin).Length>10 && selectedCategory is not "*" and not "") DragDrop.DoDragDrop(CategoryTree,new DataObject("CategoryPath",selectedCategory),DragDropEffects.Move);};
        CategoryTree.AllowDrop=true;
        CategoryTree.Drop+=(_,e)=>Guard(()=>{var node=VisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);string target=(string?)node?.Tag??"";if(target=="*")target="";if(e.Data.GetData("CategoryPath") is string source){string next=(target.Length==0?"":target+"/")+source.Split('/').Last();library.RenameCategory(source,next);selectedCategory=next;e.Handled=true;}else if(e.Data.GetData("SoundEntryId") is Guid soundId && library.Document.Entries.FirstOrDefault(s=>s.Id==soundId) is {} moved){moved.Category=target;e.Handled=true;}RefreshLibrary();});
        view = CollectionViewSource.GetDefaultView(library.Document.Entries);
        view.Filter = FilterEntry; SoundGrid.ItemsSource = view;
        library.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshLibrary);
        library.PlaybackStatisticsChanged += (_, _) => QueuePlaybackStatisticsRefresh();
        audio.Progress += (_, progress) => Dispatcher.BeginInvoke(() => UpdatePlayback(progress));
        audio.Error += (_, error) => Dispatcher.BeginInvoke(() => { Log(error.Message); autoKeys.EndPlayback();if(error.Message.StartsWith("Audio forwarding")){forwardingError=error.Message;ForwardingStateChanged?.Invoke();} });
        audio.RecordingCompleted += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            if (audio.LastRecordingPath is { } path && File.Exists(path)) await AddGeneratedSoundAsync(path);
            Notify("record-stop");
            Log("Recording saved: " + audio.LastRecordingPath);
        });
        hotkeys.Triggered += action => Dispatcher.BeginInvoke(async () => await HandleActionAsync(action,true));
        hotkeys.Error += ex => Dispatcher.BeginInvoke(() => Log(ex.Message));
        remote.RequestReceived += json => Dispatcher.InvokeAsync(() => HandleRemoteAsync(json)).Task.Unwrap();
        statusTimer.Interval=TimeSpan.FromMilliseconds(100);statusTimer.Tick += (_, _) => {foregroundAudioTarget.Observe();if(++statusTicks%10==0){RefreshStatus();UpdateDucking();}};
        StateChanged+=MainWindow_StateChanged;
        PreviewTextInput+=(_,e)=>{if(e.OriginalSource is TextBox||string.IsNullOrWhiteSpace(e.Text))return;if(Settings.TypingPrefersTts&&OwnedWindows.OfType<SpeechWindow>().FirstOrDefault() is {} speech){speech.AppendText(e.Text);e.Handled=true;}else if(Settings.TypingBeginsSearch){SearchPanel.Visibility=Visibility.Visible;SearchBox.Focus();SearchBox.Text+=e.Text;SearchBox.CaretIndex=SearchBox.Text.Length;e.Handled=true;}};
        saveTimer.Tick += (_, _) => { try { preferenceStore.Save(); } catch (Exception ex) { Log(ex.Message); } };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            HotbarPage.ItemsSource = Enumerable.Range(1, Settings.HotbarPages).Select(p => "Page " + p); HotbarPage.SelectedIndex = 0;
            HotbarMenu.IsChecked=Settings.ShowHotbar;HotbarPanel.Visibility=Settings.ShowHotbar?Visibility.Visible:Visibility.Collapsed;
            SpeakerSlider.Value = Settings.LinearVolumeSlider?Math.Clamp(Settings.SpeakerVolume,0,1):Math.Sqrt(Math.Clamp(Settings.SpeakerVolume,0,1));
            ClipSlider.Value = Settings.LinearVolumeSlider?Math.Clamp(Settings.ClipVolume,0,2):Math.Sqrt(2*Math.Clamp(Settings.ClipVolume,0,2));
            BlockVoiceBox.IsChecked = Settings.BlockVoice; HotkeysBox.IsChecked = Settings.HotkeysEnabled; AutoKeysBox.IsChecked=Settings.AutoKeysEnabled;
            RepeatBox.IsChecked = Settings.Repeat; ContinueBox.IsChecked = Settings.ContinuePlayback;
            DestinationBox.SelectedIndex = Settings.DefaultPlaybackMode switch { "Speakers" => 1, "Microphone" => 2, _ => 0 };
            audio.SelectDevices(NullIfEmpty(Settings.CaptureDeviceId), NullIfEmpty(Settings.RenderDeviceId),Settings.CaptureDeviceIds,Settings.DeviceBoosts);
            audio.SpeakerVolume=(float)Settings.SpeakerVolume;audio.ClipVolume=(float)Settings.ClipVolume;
            audio.MicrophoneGain = (float)Math.Clamp(Settings.MicrophoneGain, 0, 4); audio.BlockVoice = Settings.BlockVoice;
            initialized = true; RefreshLibrary(); ConfigureHotkeys(); ConfigureAutoKeys();ApplyInterfaceSettings();
            CompositionTarget.Rendering += RenderPlaybackCursor;
            if (Settings.RemoteEnabled && !arguments.Contains("--smoke-test"))try{remote.Start();}catch(IOException ex){Log("Remote control could not start: "+ex.Message);}
            statusTimer.Start(); saveTimer.Start();
            Log("Ready. Add sounds or drag a folder into the library.");
            if(Settings.CheckDevicesOnStartup&&!arguments.Contains("--smoke-test")){var status=audio.GetInjectionStatus();if(!status.Connected)Log("Recording device check: "+status.Message);}
            string[] paths = arguments.Where(File.Exists).ToArray();
            if (paths.Length > 0) await AddPathsAsync(paths);
            if (arguments.Contains("--smoke-test")) await RunSmokeTestAsync();
            else if(Settings.StartMinimized)MinimizeWindow();
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private bool FilterEntry(object item)
    {
        if (item is not SoundEntry entry) return false;
        bool category = selectedCategory == "*" || entry.Category == selectedCategory || entry.Category.StartsWith(selectedCategory + "/", StringComparison.OrdinalIgnoreCase);
        return category;
    }
    private void RefreshLibrary()
    {
        if (!initialized || closing) return;
        if(selectedCategory is not ("*" or "")&&!library.Document.Categories.Any(x=>x.Name==selectedCategory))selectedCategory="*";
        RefreshSoundSorting();view?.Refresh();
        UpdateSearchResults();
        Title = "Soundboard Lab · " + (Path.GetFileName(library.CurrentPath) ?? "New library") + (library.HasUnsavedChanges ? " *" : "");
        LibraryCount.Text = $"{library.Document.Entries.Count} sounds · {library.Document.Entries.Sum(x => x.PlayCount)} plays";
        RecentLibraries.IsEnabled=Settings.RecentLists.Any(File.Exists);RecentLibraries.Items.Clear(); foreach (string path in Settings.RecentLists.Where(File.Exists)) { var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path }; item.Click += (_, _) => Guard(() => library.Load(path)); RecentLibraries.Items.Add(item); }
        RefreshCategories(); RefreshHotbar(); ConfigureHotkeys(); UpdateCategoryInformation();
    }
    private bool playbackStatisticsRefreshQueued;
    private void QueuePlaybackStatisticsRefresh()
    {
        if(playbackStatisticsRefreshQueued||closing)return;
        playbackStatisticsRefreshQueued=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{
            playbackStatisticsRefreshQueued=false;if(closing)return;
            Title="Soundboard Lab · "+(Path.GetFileName(library.CurrentPath)??"New library")+(library.HasUnsavedChanges?" *":"");
            LibraryCount.Text=$"{library.Document.Entries.Count} sounds · {library.Document.Entries.Sum(x=>x.PlayCount)} plays";
            var sorted=SoundGrid.Columns.OfType<DataGridBoundColumn>().FirstOrDefault(x=>x.SortDirection.HasValue);
            if(sorted?.Binding is Binding binding&&binding.Path?.Path is nameof(SoundEntry.PlayCount) or nameof(SoundEntry.LastPlayed)){RefreshSoundSorting();ConfigureHotkeys();}
            UpdateCategoryInformation();
        });
    }
    private void RefreshCategories()
    {
        changingTree = true;
        CategoryTree.Items.Clear();
        var all = new TreeViewItem { Header = CategoryHeader(library.Document.RootCategory.Name,library.Document.Entries.Count,true), Tag = "*", IsExpanded = true };
        CategoryTree.Items.Add(all);
        var nodes = new Dictionary<string, TreeViewItem>();
        foreach (var category in OrderedCategories())
        {
            var node = new TreeViewItem { Header = CategoryHeader(category.Name.Split('/').Last(),library.Document.Entries.Count(x=>x.Category==category.Name||x.Category.StartsWith(category.Name+"/",StringComparison.Ordinal)),false,category), Tag = category.Name, IsExpanded = true };
            nodes[category.Name] = node;
            if (nodes.TryGetValue(category.Parent, out var parent)) parent.Items.Add(node); else CategoryTree.Items.Add(node);
            if (selectedCategory == category.Name) node.IsSelected = true;
        }
        if (selectedCategory == "*") all.IsSelected = true;
        if (selectedCategory == "") all.IsSelected = true;
        changingTree = false;
    }
    private void RefreshHotbar()
    {
        if (HotbarGrid is null || changingHotbar) return;
        changingHotbar=true;
        try {
        int page = Math.Max(0, HotbarPage.SelectedIndex);
        int count=Math.Max(Settings.HotbarPages,library.Document.HotbarPins.Select(p=>p.Page+1).DefaultIfEmpty(1).Max());
        if(HotbarPage.Items.Count!=count) { HotbarPage.ItemsSource=Enumerable.Range(1,count).Select(p=>"Page "+p); HotbarPage.SelectedIndex=Math.Min(page,count-1); }
        RefreshPageButtons(count,page);
        HotbarGrid.Children.Clear(); HotbarGrid.Rows=Settings.HotbarRows;HotbarGrid.Columns=Settings.HotbarColumns;HotbarGrid.Height=Settings.HotbarRows*64;UpdateHotbarLock();
        for (int slot = 0; slot < Settings.HotbarRows*Settings.HotbarColumns; slot++)
        {
            var pin=library.Document.HotbarPins.FirstOrDefault(p=>p.Page==page && p.Slot==slot);
            var entry = pin is null ? null : library.Document.Entries.FirstOrDefault(x=>x.Id==pin.SoundId);
            Settings.HotbarHotkeys.TryGetValue(slot,out var gesture);
            var tile = new Grid();
            tile.Children.Add(new TextBlock { Text = entry?.Title ?? "", TextTrimming=TextTrimming.CharacterEllipsis, HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new(3,0,3,8) });
            tile.Children.Add(new TextBlock { Text=entry is null?"":gesture??"", FontSize=10,FontStyle=FontStyles.Italic,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Margin=new(3,0,3,1) });
            var button = new Button { Content = tile, HorizontalContentAlignment=HorizontalAlignment.Stretch, VerticalContentAlignment=VerticalAlignment.Stretch, ToolTip = entry is null ? "Drop a sound here, or select a sound and click to pin it." : entry.Title+"\n"+gesture, Tag = entry, Margin = new(0), Padding=new(2), BorderThickness=new(1),BorderBrush=(Brush)FindResource("Palette_292929"), MinHeight = 64, Background=entry is null?(Brush)FindResource("Palette_252525"):new SolidColorBrush(Color.FromRgb(63,164,206)),Foreground=entry is null?(Brush)FindResource("Muted"):Brushes.Black };
            if (pin is not null && pin.Color is not "#334155") { try { button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pin.Color)); button.Foreground=Brushes.White; } catch { } }
            int selectedSlot = slot;
            var context = new ContextMenu();
            var assign = new MenuItem { Header = "Set hotkey…" }; assign.Click += (_,_)=> { if(EditHotkey(this,gesture??"","hotbar:"+selectedSlot) is {} value) Guard(()=>AssignHotkey("hotbar:"+selectedSlot,value)); };context.Items.Add(assign);
            if(entry is not null) { var color=new MenuItem {Header="Color…"};color.Click+=(_,_)=>{SoundGrid.SelectedItem=entry;SetHotbarSoundColor(entry);};context.Items.Add(color); var remove=new MenuItem {Header="Remove from hotbar"};remove.Click+=(_,_)=>{if(pin is not null)library.Document.HotbarPins.Remove(pin);};context.Items.Add(remove); }
            button.ContextMenu=context;
            button.Click += async (_, _) => { if (entry is not null) {var previousSelection=Selected;await PlayEntryAsync(entry);if(!Settings.SelectHotbarSound)SoundGrid.SelectedItem=previousSelection;} };
            button.AllowDrop = true;
            button.Drop += (_, e) => { if(lockHotbar)return; if (e.Data.GetData("SoundEntryId") is Guid id && library.Document.Entries.FirstOrDefault(x => x.Id == id) is { } moving) { library.Pin(moving,page,selectedSlot);e.Handled=true; } else if(e.Data.GetData("HotbarPinId") is Guid pinId && library.Document.HotbarPins.FirstOrDefault(p=>p.Id==pinId) is {} movingPin) { library.MovePin(movingPin,page,selectedSlot);e.Handled=true; } };
            Point origin=default; button.PreviewMouseLeftButtonDown+=(_,e)=>origin=e.GetPosition(button);
            button.PreviewMouseMove+=(_,e)=>{if(!lockHotbar && pin is not null && e.LeftButton==MouseButtonState.Pressed && (e.GetPosition(button)-origin).Length>8) DragDrop.DoDragDrop(button,new DataObject("HotbarPinId",pin.Id),DragDropEffects.Move);};
            HotbarGrid.Children.Add(button);
        }
        } finally {changingHotbar=false;}
    }
    private void CategoryTree_Changed(object sender, RoutedPropertyChangedEventArgs<object> e) { if (!changingTree && e.NewValue is TreeViewItem item) { selectedCategory = (string)item.Tag; view?.Refresh();UpdateSearchResults(); UpdateCategoryInformation(); } }
    private void Search_Changed(object sender, TextChangedEventArgs e) { UpdateSearchResults(); }
    private void HotbarPage_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) {RefreshHotbar();Notify("page-"+(HotbarPage.SelectedIndex+1));} }
    private void SoundGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCategoryInformation();
    private async void SoundGrid_PlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if(PaintSoundRow(e))return;
        // WPF's MouseDoubleClick only fires for count == 2. A rapid burst keeps
        // counting 3, 4, 5...; use each even press to match native click pairs.
        if(e.ChangedButton!=MouseButton.Left||e.ClickCount<2||(e.ClickCount&1)!=0)return;
        if(VisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is not {} cell||cell.Column.SortMemberPath=="@Hotkey"||cell.DataContext is not SoundEntry entry)return;
        e.Handled=true;SoundGrid.SelectedItem=entry;
        await PlayEntryAsync(entry);
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var extensions = new HashSet<string>([".wav", ".mp3", ".flac", ".ogg", ".opus", ".aac", ".m4a", ".wma", ".aiff", ".aif", ".mp4"], StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(f => extensions.Contains(Path.GetExtension(f))));
            else if (extensions.Contains(Path.GetExtension(path))) files.Add(path);
        }
        var added = library.AddFiles(files, selectedCategory == "*" ? "" : selectedCategory);
        Log($"Added {added.Count} sounds.");
        foreach (var entry in added)
        {
            try { var analysis = await AudioFiles.AnalyzeAsync(entry.FilePath, 64, lifetime.Token); entry.DurationSeconds = analysis.Duration.TotalSeconds; }
            catch (Exception ex) { Log($"{entry.Title}: {ex.Message}"); }
            var metadata = FileMetadata.Read(entry.FilePath);entry.MetadataTitle=metadata.Title??"";entry.Artist=metadata.Artist; if (!string.IsNullOrWhiteSpace(metadata.Title)) entry.Title = metadata.Title; if (!string.IsNullOrWhiteSpace(metadata.Tags)) entry.Tags = metadata.Tags;
        }
    }
    private async Task EnsureMicrophoneActiveAsync()
    {
        string? desired = NullIfEmpty(Settings.CaptureDeviceId);
        if (micMonitor is not null && micMonitorDevice == desired) return;
        StopMicMonitor();
        using var enumerator = new MMDeviceEnumerator();
        using var device = desired is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : enumerator.GetDevice(desired);
        micMonitor = new WasapiCapture(device, true, 30); micMonitorDevice = desired;
        micMonitor.DataAvailable += (_, data) =>
        {
            if (micMonitor?.WaveFormat.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible && micMonitor.WaveFormat.BitsPerSample == 32)
            { float peak = 0; for (int i = 0; i + 4 <= data.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(data.Buffer, i))); microphonePeak = peak; }
        };
        micMonitor.StartRecording();
        foreach(string id in Settings.CaptureDeviceIds.Where(x=>x!=device.ID).Distinct()){
            using var extraDevice=enumerator.GetDevice(id);var extra=new WasapiCapture(extraDevice,true,30);extraMicMonitors.Add(extra);extra.StartRecording();
        }
        for (int i = 0; i < 20 && !audio.GetInjectionStatus().Connected; i++) await Task.Delay(50);
    }
    private void StopMicMonitor() {foreach(var monitor in extraMicMonitors){monitor.StopRecording();monitor.Dispose();}extraMicMonitors.Clear();microphonePeak=0; if (micMonitor is null) return; micMonitor.StopRecording(); micMonitor.Dispose(); micMonitor = null; micMonitorDevice = null; }
    private async Task<bool> PlayEntryAsync(SoundEntry entry, PlaybackDestination? forced = null, bool fromHotkey=false)
    {
        var target=forced??Destination;
        var routing=new PlaybackRoutingOptions(Settings.KaraokeDelayMs,Settings.VoiceActivationEnabled?Settings.VoiceActivationPath:"",Settings.Normalize?Settings.TargetLufs:null,Settings.FixedAdjustmentDb);
        if(!lifetime.IsCancellationRequested&&playRequest is null&&playingEntry?.Id==entry.Id&&lastDestination==target&&playingFromHotkey==fromHotkey
            &&string.Equals(playingPath,entry.FilePath,StringComparison.OrdinalIgnoreCase)&&audio.RoutingOptions==routing
            &&(target==PlaybackDestination.Speakers||Settings.PlaybackDelayMs==0)&&audio.TryReplay()){
            // Replaying the active sound is the seek bar's Seek(0), without
            // entering the new-playback, device, decoding or Auto Keys setup.
            CancelSeekGesture();userStopped=false;hadPlayback=true;StatusText.Visibility=Visibility.Collapsed;
            library.MarkPlayed(entry);
            return true;
        }
        var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken playToken=request.Token;
        var previous=Interlocked.Exchange(ref playRequest,request); previous?.Cancel();previous?.Dispose();
        bool entered=false;
        try
        {
            await playTransition.WaitAsync(playToken);entered=true;
            CancelSeekGesture();
            if(!File.Exists(entry.FilePath))throw new FileNotFoundException("Sound file not found.",entry.FilePath);
            double? resume=Settings.AllowResumeInAnotherMode&&audio.IsPaused&&playingEntry?.Id==entry.Id?audio.Position.TotalSeconds:null;userStopped = false;
            bool retrigger=audio.IsPlaying&&lastDestination==target&&playingFromHotkey==fromHotkey&&!resume.HasValue;
            if(!retrigger){await autoKeys.EndPlaybackAsync();ducker?.Dispose();ducker=null;}
            if (target != PlaybackDestination.Speakers) await EnsureMicrophoneActiveAsync();
            string path = entry.FilePath;
            audio.RoutingOptions=routing;
            playToken.ThrowIfCancellationRequested();
            if(target!=PlaybackDestination.Speakers&&Settings.PlaybackDelayMs>0){await audio.StopAsync();await Task.Delay(Settings.PlaybackDelayMs,playToken);}
            await autoKeys.BeginPlaybackAsync(playToken,fromHotkey,target!=PlaybackDestination.Speakers);
            await audio.PlayAsync(path, target, playToken);if(resume.HasValue)audio.Seek(TimeSpan.FromSeconds(resume.Value));
            playToken.ThrowIfCancellationRequested();
            entry.IsMissing=false;
            playingEntry = entry;playingPath=entry.FilePath;lastDestination=target;playingFromHotkey=fromHotkey;if(!resume.HasValue)library.MarkPlayed(entry); hadPlayback = true;UpdateDucking();
            if(!firstSoundPlayed){firstSoundPlayed=true;if(Settings.NotifyFirstSound&&target!=PlaybackDestination.Both)Notify("mode-"+target.ToString().ToLowerInvariant());}
            NowPlaying.Text = entry.Title; SoundGrid.SelectedItem = entry;StatusText.Visibility=Visibility.Collapsed;
            Log("Playing " + entry.Title);
            return true;
        }
        catch (OperationCanceledException) { if(entered)autoKeys.EndPlayback();return false; }
        catch (Exception ex) { if(entered)await audio.StopAsync();autoKeys.EndPlayback();entry.IsMissing=!File.Exists(entry.FilePath); Log(ex.Message);StatusText.Visibility=Visibility.Visible;StatusText.ToolTip=ex.Message; return false; }
        finally { if(ReferenceEquals(Interlocked.CompareExchange(ref playRequest,null,request),request))request.Dispose();if(entered)playTransition.Release(); }
    }
    private void UpdatePlayback(PlaybackProgressEventArgs progress)
    {
        if (closing || (progress.TimelineVersion >= 0 && progress.TimelineVersion != audio.TimelineVersion)) return;
        if(progress.Completed)CancelSeekGesture();
        UpdateSeekPlaybackState();
        SeekSlider.Maximum = Math.Max(.001, progress.Duration.TotalSeconds);
        TimeLabel.Text = $"{(int)progress.Position.TotalMinutes}:{progress.Position.Seconds:D2}";
        TimeLabel.ToolTip = $"{TimeLabel.Text} / {(int)progress.Duration.TotalMinutes}:{progress.Duration.Seconds:D2}";
        PauseGlyph.Data = (Geometry)FindResource(audio.IsPaused ? "PlayIcon" : "PauseIcon");
        PauseButton.ToolTip = audio.IsPaused ? "Resume" : "Pause";
        if (hadPlayback && progress.Completed)
        {
            hadPlayback = false; autoKeys.EndPlayback();ducker?.Dispose();ducker=null;
            if (!userStopped && playingEntry is not null && !(playingFromHotkey&&Settings.StopOnNewSound))
            {
                if (RepeatBox.IsChecked == true) Dispatcher.BeginInvoke(async () => await PlayEntryAsync(playingEntry,lastDestination));
                else if (ContinueBox.IsChecked == true) Dispatcher.BeginInvoke(async () => await PlayNextAsync(1, true));
            }
        }
    }
    private async Task StopAsync() { var request=Interlocked.Exchange(ref playRequest,null);request?.Cancel();request?.Dispose();userStopped = true; hadPlayback = false; await audio.StopAsync(); await autoKeys.EndPlaybackAsync();ducker?.Dispose();ducker=null; NowPlaying.Text = "Ready to play"; }
    private async Task PlayNextAsync(int direction, bool autoAdvance = false,bool fromHotkey=false)
    {
        var entries = (view?.Cast<SoundEntry>() ?? library.Document.Entries).ToArray(); if (entries.Length == 0) return;
        if (randomPlayback && direction > 0) { await PlayEntryAsync(entries[Random.Shared.Next(entries.Length)]); return; }
        int index = Array.IndexOf(entries, playingEntry ?? Selected); int next = index + direction;
        if (next < 0) next = entries.Length - 1;
        if (next >= entries.Length) { if (autoAdvance && !Settings.RepeatList) return; next = 0; }
        await PlayEntryAsync(entries[next],lastDestination,fromHotkey:fromHotkey);
    }
    private double volumeBeforeMute=.8;
    private void SelectAdjacentCategory(int direction){var names=new[]{"*",""}.Concat(library.Document.Categories.Select(x=>x.Name)).ToArray();selectedCategory=names[(Array.IndexOf(names,selectedCategory)+direction+names.Length)%names.Length];RefreshLibrary();}
    internal string ShownHotkey(SoundEntry sound){int index=Array.IndexOf(view?.Cast<SoundEntry>().ToArray()??[],sound);return Settings.SameHotkeysInAllCategories?Settings.RowHotkeys.GetValueOrDefault(index,sound.Hotkey):sound.Hotkey;}
    private async Task PlayRandomAsync(bool fromHotkey=false) { var entries = view?.Cast<SoundEntry>().ToArray() ?? []; if (entries.Length > 0) { randomPlayback = true; await PlayEntryAsync(entries[Random.Shared.Next(entries.Length)],fromHotkey:fromHotkey); } }
    private async void Play_Click(object sender, RoutedEventArgs e) { randomPlayback = false; if ((Selected??view?.Cast<SoundEntry>().FirstOrDefault()) is { } entry){SoundGrid.SelectedItem=entry;await PlayEntryAsync(entry);} }
    private async void Speakers_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry) await PlayEntryAsync(entry, PlaybackDestination.Speakers); }
    private async void Microphone_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry) await PlayEntryAsync(entry, PlaybackDestination.Microphone); }
    private async Task TogglePauseAsync() { if (audio.IsPaused) { await autoKeys.BeginPlaybackAsync(lifetime.Token,playingFromHotkey,lastDestination!=PlaybackDestination.Speakers); audio.Resume(); } else { audio.Pause(); await autoKeys.EndPlaybackAsync(); } UpdateDucking(); }
    private async void Pause_Click(object sender, RoutedEventArgs e) => await TogglePauseAsync();
    private async void Stop_Click(object sender, RoutedEventArgs e) {await StopForwardingAsync();await StopAsync();}
    private async void Next_Click(object sender, RoutedEventArgs e) => await PlayNextAsync(1);
    private async void Previous_Click(object sender, RoutedEventArgs e) => await PlayNextAsync(-1);
    private async void Random_Click(object sender, RoutedEventArgs e) => await PlayRandomAsync();
    private void Seek_Released(object sender, MouseButtonEventArgs e)
    {
        if(!seekingWithMouse)return;
        if(audio.IsPlaying||audio.IsPaused)SeekToPointer(e,true);
        seekingWithMouse=false;SeekSlider.ReleaseMouseCapture();e.Handled=true;
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!initialized) return;Settings.SpeakerVolume=Settings.LinearVolumeSlider?SpeakerSlider.Value:SpeakerSlider.Value*SpeakerSlider.Value;Settings.ClipVolume=Settings.LinearVolumeSlider?ClipSlider.Value:ClipSlider.Value*ClipSlider.Value/2;audio.SpeakerVolume=(float)Settings.SpeakerVolume;audio.ClipVolume=(float)Settings.ClipVolume;}
    private void PlaybackOptions_Changed(object sender, RoutedEventArgs e) { if (initialized) { Settings.Repeat = RepeatBox.IsChecked == true; Settings.ContinuePlayback = ContinueBox.IsChecked == true; } }
    private void Destination_Changed(object sender, SelectionChangedEventArgs e) { if(ModeButton is not null){ModeButton.ToolTip="Play mode: "+Destination+". Click to choose.";if(PlayModeGlyph is not null)PlayModeGlyph.Data=(Geometry)FindResource(Destination==PlaybackDestination.Speakers?"HeadphonesIcon":Destination==PlaybackDestination.Microphone?"MicIcon":"PlayIcon");} if (initialized) {Settings.DefaultPlaybackMode = Destination.ToString();} }
    private void BlockVoice_Changed(object sender, RoutedEventArgs e) { if (initialized) audio.BlockVoice = Settings.BlockVoice = BlockVoiceBox.IsChecked == true; }
    private void Hotkeys_Changed(object sender, RoutedEventArgs e) { if (initialized) { Settings.HotkeysEnabled = HotkeysBox.IsChecked == true; ConfigureHotkeys();Notify(Settings.HotkeysEnabled?"hotkeys-on":"hotkeys-off"); } }
    private void ConfigureHotkeys()
    {
        if(!initialized)return;
        try{
            UpdateHotkeyIndicator();
            var bindings=BuildHotkeyBindings();
            hotkeys.Configure(bindings,new(){Enabled=Settings.HotkeysEnabled,AllowLeftRightModifiers=Settings.AllowLeftRightModifiers,SuppressMatchingKeys=!Settings.PassHotkeys,AllowRepeat=Settings.AllowHotkeyRepeat,AutoStopOnRelease=Settings.AutoStopOnKeyRelease,ConflictPolicy=Enum.TryParse<HotkeyConflictPolicy>(Settings.HotkeyConflictPolicy,out var policy)?policy:HotkeyConflictPolicy.Reject});
            if(Settings.HotkeysEnabled||bindings.Any(x=>SpecialHotkeyActions.NormalizeKey(x.Action) is "hotkeys-on" or "hotkeys-toggle"))hotkeys.Start();else hotkeys.Stop();
        }catch(Exception ex){hotkeys.Stop();Log("Hotkeys: "+ex.Message);}
    }
    private void ConfigureAutoKeys() {autoKeys.Rules=Settings.AutoKeys;autoKeys.Options = new() { Enabled = Settings.AutoKeysEnabled, Gesture = Settings.AutoKeyGesture, Processes = Settings.AutoKeyProcesses, LeadDelayMs = Settings.AutoKeyDelayMs, TailDelayMs = Settings.PushToTalkTailMs };}
    private async Task HandleActionAsync(string action,bool fromHotkey=false)
    {
        string originalAction=action;action=SpecialHotkeyActions.NormalizeKey(action);
        double Value()=>Settings.ActionValues.GetValueOrDefault(originalAction,Settings.ActionValues.GetValueOrDefault(action,SpecialHotkeyActions.All.FirstOrDefault(x=>x.Key==originalAction||SpecialHotkeyActions.NormalizeKey(x.Key)==action)?.DefaultValue??0));
        switch(action){
            case "forward-toggle":await ToggleForwardingAsync();return;
            case "forward-push":await HoldForwardingAsync("hotkey",true);return;
            case "forward-release":await HoldForwardingAsync("hotkey",false);return;
            case "current":if(playingEntry is {} current)await PlayEntryAsync(current,fromHotkey:fromHotkey);return;
            case "previous-played":var played=library.Document.Entries.Where(x=>x.LastPlayed.HasValue&&x.Id!=playingEntry?.Id).OrderByDescending(x=>x.LastPlayed).FirstOrDefault();if(played is not null)await PlayEntryAsync(played,fromHotkey:fromHotkey);return;
            case "play-selected":if(Selected is {} selectedSound)await PlayEntryAsync(selectedSound,fromHotkey:fromHotkey);return;
            case "random-all":var all=library.Document.Entries;if(all.Count>0)await PlayEntryAsync(all[Random.Shared.Next(all.Count)],fromHotkey:fromHotkey);return;
            case "category-previous":SelectAdjacentCategory(-1);return;case "category-next":SelectAdjacentCategory(1);return;
            case "record-speakers":await GuardAsync(()=>StartRecordingAsync(RecordingSource.Desktop));return;case "record-microphone":await GuardAsync(()=>StartRecordingAsync(RecordingSource.Microphone));return;
            case "record-start":await GuardAsync(()=>StartRecordingAsync(RecordingSource.Desktop));return;case "record-stop":await audio.StopRecordingAsync();return;
            case "jump-back-2":case "jump-back":audio.Seek(audio.Position-TimeSpan.FromSeconds(Value()));return;case "jump-forward-2":case "jump-forward":audio.Seek(audio.Position+TimeSpan.FromSeconds(Value()));return;
            case "volume-up-2":case "volume-up":SpeakerSlider.Value=Math.Clamp(SpeakerSlider.Value+Value()/100,0,1);return;case "volume-down-2":case "volume-down":SpeakerSlider.Value=Math.Clamp(SpeakerSlider.Value-Value()/100,0,1);return;case "volume-set-2":case "volume-set":SpeakerSlider.Value=Math.Clamp(Value()/100,0,1);return;
            case "volume-mute":if(SpeakerSlider.Value>0){volumeBeforeMute=SpeakerSlider.Value;SpeakerSlider.Value=0;}else SpeakerSlider.Value=volumeBeforeMute;return;
            case "mode-both":DestinationBox.SelectedIndex=0;if(fromHotkey)Notify(action);return;case "mode-speakers":DestinationBox.SelectedIndex=1;if(fromHotkey)Notify(action);return;case "mode-microphone":DestinationBox.SelectedIndex=2;if(fromHotkey)Notify(action);return;
            case "auto-on":AutoKeysBox.IsChecked=true;return;case "auto-off":AutoKeysBox.IsChecked=false;return;case "auto-toggle":AutoKeysBox.IsChecked=AutoKeysBox.IsChecked!=true;return;
            case "hotkeys-on":HotkeysBox.IsChecked=true;return;case "hotkeys-off":HotkeysBox.IsChecked=false;return;case "hotkeys-toggle":HotkeysBox.IsChecked=HotkeysBox.IsChecked!=true;return;
            case "page-previous":HotbarPage.SelectedIndex=(HotbarPage.SelectedIndex+HotbarPage.Items.Count-1)%HotbarPage.Items.Count;return;case "page-next":HotbarPage.SelectedIndex=(HotbarPage.SelectedIndex+1)%HotbarPage.Items.Count;return;
            case "tts-speakers":case "tts-microphone":case "tts-play":var speech=OwnedWindows.OfType<SpeechWindow>().FirstOrDefault();if(speech is not null)await PlayTransientAsync(await speech.GenerateAsync(),action=="tts-speakers"?PlaybackDestination.Speakers:action=="tts-microphone"?PlaybackDestination.Microphone:Destination,fromHotkey);return;
        }

        if(action.StartsWith("row:")&&int.TryParse(action[4..],out int row)){var rowSound=view?.Cast<SoundEntry>().ElementAtOrDefault(row);if(rowSound is not null)await PlayEntryAsync(rowSound,fromHotkey:fromHotkey);return;}
        if (action.StartsWith("hotbar:") && int.TryParse(action[7..], out var slot)) { var pin=library.Document.HotbarPins.FirstOrDefault(x=>x.Page==Math.Max(0,HotbarPage.SelectedIndex) && x.Slot==slot); var pinned=library.Document.Entries.FirstOrDefault(x=>x.Id==pin?.SoundId); if(pinned is not null) await PlayEntryAsync(pinned,fromHotkey:fromHotkey); return; }
        if (action.StartsWith("random-category:")) { string category = action[16..]; var group = library.Document.Entries.Where(x => x.Category == category || x.Category.StartsWith(category + "/", StringComparison.Ordinal)).ToArray(); if (group.Length > 0) await PlayEntryAsync(group[Random.Shared.Next(group.Length)],fromHotkey:fromHotkey); return; }
        if (action.StartsWith("play:") && Guid.TryParse(action[5..], out var id) && library.Document.Entries.FirstOrDefault(x => x.Id == id) is { } entry) { await PlayEntryAsync(entry,fromHotkey:fromHotkey); return; }
        switch (action) { case "start": await StartPlaybackAsync(fromHotkey);break;case "select-previous": SelectAdjacent(-1);break;case "select-next": SelectAdjacent(1);break;case "stop":await StopForwardingAsync(); await StopAsync(); break; case "pause": await TogglePauseAsync(); break; case "next": await PlayNextAsync(1,fromHotkey:fromHotkey); break; case "previous": await PlayNextAsync(-1,fromHotkey:fromHotkey); break; case "random": await PlayRandomAsync(fromHotkey); break; case "record": Recorder_Click(this, new()); break; case "cycle-mode": SwitchPlayMode(fromHotkey); break; }
    }

    private void NewList_Click(object sender, RoutedEventArgs e) { library.New(); playingEntry = null; selectedCategory = "*"; RefreshLibrary(); }
    private void OpenList_Click(object sender, RoutedEventArgs e) => Guard(() => { var dialog = new OpenFileDialog { Filter = "Sound lists (*.spl)|*.spl|Sound library (*.json)|*.json|All files|*.*", FilterIndex = 1, DefaultExt = ".spl" }; if (dialog.ShowDialog(this) == true) { library.Load(dialog.FileName); AddRecent(dialog.FileName); RefreshLibrary(); } });
    private void SaveList_Click(object sender, RoutedEventArgs e) => Guard(() => { library.Save(); if (library.CurrentPath is { } path) AddRecent(path); Log("Library saved."); });
    private void SaveListAs_Click(object sender, RoutedEventArgs e) => Guard(() => { var dialog = new SaveFileDialog { Filter = "Sound library (*.json)|*.json", FileName = "library.json" }; if (dialog.ShowDialog(this) == true) { library.Save(dialog.FileName); AddRecent(dialog.FileName); } });
    private void AddRecent(string path) { Settings.RecentLists.Remove(path); Settings.RecentLists.Insert(0, path); Settings.RecentLists = Settings.RecentLists.Take(10).ToList(); preferenceStore.Save(); }
    private async void AddFiles_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Multiselect = true, Filter = "Audio files|*.wav;*.mp3;*.flac;*.ogg;*.opus;*.aac;*.m4a;*.wma;*.aif;*.aiff;*.mp4|All files|*.*" }; if (dialog.ShowDialog(this) == true) await GuardAsync(() => AddPathsAsync(dialog.FileNames)); }
    private async void AddFolder_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) await GuardAsync(() => AddPathsAsync([dialog.FolderName])); }
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await GuardAsync(() => AddPathsAsync(paths)); }
    private void ImportSoundpad_Click(object sender, RoutedEventArgs e) => Guard(() => { var dialog = new OpenFileDialog { Filter = "Soundpad lists|*.spl" }; if (dialog.ShowDialog(this) == true) library.ImportSoundpad(dialog.FileName); });
    private void Export_Click(object sender, RoutedEventArgs e) => Guard(() => { var dialog = new OpenFolderDialog { Title = "Export library and audio files" }; if (dialog.ShowDialog(this) == true) { library.Export(dialog.FolderName); preferenceStore.Export(Path.Combine(dialog.FolderName, "preferences.json")); Log("Portable library exported."); } });
    private void RestoreBackup_Click(object sender, RoutedEventArgs e) => Guard(() => { var dialog = new OpenFileDialog { InitialDirectory = Path.Combine(stateDirectory, "backups"), Filter = "Library backups|*.json" }; if (dialog.ShowDialog(this) == true) library.Load(dialog.FileName); });
    private void Undo_Click(object sender, RoutedEventArgs e) => library.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => library.Redo();
    private void Rename_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry && Prompt("Rename sound", "Display title", entry.Title) is { } title) Guard(() => library.Rename(entry, title)); }
    private void RenameFile_Click(object sender,RoutedEventArgs e) { if(Selected is {} entry && Prompt("Rename audio file","File name or full destination path",Path.GetFileName(entry.FilePath)) is {} value) Guard(()=>{string path=Path.IsPathRooted(value)?value:Path.Combine(Path.GetDirectoryName(entry.FilePath)!,value);if(Path.GetExtension(path).Length==0)path+=Path.GetExtension(entry.FilePath);library.RenameFile(entry,path);}); }
    private void Tags_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry && Prompt("Sound tags", "Tags", entry.Tags) is { } tags) entry.Tags = tags; }
    private void MoveCategory_Click(object sender, RoutedEventArgs e) { if (Prompt("Move sounds", "Category path (use / for nested categories)", Selected?.Category ?? "") is { } category) Guard(() => { library.AddCategory(category); foreach (var entry in SoundGrid.SelectedItems.Cast<SoundEntry>().ToArray()) entry.Category = category; }); }
    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not {} entry) return;
        string target=SoundHotkeyTarget(entry);
        if(EditHotkey(this,ShownHotkey(entry),target) is {} value) Guard(()=>AssignHotkey(target,value));
    }
    private void CategoryHotkey_Click(object sender, RoutedEventArgs e)
    {
        string target="category:"+(selectedCategory is "*" or ""?"*":selectedCategory);
        if(EditHotkey(this,CurrentCategory.Hotkey,target) is {} value) Guard(()=>AssignHotkey(target,value));
    }
    private void Remove_Click(object sender, RoutedEventArgs e) => library.Remove(SoundGrid.SelectedItems.Cast<SoundEntry>().ToArray());
    private void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var entries = SoundGrid.SelectedItems.Cast<SoundEntry>().ToArray(); if (entries.Length == 0) return;
        if (MessageBox.Show(this, $"Move {entries.Length} audio files to the Recycle Bin?", "Recycle audio files", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Guard(() => { foreach (var entry in entries) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(entry.FilePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); library.Remove(entries); });
    }
    private void Explorer_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry) Guard(() => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + entry.FilePath + "\"", UseShellExecute = true })); }
    private void AddCategory_Click(object sender, RoutedEventArgs e) { if (Prompt("New category", "Category path", selectedCategory is "*" or "" ? "" : selectedCategory + "/") is { } value) Guard(() => library.AddCategory(value)); }
    private void RenameCategory_Click(object sender,RoutedEventArgs e)=>EditCategory_Click(sender,e);
    private void DeleteCategory_Click(object sender, RoutedEventArgs e) { if (selectedCategory is not "*" and not "") { library.RemoveCategory(selectedCategory); selectedCategory = "*"; RefreshLibrary(); } }
    private void CategoryUp_Click(object sender, RoutedEventArgs e) => MoveCategoryOrder(-1);
    private void CategoryDown_Click(object sender, RoutedEventArgs e) => MoveCategoryOrder(1);
    private void MoveCategoryOrder(int direction) { int index = library.Document.Categories.ToList().FindIndex(x => x.Name == selectedCategory); if (index >= 0) library.Document.Categories.Move(index, Math.Clamp(index + direction, 0, library.Document.Categories.Count - 1)); }
    private void Pin_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry) { int page = Math.Max(0, HotbarPage.SelectedIndex); int slot = Enumerable.Range(0, Settings.HotbarRows*Settings.HotbarColumns).FirstOrDefault(i => !library.Document.HotbarPins.Any(x => x.Page == page && x.Slot == i), -1); if (slot < 0) { Log("This hotbar page is full."); return; } library.Pin(entry,page,slot); } }
    private void Unpin_Click(object sender, RoutedEventArgs e) { if (Selected is { } entry) library.Unpin(entry,Math.Max(0,HotbarPage.SelectedIndex)); }
    private void Color_Click(object sender, RoutedEventArgs e) => OpenSoundColorTool();
    private void SetHotbarSoundColor(SoundEntry entry) { if (Prompt("Hotbar color", "Hex color", entry.Color) is { } value) Guard(() => { _ = ColorConverter.ConvertFromString(value); entry.Color = value; var selectedPins=library.Document.HotbarPins.Where(p=>p.SoundId==entry.Id && p.Page==Math.Max(0,HotbarPage.SelectedIndex)).ToArray(); foreach(var pin in selectedPins) foreach(var other in library.Document.HotbarPins.Where(p=>p.Id==pin.Id || (Settings.SameHotbarColors && p.Slot==pin.Slot))) other.Color=value; }); }

    private async void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Prompt("Import audio from URL", "Direct HTTP or HTTPS audio URL", "https://") is not { } value) return;
        await GuardAsync(async () => { string file = await DownloadAudioAsync(value); await AddPathsAsync([file]); });
    }
    internal async Task<string> DownloadAudioAsync(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https") throw new ArgumentException("Use a direct HTTP or HTTPS audio URL.");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 100 * 1024 * 1024) throw new InvalidDataException("The download exceeds 100 MB.");
        string name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Path.GetFileName(uri.LocalPath);
        name = Path.GetFileName(name); if (string.IsNullOrWhiteSpace(name)) name = "download.wav";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        string folder = Path.Combine(stateDirectory, "downloads"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + "-" + name); string partial = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".partial" + Path.GetExtension(name));
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token); await using (var output = File.Create(partial))
            { var buffer = new byte[65536]; long total = 0; int read; while ((read = await input.ReadAsync(buffer, lifetime.Token)) > 0) { total += read; if (total > 100 * 1024 * 1024) throw new InvalidDataException("The download exceeds 100 MB."); await output.WriteAsync(buffer.AsMemory(0, read), lifetime.Token); } }
            await AudioFiles.AnalyzeAsync(partial, 32, lifetime.Token); File.Move(partial, path); return path;
        }
        catch { if (File.Exists(partial)) File.Delete(partial); throw; }
    }

    private void Recorder_Click(object sender, RoutedEventArgs e) => new RecorderWindow(audio, Settings, stateDirectory, Notify,async(source,seconds)=>await StartRecordingAsync(source,seconds)) { Owner = this }.Show();
    private void Editor_Click(object sender, RoutedEventArgs e) { if (Selected is { } external && Settings.UseExternalEditor) { Guard(()=>LaunchExternalEditor(external.FilePath)); return; } if (Selected is { } entry) new EditorWindow(entry.FilePath, async path => {await AddPathsAsync([path]);if(string.Equals(path,entry.FilePath,StringComparison.OrdinalIgnoreCase)){entry.DurationSeconds=(await AudioFiles.AnalyzeAsync(path)).Duration.TotalSeconds;RefreshLibrary();}}, Settings) { Owner = this }.Show(); }
    private void Tts_Click(object sender, RoutedEventArgs e) => new SpeechWindow(AddGeneratedSoundAsync, stateDirectory, Settings) { Owner = this }.Show();
    private async Task ApplySettingsAsync() { await StopForwardingAsync();await StopAsync(); StopMicMonitor(); audio.SelectDevices(NullIfEmpty(Settings.CaptureDeviceId),NullIfEmpty(Settings.RenderDeviceId),Settings.CaptureDeviceIds,Settings.DeviceBoosts); audio.MicrophoneGain=(float)Settings.MicrophoneGain;audio.ClipVolume=(float)Settings.ClipVolume;DestinationBox.SelectedIndex=Settings.DefaultPlaybackMode switch {"Speakers"=>1,"Microphone"=>2,_=>0}; ConfigureAutoKeys();AutoKeysBox.IsChecked=Settings.AutoKeysEnabled;double speakers=Settings.SpeakerVolume,clips=Settings.ClipVolume;SpeakerSlider.Value=Settings.LinearVolumeSlider?speakers:Math.Sqrt(speakers);ClipSlider.Value=Settings.LinearVolumeSlider?clips:Math.Sqrt(2*clips);HotkeysBox.IsChecked=Settings.HotkeysEnabled;BlockVoiceBox.IsChecked=Settings.BlockVoice;ConfigureHotkeys();RefreshLibrary();ApplyInterfaceSettings();ApplyStartupSetting(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(this,audio,preferenceStore,ApplySettingsAsync){Owner=this}.ShowDialog();
    private void HotkeyOverview_Click(object sender,RoutedEventArgs e)=>ShowText("Hotkey overview",string.Join("\n",hotkeys.Bindings.OrderBy(x=>x.Gesture).Select(x=>x.Gesture+" → "+x.Action)));
    private void MicrophoneSetup_Click(object sender, RoutedEventArgs e) => new MicrophoneSetupWindow(audio, Settings, async () => { StopMicMonitor(); await EnsureMicrophoneActiveAsync(); RefreshStatus(); }) { Owner = this }.ShowDialog();
    internal void OpenMicrophoneSetup()=>MicrophoneSetup_Click(this,new());
    private void MeasureVoice_Click(object sender,RoutedEventArgs e)
    {
        var preferences=new SettingsWindow(this,audio,preferenceStore,ApplySettingsAsync){Owner=this};
        preferences.Loaded+=(_,_)=>preferences.BeginVoiceMeasurement();preferences.ShowDialog();
    }
    private void UpdateDucking()
    {
        if(!audio.IsPlaying||!Settings.ReduceOtherApplications){ducker?.Dispose();ducker=null;return;}
        try{ducker??=new();ducker.Update(Settings.OtherApplicationsVolume.StartsWith("Mute")?0:Settings.OtherApplicationsVolume.Contains("80%")?.2f:.5f);}catch(Exception ex){ducker?.Dispose();ducker=null;Log("Application volume: "+ex.Message);}
    }
    private async void Notify(string id)=>await PlayNotificationAsync(id);
    private async Task PlayNotificationAsync(string id,bool wait=false)
    {
        if(closing||!Settings.Notifications.TryGetValue(id,out var setting)||!setting.Enabled||string.IsNullOrWhiteSpace(setting.Path))return;
        try{notifications.SpeakerVolume=(float)Settings.SpeakerVolume;await notifications.PlayAsync(setting.Path,PlaybackDestination.Speakers);if(wait)while(notifications.IsPlaying)await Task.Delay(15);}catch(Exception ex){Log("Notification: "+ex.Message);}
    }
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => Guard(() => { var dialog = new SaveFileDialog { FileName = "soundboard-diagnostics.json", Filter = "Diagnostic report|*.json" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Version = AppVersion.Current, OS = Environment.OSVersion.ToString(), Devices = audio.Devices, Microphone = audio.GetInjectionStatus(), Settings, Messages = messages.ToArray() }, new JsonSerializerOptions { WriteIndented = true })); });
    private void RefreshStatus()
    {
        if(audio.IsForwarding){MicStatus.Text="Forwarding · "+audio.ForwardingSourceName;MicStatus.ToolTip="Audio forwarding is active. Stop playback or open Preferences → Audio to stop forwarding.";return;}
        try { var state = audio.GetInjectionStatus(); MicStatus.Text = state.Connected ? $"Mic ready · {microphonePeak:P0}" : "Mic mixing needs setup"; MicStatus.ToolTip = state.Message; }
        catch (Exception ex) { MicStatus.Text = "Microphone unavailable"; MicStatus.ToolTip = ex.Message; }
    }
    private async Task<string> HandleRemoteAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement; string command = (root.TryGetProperty("command", out var cmd) ? cmd : root.GetProperty("action")).GetString() ?? "";
            switch (command.ToLowerInvariant())
            {
                case "play":
                    SoundEntry? entry = null;
                    if (root.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var guid)) entry = library.Document.Entries.FirstOrDefault(x => x.Id == guid);
                    if (root.TryGetProperty("index", out var index)) entry = library.Document.Entries.ElementAtOrDefault(index.GetInt32());
                    if (root.TryGetProperty("path", out var path)) { string file = Path.GetFullPath(path.GetString()!); entry = library.Document.Entries.FirstOrDefault(x => x.FilePath == file) ?? library.AddFiles([file]).FirstOrDefault(); }
                    if (entry is null) throw new ArgumentException("Sound not found.");
                    PlaybackDestination? mode = root.TryGetProperty("mode", out var target) && Enum.TryParse<PlaybackDestination>(target.GetString(), true, out var parsed) ? parsed : null;
                    if (!await PlayEntryAsync(entry, mode)) throw new InvalidOperationException(StatusText.Text); break;
                case "seek": audio.Seek(TimeSpan.FromSeconds(root.GetProperty("seconds").GetDouble())); break;
                case "volume": SpeakerSlider.Value = root.GetProperty("value").GetDouble(); break;
                case "state": return JsonSerializer.Serialize(new { ok = true, processId=Environment.ProcessId, renderedMenuLabels=RenderedMenuLabels(),appearance=new {Theme=Settings.Theme,Zoom=Settings.Zoom,Scale=(Content as Grid)?.LayoutTransform.Value.M11,Background=Application.Current.Resources["Palette_252525"].ToString(),Sorting=SoundGrid.CanUserSortColumns},title = Title, libraryCount = library.Document.Entries.Count, entries = library.Document.Entries.Select(x => new { x.Id, x.Title, x.Category, x.PlayCount }), pins=library.Document.HotbarPins, visibleCount = view?.Cast<SoundEntry>().Count() ?? 0, hotbarCount = HotbarGrid.Children.Count, hotbarVisible=HotbarPanel.Visibility==Visibility.Visible, hotbarPage=Math.Max(0,HotbarPage.SelectedIndex),hotbarPages=HotbarPage.Items.Count,hotbarRows=HotbarGrid.Rows,hotbarColumns=HotbarGrid.Columns,hotbarLocked=lockHotbar,categoryLayout=CategoryLayout(),playMode=Destination.ToString(),columnHeaders=Descendants(SoundGrid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Where(x=>x.Column is not null).Select(x=>new{Title=x.Column!.Header,Sort=x.Column.SortDirection?.ToString(),Hovered=x.IsMouseOver,Background=(x.Template.FindName("Chrome",x) as Border)?.Background?.ToString()}),visibleEntries=view?.Cast<SoundEntry>().Select(x=>new{x.Id,x.Title,x.DurationSeconds}),categoryOrder=CategoryTree.Items.OfType<TreeViewItem>().Select(x=>x.Tag),playing = audio.IsPlaying, paused = audio.IsPaused, position = audio.Position.TotalSeconds, displayPosition=SeekSlider.Value,seekCursorVisible=(SeekSlider.Template.FindName("SeekCursor",SeekSlider) as UIElement)?.IsVisible??false,seekingWithMouse, duration = audio.Duration.TotalSeconds, recording = audio.IsRecording, microphone = audio.GetInjectionStatus(), status = StatusText.Text });
                case "add": await AddPathsAsync(root.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToArray()); break;
                case "search": SearchBox.Text = root.GetProperty("text").GetString() ?? ""; break;
                case "select": SoundGrid.SelectedItem = library.Document.Entries.ElementAtOrDefault(root.GetProperty("index").GetInt32()); break;
                case "category": selectedCategory = root.GetProperty("name").GetString() ?? "*"; if (selectedCategory is not "*" and not "") library.AddCategory(selectedCategory); RefreshLibrary(); break;
                case "move": MoveTo(root.GetProperty("index").GetInt32(), root.GetProperty("category").GetString() ?? ""); break;
                case "rename": library.Rename(library.Document.Entries[root.GetProperty("index").GetInt32()], root.GetProperty("title").GetString()!); break;
                case "add-hotbar-page": AddHotbarPage_Click(this,new());break;
                case "pin": var pin = library.Document.Entries[root.GetProperty("index").GetInt32()]; int page = root.GetProperty("page").GetInt32(), slot = root.GetProperty("slot").GetInt32(); if (page<0 || page>=HotbarPage.Items.Count || slot<0 || slot>=Settings.HotbarRows*Settings.HotbarColumns) throw new ArgumentException("Invalid hotbar page or slot."); library.Pin(pin,page,slot); break;
                case "save": library.Save(root.TryGetProperty("path", out var save) ? save.GetString() : null); break;
                case "load": library.Load(root.GetProperty("path").GetString()!); break;
                case "new": library.New(); break;
                case "undo": library.Undo(); break;
                case "redo": library.Redo(); break;
                case "export": library.Export(root.GetProperty("path").GetString()!); break;
                case "import-url": await AddPathsAsync([await DownloadAudioAsync(root.GetProperty("url").GetString()!)]); break;
                case "screenshot": await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); SaveScreenshot(root.GetProperty("path").GetString()!); break;
                case "open-tool": string tool = root.GetProperty("tool").GetString() ?? ""; _ = Dispatcher.BeginInvoke(() => { switch(tool) { case "settings": Settings_Click(this,new()); break; case "editor": Editor_Click(this,new()); break; case "speech": Tts_Click(this,new()); break; case "recorder": Recorder_Click(this,new()); break; case "microphone": MicrophoneSetup_Click(this,new()); break; } }); break;
                case "tool-state": return JsonSerializer.Serialize(new { ok = true, windows = OwnedWindows.Cast<Window>().Where(x => x.IsVisible).Select(x => new { x.Title, editor=(x as EditorWindow)?.State(), width = x.ActualWidth, height = x.ActualHeight, background = x.Background?.ToString(), textBoxes = Descendants(x).OfType<TextBox>().Count(), waveSegments = Descendants(x).OfType<System.Windows.Shapes.Line>().Count() }) });
                case "close-tools": foreach (var window in OwnedWindows.Cast<Window>().ToArray()) window.Close(); break;
                case "close": _ = Dispatcher.BeginInvoke(async () => { await Task.Delay(150);exitRequested=true; Close(); }); break;
                case "stop": case "pause": case "next": case "previous": case "random": case "record": case "cycle-mode": await HandleActionAsync(command); break;
                default: throw new ArgumentException("Unknown remote command: " + command);
            }
            return "{\"ok\":true}";
        }
        catch (Exception ex) { Log("Remote: " + ex.Message); return JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
    }
    private void MoveTo(int index, string category) { library.AddCategory(category); library.Document.Entries[index].Category = category; }
    private void SaveScreenshot(string path)
    {
        Window target = OwnedWindows.Cast<Window>().FirstOrDefault(x => x.IsVisible) ?? this;
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(target);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root,i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static T? VisualAncestor<T>(DependencyObject? item) where T:DependencyObject { while(item is not null){if(item is T found)return found;item=VisualTreeHelper.GetParent(item);}return null; }
    private async Task RunSmokeTestAsync()
    {
        var result = new Dictionary<string, object>();
        try
        {
            result["windowLoaded"] = initialized; result["title"] = Title; result["hotbarButtons"] = HotbarGrid.Children.Count;
            result["devices"] = audio.Devices.Count; result["microphoneStatus"] = audio.GetInjectionStatus();
            result["categories"] = CategoryTree.Items.Count; result["libraryEntries"] = library.Document.Entries.Count;
            await Task.Delay(500); result["passed"] = initialized && HotbarGrid.Children.Count == 12 && CategoryTree.Items.Count >= 1 && RenderedMenuLabels().All(x=>!string.IsNullOrWhiteSpace(x));
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(stateDirectory, "ui-smoke.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); Close();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if(Settings.MinimizeInsteadOfClose&&!exitRequested&&!arguments.Contains("--smoke-test")){e.Cancel=true;MinimizeWindow();return;}
        e.Cancel = true; closing = true;CancelStatusMenu();tray?.Dispose();
        try { CompositionTarget.Rendering -= RenderPlaybackCursor; lifetime.Cancel(); statusTimer.Stop(); saveTimer.Stop(); await forwardingController.StopAsync();hotkeys.Dispose(); remote.Dispose(); await StopAsync(); StopMicMonitor(); await audio.StopRecordingAsync(); audio.Dispose(); notifications.Dispose(); autoKeys.Dispose(); preferenceStore.Save(); library.Dispose(); http.Dispose(); lifetime.Dispose(); }
        catch (Exception ex) { File.AppendAllText(Path.Combine(stateDirectory, "errors.log"), ex + "\n"); }
        _ = Dispatcher.BeginInvoke(Close);
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // The global hook already dispatched this key. Do not dispatch it twice
        // or let a repeated bound key invoke a local menu command.
        if(TryHandleGlobalHotkey(e,Keyboard.Modifiers))return;
        if(e.OriginalSource is TextBox&&!(Keyboard.Modifiers==ModifierKeys.Control&&e.Key is Key.R or Key.T or Key.E or Key.F or Key.S))return;
        if (e.Key == Key.Enter) { Play_Click(this, new()); e.Handled = true; }
        else if (e.Key == Key.Delete) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Recycle_Click(this, new()); else if(CategoryTree.IsKeyboardFocusWithin) DeleteCategory_Click(this,new()); else Remove_Click(this, new()); e.Handled = true; }
        else if (e.Key == Key.F1) { About_Click(this,new());e.Handled=true; }
        else if (e.Key == Key.F2 && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) { if(CategoryTree.IsKeyboardFocusWithin) RenameCategory_Click(this,new()); else if(Keyboard.Modifiers==ModifierKeys.Shift) Rename_Click(this,new()); else RenameFile_Click(this,new()); e.Handled=true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key) { case Key.F: Find_Click(this,new()); break; case Key.S: SaveList_Click(this, new()); break; case Key.A: SoundGrid.SelectAll();e.Handled=true;break; case Key.R: Recorder_Click(this,new());e.Handled=true;break;case Key.T:Tts_Click(this,new());e.Handled=true;break;case Key.E:Editor_Click(this,new());e.Handled=true;break; case Key.Z: library.Undo(); break; case Key.Y: library.Redo(); break; case Key.C: CopyEntries(false); break; case Key.X: CopyEntries(true); break; case Key.V: PasteEntries(); break; }
        }
    }
    private SoundEntry[] copiedEntries = []; private bool cutEntries;
    private void CopyEntries(bool cut) { copiedEntries = SoundGrid.SelectedItems.Cast<SoundEntry>().ToArray(); cutEntries = cut; }
    private void PasteEntries()
    {
        string category = selectedCategory == "*" ? "" : selectedCategory;
        if (copiedEntries.Length > 0)
        {
            foreach (var entry in copiedEntries) if (cutEntries) entry.Category = category; else library.Document.Entries.Add(new() { FilePath = entry.FilePath, Title = entry.Title, Category = category, Tags = entry.Tags, DurationSeconds = entry.DurationSeconds });
            if (cutEntries) copiedEntries = [];
        }
        else if (Clipboard.ContainsFileDropList()) _ = GuardAsync(() => AddPathsAsync(Clipboard.GetFileDropList().Cast<string>()));
    }
    private void Log(string message) { if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; } messages.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + message); while (messages.Count > 200) messages.Dequeue(); if (StatusText is not null) StatusText.Text = message; if(LogTextBox is not null && LogPanel.Visibility==Visibility.Visible){LogTextBox.Text=string.Join(Environment.NewLine,messages);LogTextBox.ScrollToEnd();} }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "Soundboard Lab", MessageBoxButton.OK, MessageBoxImage.Information); } }
    private async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "Soundboard Lab", MessageBoxButton.OK, MessageBoxImage.Information); } }
    internal string? Prompt(string title, string label, string initial) => Dialogs.Prompt(this, title, label, initial);
    private void ShowText(string title, string text) => Dialogs.ShowText(this, title, text);
}
