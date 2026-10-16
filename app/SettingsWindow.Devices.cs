using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using SoundboardLab.Audio;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    internal Task RefreshDevicesAsync(bool initial=false)=>refreshDevices(initial);
    private void BuildDevices(Panel page)
    {
        // Devices fills the page; only the device list scrolls when the window is shorter.
        var viewport=(ScrollViewer)page.Parent;var layout=new Grid {Margin=page.Margin,MaxWidth=505,HorizontalAlignment=HorizontalAlignment.Left};layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});viewport.Content=layout;viewport.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;
        var group=Group(layout,"Playback device");var playback=Row(group,"Playback device:",92);
        var render=new ComboBox {IsEnabled=false,DisplayMemberPath="Name",SelectedValuePath="Id",Width=300,HorizontalAlignment=HorizontalAlignment.Left};
        AutomationProperties.SetAutomationId(render,nameof(AppPreferences.RenderDeviceId));Grid.SetColumn(render,1);playback.Children.Add(render);
        group=Group(layout,"Recording devices");var recordingGroup=(GroupBox)group.Parent;Grid.SetRow(recordingGroup,1);recordingGroup.Margin=new(0);recordingGroup.VerticalContentAlignment=VerticalAlignment.Stretch;
        Explain(group,"Sounds will be played on the recording devices, which you select here.");Check(group,"Check device configuration on startup",nameof(AppPreferences.CheckDevicesOnStartup));
        group.Children.Add(new TextBlock {Text="Check at least one device:",Margin=new(2,4,0,6)});
        var rows=new ObservableCollection<DeviceRow>();
        var table=new ListView {Height=388,HorizontalContentAlignment=HorizontalAlignment.Stretch};AutomationProperties.SetAutomationId(table,"RecordingDevices");
        var itemStyle=new Style(typeof(ListViewItem),(Style)FindResource(typeof(ListViewItem)));itemStyle.Setters.Add(new Setter(Control.HeightProperty,68d));itemStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));table.ItemContainerStyle=itemStyle;
        var columns=new GridView();
        var use=new FrameworkElementFactory(typeof(CheckBox));use.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);use.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);use.SetValue(FrameworkElement.MarginProperty,new Thickness(0));use.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(DeviceRow.Use)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        var useTemplate=new DataTemplate {VisualTree=use};columns.Columns.Add(new(){Header="Use",Width=40,CellTemplate=useTemplate});
        var deviceCell=new FrameworkElementFactory(typeof(DeviceDisplay));deviceCell.SetBinding(DeviceDisplay.DeviceProperty,new Binding());
        var deviceColumn=new GridViewColumn {Header="Device",Width=330,CellTemplate=new DataTemplate {VisualTree=deviceCell}};columns.Columns.Add(deviceColumn);
        columns.Columns.Add(new(){Header="Boost",Width=44,DisplayMemberBinding=new Binding(nameof(DeviceRow.BoostText))});
        var statusText=new FrameworkElementFactory(typeof(TextBlock));statusText.SetBinding(TextBlock.TextProperty,new Binding(nameof(DeviceRow.Status)));statusText.SetBinding(TextBlock.ForegroundProperty,new Binding(nameof(DeviceRow.StatusBrush)));
        columns.Columns.Add(new(){Header="Status",Width=46,CellTemplate=new DataTemplate {VisualTree=statusText}});
        table.View=columns;table.ItemsSource=rows;group.Children.Add(table);
        table.SizeChanged+=(_,_)=>deviceColumn.Width=Math.Max(100,table.ActualWidth-40-44-46-8);
        var footer=new Grid {Margin=new(0,4,0,0)};footer.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});group.Children.Add(footer);
        var summary=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};footer.Children.Add(summary);
        summary.Children.Add(new TextBlock {Text="Status: ",VerticalAlignment=VerticalAlignment.Center});
        var status=new TextBlock {FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetAutomationId(status,"RecordingDeviceStatus");summary.Children.Add(status);
        summary.Children.Add(HelpButton("RecordingDeviceStatusHelp","Device configuration","Select the same recording device in your voice application. If its status is Bad, use Repair, then reopen recording applications. Use Restart Win-Audio only if the audio engine needs to reload its configuration."));
        void UpdateSummary(){bool good=rows.Any(x=>x.Use)&&rows.Where(x=>x.Use).All(x=>x.Installed);Localization.SetText(status,good?"Good":"Repair required");status.Foreground=good?Brushes.Green:Brushes.Red;}
        void UpdateSelection(){draft.CaptureDeviceIds=rows.Where(x=>x.Use).Select(x=>x.Id).ToList();draft.CaptureDeviceId=draft.CaptureDeviceIds.FirstOrDefault()??"";UpdateSummary();Dirty();}
        Button repair=null!,refresh=null!;int generation=devicePageGeneration;Task pendingRefresh=Task.CompletedTask;bool loaded=false;
        async Task Repair(){try{await main.ConfigureCaptureDevicesAsync(rows.Where(x=>x.Use).Select(x=>x.Id),true);await Refresh();}catch(Exception ex){Dialogs.Error(this,ex);}}
        repair=Button("",async(_,_)=>await Repair());var repairContent=new StackPanel {Orientation=Orientation.Horizontal};repairContent.Children.Add(ShieldIcon());repairContent.Children.Add(new TextBlock {Text="Repair",VerticalAlignment=VerticalAlignment.Center});repair.Content=repairContent;AutomationProperties.SetAutomationId(repair,"RepairRecordingDevices");Grid.SetColumn(repair,1);footer.Children.Add(repair);
        refresh=AudioIconButton(new System.Windows.Shapes.Path {Data=Geometry.Parse("M14,3 A7,7 0 1 0 15,11 L12,10 A4,4 0 1 1 11,5 L8,8 H16 V0 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,103,171)),Width=17,Height=17,Stretch=Stretch.Uniform},"Refresh","RefreshRecordingDevices",async(_,_)=>await Refresh());Grid.SetColumn(refresh,2);footer.Children.Add(refresh);
        Task Refresh(bool initial=false){if(!pendingRefresh.IsCompleted)return pendingRefresh;return pendingRefresh=RefreshCore(initial);}
        async Task RefreshCore(bool initial){
            table.IsEnabled=render.IsEnabled=repair.IsEnabled=refresh.IsEnabled=false;
            Localization.SetText(status,"Loading…");status.Foreground=Brushes.Gray;status.ToolTip=null;
            var token=preferencesLifetime.Token;
            try{
                var snapshot=await Task.Run(()=>readDevices(token),token).WaitAsync(DeviceDiscoveryTimeout,token);
                if(token.IsCancellationRequested||generation!=devicePageGeneration)return;
                var selection=loaded?rows.Where(x=>x.Use).Select(x=>x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase):draft.CaptureDeviceIds.Append(draft.CaptureDeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.Clear();foreach(var device in snapshot.Devices.Where(x=>x.IsInput)){
                    var row=new DeviceRow {Id=device.Id,Name=device.Name,Use=selection.Contains(device.Id),Boost=draft.DeviceBoosts.GetValueOrDefault(device.Id),IsDefault=device.Id.Equals(snapshot.DefaultCaptureId,StringComparison.OrdinalIgnoreCase),Installed=snapshot.InstalledCaptures.Contains(device.Id)};
                    rows.Add(row);row.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DeviceRow.Use))UpdateSelection();};
                }
                if(initial&&!loaded&&selection.All(string.IsNullOrEmpty)&&rows.Count>0)(rows.FirstOrDefault(x=>x.IsDefault)??rows[0]).Use=true;
                UpdateSelection();
                // Replacing ItemsSource must not write an intermediate null
                // selection into the stored playback-device preference.
                BindingOperations.ClearBinding(render,Selector.SelectedValueProperty);
                var playbackDevices=new[]{new AudioDevice("","Default",false)}.Concat(snapshot.Devices.Where(x=>!x.IsInput)).ToList();
                if(!playbackDevices.Any(x=>x.Id==draft.RenderDeviceId))playbackDevices.Add(new(draft.RenderDeviceId,"Unavailable playback device",false));
                render.ItemsSource=playbackDevices;render.SetBinding(Selector.SelectedValueProperty,Bind(nameof(AppPreferences.RenderDeviceId)));
                deviceSnapshot=snapshot;loaded=true;
                // Default discovery must not mark an otherwise unchanged
                // dialog dirty or discard edits made on other pages.
                if(initial&&this.snapshot.Length>0){var baseline=JsonSerializer.Deserialize<AppPreferences>(this.snapshot)!;if(baseline.CaptureDeviceIds.Count==0&&string.IsNullOrEmpty(baseline.CaptureDeviceId)){baseline.CaptureDeviceIds=[..draft.CaptureDeviceIds];baseline.CaptureDeviceId=draft.CaptureDeviceId;this.snapshot=JsonSerializer.Serialize(baseline);}}
                Dirty();Localization.Apply(layout);
            }catch(OperationCanceledException) when(token.IsCancellationRequested){}
            catch(Exception ex){if(token.IsCancellationRequested||generation!=devicePageGeneration)return;Localization.SetText(status,"Could not load devices");status.Foreground=Brushes.Red;status.ToolTip=ex is TimeoutException?"Windows did not respond to device discovery. Try Refresh.":ex.Message;}
            finally{if(!token.IsCancellationRequested&&generation==devicePageGeneration){table.IsEnabled=render.IsEnabled=repair.IsEnabled=loaded;refresh.IsEnabled=true;}}
        }
        refreshDevices=Refresh;table.IsEnabled=repair.IsEnabled=false;status.Text=Localization.Text("Loading…");status.Foreground=Brushes.Gray;
        var menu=new ContextMenu {Background=new SolidColorBrush(Color.FromRgb(240,240,240)),Foreground=Brushes.Black};menu.Resources["Text"]=new SolidColorBrush(Color.FromRgb(0,35,74));menu.Resources["Palette_414141"]=new SolidColorBrush(Color.FromRgb(204,232,255));
        MenuItem Command(string title,RoutedEventHandler handler){var item=new MenuItem {Header=title,Uid=title,Style=(Style)FindResource("DeviceMenuItem")};item.Click+=handler;menu.Items.Add(item);return item;}
        var boost=Command("Set boost",(_,_)=>{if(table.SelectedItem is DeviceRow row)SetDeviceBoost(row);});
        var setup=Command("Set setup mode",(_,_)=>{if(table.SelectedItem is DeviceRow row){var dialog=CreateSetupModeWindow(row,RefreshAfterSetup);dialog.Owner=this;dialog.ShowDialog();}});
        var separator=new Separator();menu.Items.Add(separator);Command("Windows Audio Preferences",(_,_)=>{try{Process.Start(new ProcessStartInfo("control.exe","mmsys.cpl,,1"){UseShellExecute=true});}catch(Exception ex){Dialogs.Error(this,ex);}});
        var restart=Command("Restart Win-Audio",async(_,_)=>{if(MessageBox.Show(this,"This briefly interrupts playback and recording in all applications. Restart Windows Audio now?","Restart Windows Audio Service?",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;try{await main.RestartWindowsAudioAsync();await Refresh();}catch(Exception ex){Dialogs.Error(this,ex);}});restart.Icon=ShieldIcon();
        menu.Opened+=(_,_)=>{bool selected=table.SelectedItem is DeviceRow;boost.Visibility=setup.Visibility=separator.Visibility=selected?Visibility.Visible:Visibility.Collapsed;boost.IsEnabled=setup.IsEnabled=selected;Localization.Apply(menu);};
        table.PreviewMouseRightButtonDown+=(_,e)=>{var item=FindDeviceItem(e.OriginalSource as DependencyObject);table.SelectedItem=item?.Content;boost.IsEnabled=setup.IsEnabled=item is not null;};
        table.PreviewMouseLeftButtonDown+=(_,e)=>{if(e.ClickCount<2||e.ClickCount%2!=0||FindDeviceItem(e.OriginalSource as DependencyObject) is not {} item)return;if(e.OriginalSource is DependencyObject origin&&FindDeviceCheck(origin))return;e.Handled=true;table.SelectedItem=item.Content;if(item.Content is DeviceRow row)SetDeviceBoost(row);};
        table.ContextMenu=menu;
        var recordingLayout=new DockPanel();var recordingChildren=group.Children.Cast<UIElement>().ToArray();group.Children.Clear();
        foreach(var child in recordingChildren.Where(x=>x!=table)){DockPanel.SetDock(child,child==footer?Dock.Bottom:Dock.Top);recordingLayout.Children.Add(child);}
        table.ClearValue(HeightProperty);recordingLayout.Children.Add(table);recordingGroup.Content=recordingLayout;
        void RefreshAfterSetup()=>_=Refresh();
    }
    private static ListViewItem? FindDeviceItem(DependencyObject? item){while(item is not null){if(item is ListViewItem row)return row;if(item is GridViewColumnHeader)return null;item=item is Visual?VisualTreeHelper.GetParent(item):LogicalTreeHelper.GetParent(item);}return null;}
    private static bool FindDeviceCheck(DependencyObject? item){while(item is not null&&item is not ListViewItem){if(item is CheckBox)return true;item=item is Visual?VisualTreeHelper.GetParent(item):LogicalTreeHelper.GetParent(item);}return false;}
    private static FrameworkElement ShieldIcon(){var panel=new Grid {Width=15,Height=16,Margin=new(0,0,4,0)};panel.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M1,1 L7.5,0 14,1 V7 Q14,11 7.5,16 Q1,11 1,7 Z"),Fill=new SolidColorBrush(Color.FromRgb(255,205,40)),Stroke=new SolidColorBrush(Color.FromRgb(0,103,171)),StrokeThickness=1});panel.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M7.5,0 L14,1 V7 H7.5 V16 Q1,11 1,7 H7.5 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,120,215))});return panel;}
    private void SetDeviceBoost(DeviceRow row)
    {
        if(Dialogs.Prompt(this,"Set boost","Recording gain in dB (−30 to +30)",row.Boost.ToString()) is not {} value)return;
        if(!double.TryParse(value,out double db)||!double.IsFinite(db)||db is <-30 or >30){MessageBox.Show(this,"Enter a boost from -30 to +30 dB.","Set boost");return;}
        draft.DeviceBoosts[row.Id]=db;row.Boost=db;Dirty();
    }
    private sealed class DeviceRow:ObservableModel
    {
        private bool use,installed;private double boost;
        public string Id {get;set;}="";public string Name {get;set;}="";public bool IsDefault {get;set;}
        public string EndpointName=>Name.LastIndexOf(" (",StringComparison.Ordinal) is int split&&split>0?Name[..split]:Name;
        public string ControllerName=>Name.LastIndexOf(" (",StringComparison.Ordinal) is int split&&split>0?Name[(split+1)..]:"";
        public bool Use {get=>use;set{if(Set(ref use,value)){Raise(nameof(Status));Raise(nameof(StatusBrush));}}}
        public bool Installed {get=>installed;set{if(Set(ref installed,value)){Raise(nameof(Status));Raise(nameof(StatusBrush));}}}
        public double Boost {get=>boost;set{if(Set(ref boost,value))Raise(nameof(BoostText));}}
        public string BoostText=>Boost==0?"":Boost.ToString("+0.#;-0.#")+" dB";
        public string Status=>!Use?"":Installed?"Good":"Bad";
        public Brush StatusBrush=>Installed?Brushes.Green:Brushes.Red;
    }
    private sealed class DeviceDisplay:Grid
    {
        public static readonly DependencyProperty DeviceProperty=DependencyProperty.Register(nameof(Device),typeof(DeviceRow),typeof(DeviceDisplay),new PropertyMetadata(null,(owner,_)=>((DeviceDisplay)owner).Refresh()));
        public DeviceRow? Device {get=>(DeviceRow?)GetValue(DeviceProperty);set=>SetValue(DeviceProperty,value);}
        private void Refresh()
        {
            Children.Clear();ColumnDefinitions.Clear();if(Device is not {} device)return;ColumnDefinitions.Add(new(){Width=new(48)});ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
            var icon=new Grid {Width=40,Height=50,Margin=new(0,0,6,0)};
            icon.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M13,11 Q13,1 23,1 Q33,1 33,11 V26 Q33,36 23,36 Q13,36 13,26 Z M7,22 H10 V26 Q10,40 23,40 Q36,40 36,26 V22 H39 V26 Q39,41 25,43 V47 H35 V50 H11 V47 H21 V43 Q7,41 7,26 Z"),Fill=new SolidColorBrush(Color.FromRgb(0,103,171)),Stretch=Stretch.Uniform});
            if(device.IsDefault){var badge=new Grid {Width=22,Height=22,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom};badge.Children.Add(new System.Windows.Shapes.Ellipse {Fill=new SolidColorBrush(Color.FromRgb(132,169,67)),Stroke=Brushes.White,StrokeThickness=2});badge.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M4,11 L9,16 17,5"),Stroke=Brushes.White,StrokeThickness=2,Margin=new(2)});AutomationProperties.SetName(badge,"Default recording device");icon.Children.Add(badge);}Children.Add(icon);
            var labels=new StackPanel {VerticalAlignment=VerticalAlignment.Center};labels.Children.Add(new TextBlock {Text=device.EndpointName,TextTrimming=TextTrimming.CharacterEllipsis});labels.Children.Add(new TextBlock {Text=device.ControllerName,Foreground=Brushes.Gray,Margin=new(0,4,0,0),TextTrimming=TextTrimming.CharacterEllipsis});Grid.SetColumn(labels,1);Children.Add(labels);ToolTip=device.Name;
        }
    }
}
