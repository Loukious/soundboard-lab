using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SoundboardLab.Audio;
using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

internal sealed partial class SettingsWindow : Window
{
    private readonly MainWindow main;
    private readonly AudioService audio;
    private readonly PreferenceStore store;
    private readonly Func<Task> apply;
    private readonly Func<Window,string,string,IEnumerable<HotkeyAssignment>?,bool?,string?> hotkeyEditor;
    private readonly AudioService preview=new();
    private AppPreferences draft;
    private readonly DockPanel root=new() { Margin=new(8) };
    private readonly TabControl tabs=new();
    private readonly Button applyButton=new() { Content="Apply" };
    private readonly ObservableCollection<HotkeyRow> hotkeyRows=[];
    private ListView autoKeysList=null!;
    private bool building,saving;
    private string snapshot="",hotkeySnapshot="";
    private string CurrentSnapshot()=>JsonSerializer.Serialize(draft);
    private string HotkeySnapshot()=>JsonSerializer.Serialize(hotkeyRows);
    private void RememberSnapshot(){snapshot=CurrentSnapshot();hotkeySnapshot=HotkeySnapshot();}
    private static AppPreferences Clone(AppPreferences value)=>JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(value))!;
    public SettingsWindow(MainWindow main,AudioService audio,PreferenceStore store,Func<Task> apply,
        Func<Window,string,string,IEnumerable<HotkeyAssignment>?,bool?,string?>? hotkeyEditor=null)
    {
        this.main=main;this.audio=audio;this.store=store;this.apply=apply;this.hotkeyEditor=hotkeyEditor??main.EditHotkey;draft=Clone(store.Settings);
        Style=(Style)Application.Current.FindResource(typeof(Window));
        Title="Preferences";Width=576;Height=736;MinWidth=560;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(240,240,240));Foreground=new SolidColorBrush(Color.FromRgb(0,35,74));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/SoundboardLab;component/SettingsResources.xaml",UriKind.Relative) });
        Content=root;DataContext=draft;
        var footer=new DockPanel { Margin=new(0,12,0,0),LastChildFill=false };DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var reset=Button("Reset",(_,_)=>{draft=new();SoundAssets.Initialize(draft,main.StateDirectory);DataContext=draft;BuildPages();applyButton.IsEnabled=true;});DockPanel.SetDock(reset,Dock.Left);footer.Children.Add(reset);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal };DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);
        buttons.Children.Add(Button("OK",async(_,_)=>await Save(true)));buttons.Children.Add(Button("Cancel",(_,_)=>Close()));buttons.Children.Add(applyButton);
        System.Windows.Automation.AutomationProperties.SetAutomationId(applyButton,"PreferencesApply");applyButton.Click+=async(_,_)=>await Save(false);root.Children.Add(tabs);
        root.AddHandler(TextBoxBase.TextChangedEvent,new TextChangedEventHandler((_,_)=>Dirty()));
        root.AddHandler(ToggleButton.CheckedEvent,new RoutedEventHandler((_,_)=>Dirty()));root.AddHandler(ToggleButton.UncheckedEvent,new RoutedEventHandler((_,_)=>Dirty()));
        root.AddHandler(Selector.SelectionChangedEvent,new SelectionChangedEventHandler((sender,e)=>{if(e.OriginalSource is not TabControl)Dirty();}));
        tabs.SelectionChanged+=(_,e)=>{if(e.OriginalSource==tabs){StopVoiceMeasurement(false);Dispatcher.BeginInvoke(()=>Localization.Apply(this));}};
        BuildPages();RememberSnapshot();Loaded+=(_,_)=>{Localization.Apply(this);RememberSnapshot();applyButton.IsEnabled=false;};Closed+=(_,_)=>{StopMeasurements();preview.Dispose();};
        main.ForwardingStateChanged+=ForwardingChanged;Closed+=(_,_)=>main.ForwardingStateChanged-=ForwardingChanged;
    }
    internal void SelectPage(string name)
    {
        foreach(TabItem page in tabs.Items)if(page.Uid==name){tabs.SelectedItem=page;return;}
    }
    private void ForwardingChanged()=>updateForwarding?.Invoke();
    private void Dirty() {if(!building)Dispatcher.BeginInvoke(()=>applyButton.IsEnabled=CurrentSnapshot()!=snapshot||HotkeySnapshot()!=hotkeySnapshot);}
    private static Binding Bind(string property)=>new(property) { Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true };
    private static Button Button(string text,RoutedEventHandler click) {var button=new Button {Content=text};button.Click+=click;return button;}
    private StackPanel Page(string name)
    {
        var panel=new StackPanel { Margin=new(8,8,4,0) };
        tabs.Items.Add(new TabItem { Header=name,Uid=name,Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } });return panel;
    }
    private static StackPanel Group(Panel parent,string title,bool bold=true)
    {
        var panel=new StackPanel();parent.Children.Add(new GroupBox { Header=new TextBlock { Text=title,Foreground=Brushes.Black,FontSize=bold?16:12,FontWeight=bold?FontWeights.Bold:FontWeights.Normal },Content=panel });return panel;
    }
    private static void Explain(Panel parent,string text) =>parent.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)});
    private static CheckBox Check(Panel parent,string title,string? property=null,bool value=false)
    {
        var check=new CheckBox { Content=title,IsChecked=value };if(property is not null)System.Windows.Automation.AutomationProperties.SetAutomationId(check,property);if(property is not null)check.SetBinding(ToggleButton.IsCheckedProperty,Bind(property));parent.Children.Add(check);return check;
    }
    private static Grid Row(Panel parent,string label,double labelWidth=134)
    {
        var row=new Grid { Margin=new(2,3,0,3),MinHeight=26 };row.ColumnDefinitions.Add(new(){Width=new(labelWidth)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});parent.Children.Add(row);return row;
    }
    private static TextBox Field(Panel parent,string label,string property,double width=85,double labelWidth=134)
    {
        var row=Row(parent,label,labelWidth);var field=new TextBox {Width=width,HorizontalAlignment=HorizontalAlignment.Left};System.Windows.Automation.AutomationProperties.SetAutomationId(field,property);field.SetBinding(TextBox.TextProperty,Bind(property));Grid.SetColumn(field,1);row.Children.Add(field);return field;
    }
    private static ComboBox Choice(Panel parent,string label,string[] choices,string selected,string? property=null,double labelWidth=134)
    {
        var row=Row(parent,label,labelWidth);var combo=new ComboBox { ItemsSource=choices,SelectedItem=selected,Width=200,HorizontalAlignment=HorizontalAlignment.Left };var template=new DataTemplate();var textFactory=new FrameworkElementFactory(typeof(TextBlock));textFactory.SetBinding(TextBlock.TextProperty,new Binding {Converter=new LocalizedTextConverter()});template.VisualTree=textFactory;combo.ItemTemplate=template;if(property is not null){System.Windows.Automation.AutomationProperties.SetAutomationId(combo,property);combo.SetBinding(Selector.SelectedItemProperty,Bind(property));}Grid.SetColumn(combo,1);row.Children.Add(combo);return combo;
    }
    private TextBox PathField(Panel parent,string label,string property,bool folder=false)
    {
        if(label.Length>0)parent.Children.Add(new TextBlock {Text=label,Margin=new(2,4,0,4)});
        var row=new DockPanel();parent.Children.Add(row);var text=new TextBox {MinWidth=70};System.Windows.Automation.AutomationProperties.SetAutomationId(text,property);text.SetBinding(TextBox.TextProperty,Bind(property));
        var browse=Button("Browse",(_,_)=>{if(folder){var dialog=new OpenFolderDialog();if(dialog.ShowDialog(this)==true)text.Text=dialog.FolderName;}else{var dialog=new OpenFileDialog {Filter=property==nameof(AppPreferences.ExternalEditorPath)?"Applications|*.exe|All files|*.*":"Audio files|*.wav;*.mp3;*.flac;*.ogg;*.m4a|All files|*.*"};if(dialog.ShowDialog(this)==true)text.Text=dialog.FileName;}});
        DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(text);return text;
    }
    private static ListView Table(Panel parent,double height,params (string title,string path,double width)[] columns)
    {
        var table=new ListView {Height=height,Margin=new(0,1,0,3)};var view=new GridView();foreach(var col in columns)view.Columns.Add(new GridViewColumn {Header=col.title,DisplayMemberBinding=new Binding(col.path){Converter=col.path is "Title" or "Type" or "Status"?new LocalizedTextConverter():null},Width=col.width});table.View=view;parent.Children.Add(table);return table;
    }
    private void BuildPages()
    {
        StopMeasurements();
        building=true;int selected=Math.Max(0,tabs.SelectedIndex);tabs.Items.Clear();hotkeyRows.Clear();
        var audioPage=Page("Audio");BuildAudio(audioPage);BuildForwarding(audioPage);BuildHotkeys(Page("Hotkeys"));BuildInterface(Page("Interface"));BuildDevices(Page("Devices"));BuildRecorder(Page("Recorder"));BuildEditor(Page("Editor"));BuildNotifications(Page("Notifications"));
        tabs.SelectedIndex=Math.Min(selected,6);Localization.Apply(this);building=false;
    }
    private void BuildHotkeys(Panel page)
    {
        var group=Group(page,"Special Hotkeys");
        foreach(var action in SpecialHotkeyActions.All)hotkeyRows.Add(new(){Action=action.Key,Title=ActionTitle(action),Hotkey=draft.ActionHotkeys.GetValueOrDefault(action.Key,draft.ActionHotkeys.GetValueOrDefault(SpecialHotkeyActions.NormalizeKey(action.Key),""))});
        foreach(var pair in draft.ActionHotkeys.Where(x=>!SpecialHotkeyActions.All.Any(a=>a.Key==x.Key||SpecialHotkeyActions.NormalizeKey(a.Key)==x.Key)))hotkeyRows.Add(new(){Action=pair.Key,Title=pair.Key,Hotkey=pair.Value});
        var table=Table(group,192,("Action",nameof(HotkeyRow.Title),320),("Hotkey",nameof(HotkeyRow.Hotkey),155));ConfigureActionIcons(table);System.Windows.Automation.AutomationProperties.SetAutomationId(table,"SpecialHotkeyActions");table.ItemsSource=hotkeyRows;
        var buttons=new StackPanel {Orientation=Orientation.Horizontal};group.Children.Add(buttons);
        var assign=Button("Set hotkey",(_,_)=>SetSpecialHotkey(table));assign.IsEnabled=false;buttons.Children.Add(assign);
        table.PreviewMouseLeftButtonDown+=SpecialHotkey_Pressed;
        var change=Button("Change value",(_,_)=>{if(table.SelectedItem is not HotkeyRow row)return;var action=SpecialHotkeyActions.All.First(x=>x.Key==row.Action);if(Dialogs.Prompt(this,"Change value",action.Title+" ("+action.Unit+")",draft.ActionValues.GetValueOrDefault(action.Key,action.DefaultValue??0).ToString()) is {} text){if(!double.TryParse(text,out var value)||!double.IsFinite(value)||value<(action.Key.StartsWith("volumeSet")?0:1)||value>(action.Unit=="s"?3600:100)){MessageBox.Show(this,"Enter a value within "+(action.Unit=="s"?"1–3600 seconds":action.Key.StartsWith("volumeSet")?"0–100%":"1–100%"));return;}draft.ActionValues[action.Key]=value;row.Title=ActionTitle(action);table.Items.Refresh();Dirty();}});change.IsEnabled=false;buttons.Children.Add(change);table.SelectionChanged+=(_,_)=>{assign.IsEnabled=table.SelectedItem is HotkeyRow;change.IsEnabled=table.SelectedItem is HotkeyRow row&&SpecialHotkeyActions.All.Any(x=>x.Key==row.Action&&x.DefaultValue.HasValue);};
        group=Group(page,"Numpad Hotkeys");ModifierRow(group,"Enable Alt+Numcode hotkeys",nameof(AppPreferences.AltNumcodeHotkeys),true);ModifierRow(group,"Enable Ctrl+Alt+Index hotkeys",nameof(AppPreferences.IndexHotkeys),false);
        group=Group(page,"Auto Keys");Explain(group,"Presses keys automatically when you play sounds.");autoKeysList=Table(group,94,("Key","Key",160),("Type","Type",300));RefreshAutoKeys();
        buttons=new(){Orientation=Orientation.Horizontal};group.Children.Add(buttons);
        buttons.Children.Add(Button("Add",(_,_)=>EditAutoKey(false)));var editRule=Button("Edit",(_,_)=>EditAutoKey(true));var removeRule=Button("Remove",(_,_)=>{if(autoKeysList.SelectedItem is AutoKeyEntry rule)draft.AutoKeys.Remove(rule);RefreshAutoKeys();Dirty();});editRule.IsEnabled=removeRule.IsEnabled=false;buttons.Children.Add(editRule);buttons.Children.Add(removeRule);autoKeysList.SelectionChanged+=(_,_)=>editRule.IsEnabled=removeRule.IsEnabled=autoKeysList.SelectedItem is AutoKeyEntry;

        group=Group(page,"Advanced options");Check(group,"Pass hotkeys",nameof(AppPreferences.PassHotkeys));Check(group,"Auto stop (recommended)",nameof(AppPreferences.StopOnNewSound));Check(group,"Allow left and right modifiers",nameof(AppPreferences.AllowLeftRightModifiers));Check(group,"Same hotkeys in all categories",nameof(AppPreferences.SameHotkeysInAllCategories));
    }
    private string ActionTitle(SpecialHotkeyAction action)=>Localization.Text(action.Title)+(action.DefaultValue.HasValue?" "+draft.ActionValues.GetValueOrDefault(action.Key,action.DefaultValue.Value).ToString("0.##")+" "+action.Unit:"");
    private void RefreshAutoKeys(){autoKeysList.ItemsSource=null;autoKeysList.ItemsSource=draft.AutoKeys;}
    private void EditAutoKey(bool edit)
    {
        var old=edit?autoKeysList.SelectedItem as AutoKeyEntry:null;if(edit&&old is null)return;
        var dialog=new AutoKeyEditorWindow(old,draft){Owner=this};if(dialog.ShowDialog()!=true)return;
        if(old is not null)draft.AutoKeys[draft.AutoKeys.IndexOf(old)]=dialog.Value;else draft.AutoKeys.Add(dialog.Value);
        RefreshAutoKeys();Dirty();
    }
    private void BuildInterface(Panel page)
    {
        var group=Group(page,"Language");Choice(group,"Language:",Localization.Languages,draft.Language,property:nameof(AppPreferences.Language),labelWidth:80);Choice(group,"Date format:",["Language default","ISO 8601 - 2016-10-30","English (en_US) - 10/30/2016","English (en_GB) - 30/10/2016","Deutsch (de_DE) - 30.10.2016"],draft.DateFormat,property:nameof(AppPreferences.DateFormat),labelWidth:80);
        group=Group(page,"Toolbar");Choice(group,"Style:",["System default","Light","Dark","Navy"],draft.Theme,property:nameof(AppPreferences.Theme));Choice(group,"Zoom:",["100%","150%","200%"],draft.Zoom,property:nameof(AppPreferences.Zoom));Choice(group,"Toolbar size:",["Small","Medium","Large"],draft.ToolbarSize,property:nameof(AppPreferences.ToolbarSize));var colorChoice=Choice(group,"Toolbar and icon color:",["Blue","Red","Green","Orange","Purple","Yellow","White","Black","Teal"],draft.ToolbarColor,property:nameof(AppPreferences.ToolbarColor));colorChoice.IsEditable=true;colorChoice.SetBinding(ComboBox.TextProperty,Bind(nameof(AppPreferences.ToolbarColor)));colorChoice.ToolTip="Choose a color or enter a color name or #RRGGBB.";
        var row=Row(group,"Playback position update rate:",180);var ratePanel=new StackPanel {Orientation=Orientation.Horizontal};Grid.SetColumn(ratePanel,1);row.Children.Add(ratePanel);var rate=new Slider {Width=145};rate.SetBinding(RangeBase.ValueProperty,Bind(nameof(AppPreferences.CursorUpdatesPerSecond)));ratePanel.Children.Add(rate);var rateLabel=new TextBlock {Margin=new(14,5,0,0)};rateLabel.SetBinding(TextBlock.TextProperty,new Binding(nameof(AppPreferences.CursorUpdatesPerSecond)){StringFormat="{0}/s"});ratePanel.Children.Add(rateLabel);rate.ValueChanged+=(_,_)=>Dirty();
        Check(group,"Display microphone level on toolbar",nameof(AppPreferences.ShowMicrophoneLevel));
        var responseRow=new StackPanel {Orientation=Orientation.Horizontal};group.Children.Add(responseRow);
        var linear=Check(responseRow,"Volume slider works linearly",nameof(AppPreferences.LinearVolumeSlider));
        var response=new VolumeResponseGraph {Margin=new(24,0,0,0),Linear=draft.LinearVolumeSlider};System.Windows.Automation.AutomationProperties.SetAutomationId(response,"VolumeSliderGraph");responseRow.Children.Add(response);
        linear.Checked+=(_,_)=>response.Linear=true;linear.Unchecked+=(_,_)=>response.Linear=false;
        group=Group(page,"Play mode");Explain(group,"Determines whether a sound is played back on speakers, microphone, or both when you play it by hotkey, double-click, Enter or Play icon.");Check(group,"Allow resume in another play mode",nameof(AppPreferences.AllowResumeInAnotherMode));Choice(group,"Hotkey switches between:",["all play modes","default and speakers","default and microphone"],draft.ModeSwitch,property:nameof(AppPreferences.ModeSwitch),labelWidth:150);
        group=Group(page,"Misc");
        foreach(var (label,property) in new[]{("Run Soundboard Lab when Windows starts",nameof(AppPreferences.RunOnStartup)),("Start minimized",nameof(AppPreferences.StartMinimized)),("Show system tray icon",nameof(AppPreferences.ShowTrayIcon)),("Minimize to system tray",nameof(AppPreferences.MinimizeToTray)),("Minimize instead of close",nameof(AppPreferences.MinimizeInsteadOfClose)),("Show always on top",nameof(AppPreferences.AlwaysOnTop)),("Disable sorting in sound list",nameof(AppPreferences.DisableSorting)),("Use alternating row colors",nameof(AppPreferences.AlternatingRowColors)),("Typing begins search",nameof(AppPreferences.TypingBeginsSearch)),("Typing prefers TTS when TTS is open",nameof(AppPreferences.TypingPrefersTts)),("Remember playback state throughout application starts",nameof(AppPreferences.RememberPlaybackState))})Check(group,label,property);
    }
    private void BuildNotifications(Panel page)
    {
        var group=Group(page,"Recorder",false);Notification(group,"Start to record speakers","record-speakers");Notification(group,"Start to record microphone","record-microphone");Notification(group,"Stop recording","record-stop");
        group=Group(page,"Play mode",false);Check(group,"Notify me when playing first sound after application start",nameof(AppPreferences.NotifyFirstSound));Notification(group,"Default","mode-both");Notification(group,"Speakers","mode-speakers");Notification(group,"Microphone","mode-microphone");
        group=Group(page,"Hotkeys",false);Notification(group,"Enabled","hotkeys-on");Notification(group,"Disabled","hotkeys-off");group=Group(page,"Auto Keys",false);Notification(group,"Enabled","auto-on");Notification(group,"Disabled","auto-off");
        group=Group(page,"Hotbar",false);for(int i=1;i<=9;i++)Notification(group,"Page "+i,"page-"+i);
    }
    private void Notification(Panel parent,string title,string id)
    {
        if(!draft.Notifications.TryGetValue(id,out var setting))draft.Notifications[id]=setting=new();
        var row=new Grid {Margin=new(0,1,0,1)};foreach(double width in new[]{168d,1,76,25})row.ColumnDefinitions.Add(new(){Width=width==1?new(1,GridUnitType.Star):new(width)});parent.Children.Add(row);
        var check=new CheckBox {Content=title,Margin=new(0,4,0,4)};check.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(NotificationPreference.Enabled)){Source=setting,Mode=BindingMode.TwoWay});row.Children.Add(check);
        var path=new TextBox {MinWidth=50};path.SetBinding(TextBox.TextProperty,new Binding(nameof(NotificationPreference.Path)){Source=setting,Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});Grid.SetColumn(path,1);row.Children.Add(path);
        var browse=Button("Browse",(_,_)=>{var dialog=new OpenFileDialog {Filter="Audio files|*.wav;*.mp3;*.m4a;*.ogg;*.flac|All files|*.*"};if(dialog.ShowDialog(this)==true)path.Text=dialog.FileName;});Grid.SetColumn(browse,2);row.Children.Add(browse);
        var listen=Button("♫",async(_,_)=>{try{await PreviewSoundAsync(path.Text);}catch(Exception ex){Dialogs.Error(this,ex);}});listen.MinWidth=22;listen.Padding=new(1);Grid.SetColumn(listen,3);row.Children.Add(listen);
    }
    private async Task PreviewSoundAsync(string path){await preview.StopAsync();preview.SelectDevices(null,string.IsNullOrWhiteSpace(draft.RenderDeviceId)?null:draft.RenderDeviceId);preview.SpeakerVolume=(float)draft.SpeakerVolume;await preview.PlayAsync(path,PlaybackDestination.Speakers);}
    private async Task Save(bool close)
    {
        StopVoiceMeasurement(true);
        if(saving)return;saving=true;tabs.IsEnabled=false;applyButton.IsEnabled=false;
        try
        {
            if(Validation.GetHasError(root)||InvalidInput(root))throw new ArgumentException("Correct invalid numbers before applying preferences.");
            if(draft.MicrophoneGain is <0 or >4||draft.ClipVolume is <0 or >2||draft.TargetLufs is <-70 or >0||draft.AutoKeyDelayMs is <0 or >10000||draft.PushToTalkTailMs is <0 or >10000||draft.PlaybackDelayMs is <0 or >10000||draft.RecordingLimitSeconds is <0 or >86400||draft.FixedAdjustmentDb is <-30 or >30||draft.KaraokeDelayMs is <-10000 or >10000)throw new ArgumentException("A gain, delay, or recording limit is outside the supported range.");
            draft.CursorUpdatesPerSecond=Math.Clamp(draft.CursorUpdatesPerSecond,15,144);
            draft.ActionHotkeys=hotkeyRows.Where(x=>!string.IsNullOrWhiteSpace(x.Hotkey)).ToDictionary(x=>x.Action,x=>GlobalHotkeyService.NormalizeGesture(x.Hotkey));
            if(draft.ActionHotkeys.TryGetValue("forwardingPush",out string? pushKey)&&pushKey.Split('+').Last() is "WheelUp" or "WheelDown")throw new ArgumentException("Push to forward audio requires a key or mouse button that can be held down.");
            if(draft.AutoKeysEnabled&&draft.AutoKeys.Count==0&&!string.IsNullOrWhiteSpace(draft.AutoKeyGesture))_=AutoKeyService.ParseGesture(draft.AutoKeyGesture);
            foreach(var rule in draft.AutoKeys)_=AutoKeyService.ParseGesture(rule.Key);
            if(draft.VoiceActivationEnabled&&!File.Exists(draft.VoiceActivationPath))throw new FileNotFoundException("Choose an existing voice activation sound.");
            if(draft.CaptureDeviceIds.Count==0)throw new ArgumentException("Check at least one recording device.");
            if(draft.UseExternalEditor&&!File.Exists(draft.ExternalEditorPath))throw new FileNotFoundException("Choose an existing external sound editor.");
            PreferenceStore.Validate(draft);_=ColorConverter.ConvertFromString(draft.ToolbarColor);
            var previous=Clone(store.Settings);
            string[] previousIds=previous.CaptureDeviceIds.Count>0?previous.CaptureDeviceIds.ToArray():[string.IsNullOrWhiteSpace(previous.CaptureDeviceId)?audio.Devices.FirstOrDefault(x=>x.IsInput&&draft.CaptureDeviceIds.Contains(x.Id))?.Id??"":previous.CaptureDeviceId];
            if(!previousIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(draft.CaptureDeviceIds))await main.ConfigureCaptureDevicesAsync(draft.CaptureDeviceIds);
            var restoreHotkeys=main.PrepareSpecialHotkeyAssignments(draft);
            Copy(draft,store.Settings);
            try {await apply();store.Save();}catch{Copy(previous,store.Settings);restoreHotkeys();await apply();throw;}
            RememberSnapshot();applyButton.IsEnabled=false;if(close)Close();
        }
        catch(Exception ex){Dialogs.Error(this,ex);}
        finally{saving=false;tabs.IsEnabled=true;Dirty();}
    }
    private static void Copy(AppPreferences source,AppPreferences target){var copy=Clone(source);foreach(var property in typeof(AppPreferences).GetProperties())property.SetValue(target,property.GetValue(copy));}
    private static bool InvalidInput(DependencyObject root)
    {
        var seen=new HashSet<DependencyObject>();return Visit(root);
        bool Visit(DependencyObject item){if(!seen.Add(item))return false;if(Validation.GetHasError(item))return true;if(LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>().Any(Visit))return true;if(item is Visual)for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)if(Visit(VisualTreeHelper.GetChild(item,i)))return true;return false;}
    }
    private sealed class HotkeyRow {public string Action {get;set;}="";public string Title {get;set;}="";public string Hotkey {get;set;}="";}
}
