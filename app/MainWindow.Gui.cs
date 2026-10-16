using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using SoundboardLab.Models;
using SoundboardLab.Audio;

namespace SoundboardLab;

public sealed class GuiValueConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => (parameter as string) switch
    {
        "Hotkey" => value is SoundEntry sound&&Application.Current.MainWindow is MainWindow main?main.ShownHotkey(sound):"",
        "Filename" => System.IO.Path.GetFileName(value as string)??"",
        "Index" => value is SoundEntry indexed&&Application.Current.MainWindow is MainWindow owner?owner.SoundIndex(indexed).ToString(culture):value is int index?(index+1).ToString(culture):"",
        "Duration" => value is double seconds && double.IsFinite(seconds) && seconds >= 0 ? $"{(int)(seconds / 60)}:{(int)seconds % 60:D2}" : "",
        _ => value
    };
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
    public object Convert(object[] values,Type type,object parameter,CultureInfo culture)=>values.Length>=3&&values[0] is int index?values[index%2==0?1:2]:DependencyProperty.UnsetValue;
    public object[] ConvertBack(object value,Type[] types,object parameter,CultureInfo culture)=>types.Select(_=>Binding.DoNothing).ToArray();
}

public partial class MainWindow
{
    private readonly PlaybackPositionSmoother cursor = new();
    private TimeSpan lastRenderTime;
    private bool seekingWithMouse;
    private void Seek_Pressed(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;
        if(!audio.IsPlaying&&!audio.IsPaused)return;
        seekingWithMouse=true;SeekSlider.Focus();SeekSlider.CaptureMouse();SeekToPointer(e,false);
    }
    private void Seek_Moved(object sender,MouseEventArgs e)
    {
        if(seekingWithMouse){SeekToPointer(e,false);e.Handled=true;}
    }
    private void Seek_CaptureLost(object sender,MouseEventArgs e) => seekingWithMouse=false;
    private void CancelSeekGesture()
    {
        seekingWithMouse=false;
        if(SeekSlider.IsMouseCaptured)SeekSlider.ReleaseMouseCapture();
    }
    private void UpdateSeekPlaybackState()
    {
        bool active=audio.IsPlaying||audio.IsPaused;
        SeekSlider.IsEnabled=active;
        if(active)return;
        CancelSeekGesture();
        SeekSlider.Value=0;
    }
    private void SeekToPointer(MouseEventArgs e,bool commit)
    {
        if(SeekSlider.Template.FindName("PART_Track",SeekSlider) is not Track track)return;
        // Use an absolute coordinate, independent of the thumb's last layout.
        // ValueFromPoint adds a delta to the current value; repeated pointer
        // events before layout can otherwise apply the same delta twice.
        double thumbWidth=track.Thumb?.ActualWidth??0;
        double fraction=Math.Clamp((e.GetPosition(track).X-thumbWidth/2)/Math.Max(1,track.ActualWidth-thumbWidth),0,1);
        if(track.IsDirectionReversed)fraction=1-fraction;
        double value=fraction*audio.Duration.TotalSeconds;
        SeekSlider.Maximum=Math.Max(.001,audio.Duration.TotalSeconds);
        SeekSlider.Value=value;
        if(commit)audio.Seek(TimeSpan.FromSeconds(value));
    }
    private void RenderPlaybackCursor(object? sender,EventArgs e)
    {
        if(closing || !initialized || e is not RenderingEventArgs frame || frame.RenderingTime==lastRenderTime || (frame.RenderingTime-lastRenderTime).TotalSeconds + .0005 < 1d/Math.Clamp(Settings.CursorUpdatesPerSecond,15,144))return;
        lastRenderTime=frame.RenderingTime;MicrophoneMeter.Value=microphonePeak;
        UpdateSeekPlaybackState();
        var position=cursor.Update(audio.Position.TotalSeconds,audio.Duration.TotalSeconds,audio.IsPlaying,audio.TimelineVersion,frame.RenderingTime.TotalSeconds);
        if(!seekingWithMouse)SeekSlider.Value=position;
    }
    private void ToggleLog_Click(object sender,RoutedEventArgs e)
    {
        LogPanel.Visibility=LogPanel.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
        if(LogPanel.Visibility==Visibility.Visible){LogTextBox.Text=string.Join(Environment.NewLine,messages);LogTextBox.ScrollToEnd();}
    }
    private void AddHotbarPage_Click(object sender,RoutedEventArgs e)
    {
        int count=HotbarPage.Items.Count;
        if(count>=128){Log("The hotbar already has 128 pages.");return;}
        Settings.HotbarPages=count+1;
        RefreshHotbar();HotbarPage.SelectedIndex=count;preferenceStore.Save();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int bytes);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
    }
    private void Exit_Click(object sender, RoutedEventArgs e) {exitRequested=true;Close();}
    private void CycleMode_Click(object sender,RoutedEventArgs e) => SwitchPlayMode(false);
    private void ChooseMode_Click(object sender,RoutedEventArgs e) { if(sender is MenuItem item && int.TryParse(item.Tag?.ToString(),out int index)) DestinationBox.SelectedIndex=index; }
    private void AutoKeys_Changed(object sender,RoutedEventArgs e)
    {
        if(!initialized)return;
        if(AutoKeysBox.IsChecked==true && Settings.AutoKeys.Count==0 && string.IsNullOrWhiteSpace(Settings.AutoKeyGesture)) { AutoKeysBox.IsChecked=false;Log("Set push-to-talk keys in Preferences → Auto keys first.");return; }
        Settings.AutoKeysEnabled=AutoKeysBox.IsChecked==true;if(!Settings.AutoKeysEnabled)autoKeys.EndPlayback();ConfigureAutoKeys();Notify(Settings.AutoKeysEnabled?"auto-on":"auto-off");
    }
    private void BlockVoiceMenu_Click(object sender,RoutedEventArgs e) { if(sender is MenuItem item)BlockVoiceBox.IsChecked=item.IsChecked; }
    private void ConfigureColumnMenu()
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(CategoryTree,ScrollBarVisibility.Disabled);
        SoundGrid.PreviewMouseRightButtonDown += (_,e)=> {
            if(VisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not {} header)return;
            var menu=new ContextMenu();
            foreach(var column in SoundGrid.Columns) {
                var item=new MenuItem { Header=column.Header?.ToString()=="#"?"Index":column.Header,IsCheckable=true,IsChecked=column.Visibility==Visibility.Visible };
                item.Click+=(_,_)=>column.Visibility=item.IsChecked?Visibility.Visible:Visibility.Collapsed;
                menu.Items.Add(item);
            }
            header.ContextMenu=menu;
        };
    }
    private void Find_Click(object sender, RoutedEventArgs e) { SearchPanel.Visibility = Visibility.Visible; SearchBox.Focus(); SearchBox.SelectAll(); }
    private void CloseSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchPanel.Visibility = Visibility.Collapsed; SoundGrid.Focus(); }
    private void ToggleCategories_Click(object sender, RoutedEventArgs e)
    {
        bool show = (sender as MenuItem)?.IsChecked == true;
        CategoriesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ((Grid)CategoriesPanel.Parent).ColumnDefinitions[categoriesRight?2:0].Width = new GridLength(show ? 180 : 0);
    }
    private void ToggleCategoryInfo_Click(object sender, RoutedEventArgs e) => CategoryInfoPanel.Visibility = (sender as MenuItem)?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    private void ToggleHotbar_Click(object sender, RoutedEventArgs e)
    {
        Settings.ShowHotbar=(sender as MenuItem)?.IsChecked==true;
        HotbarPanel.Visibility=Settings.ShowHotbar?Visibility.Visible:Visibility.Collapsed;
        if(initialized)preferenceStore.Save();
    }
    private void About_Click(object sender, RoutedEventArgs e) => Dialogs.ShowText(this, "About Soundboard Lab", "Soundboard Lab\nIndependent development preview\n\nSoundpad-style controls and workflows, with an independent playback engine and capture mixer.");
    private void UpdateCategoryInformation()
    {
        if (CategoryNameLabel is null || view is null) return;
        var entries = view.Cast<SoundEntry>().ToArray();
        CategoryNameLabel.Text = selectedCategory switch { "*" or "" => library.Document.RootCategory.Name, _ => selectedCategory.Split('/').Last() };
        CategoryInfoIcon.Content=CategoryIcons.Create(CurrentCategory,CategoryNameLabel.Text,44,(Brush)FindResource("Accent"));
        CategorySoundCount.Text = entries.Length.ToString();
        SelectedCountLabel.Text = SoundGrid.SelectedItems.Count.ToString();
        CategoryPlayCount.Text = entries.Sum(x => (long)x.PlayCount).ToString();
    }
    private object CategoryHeader(string title, int count, bool home = false,CategoryEntry? iconCategory=null)
    {
        var panel=new Grid {HorizontalAlignment=HorizontalAlignment.Stretch};panel.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});panel.ColumnDefinitions.Add(new(){Width=new GridLength(30)});
        var number=new TextBlock {Text=count.ToString(),FontWeight=home?FontWeights.Bold:FontWeights.Normal,Foreground=(Brush)FindResource("Muted"),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};Grid.SetColumn(number,1);panel.Children.Add(number);
        var name=new DockPanel {LastChildFill=true};panel.Children.Add(name);
        var category=iconCategory??(home?library.Document.RootCategory:new CategoryEntry());var icon=CategoryIcons.Create(category,title,21,(Brush)FindResource("Accent"));icon.Margin=new Thickness(0,0,5,0);name.Children.Add(icon);
        name.Children.Add(new TextBlock {Text=title,FontWeight=home?FontWeights.Bold:FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});return panel;
    }

    private void RefreshPageButtons(int count, int current)
    {
        HotbarPageButtons.Children.Clear();
        int start = Math.Max(0, current - 4), end = Math.Min(count, start + 8);
        for (int page = start; page < end; page++)
        {
            int selected = page;
            var button = new Button { Content = (page+1).ToString(), Width = 24, Height = 23, Padding = new Thickness(0), Margin = new Thickness(1,2,1,2), BorderThickness = new Thickness(0), Foreground = (Brush)FindResource(page==current?"Palette_202020":"Accent"), FontSize=16,FontWeight=FontWeights.SemiBold, Background = (Brush)FindResource(page==current?"Accent":"Palette_353535") };
            button.Click += (_, _) => HotbarPage.SelectedIndex = selected;
            HotbarPageButtons.Children.Add(button);
        }
        var add=new Button {Content=new System.Windows.Shapes.Path {Data=Geometry.Parse("M6,0 H10 V6 H16 V10 H10 V16 H6 V10 H0 V6 H6 Z"),Fill=(Brush)FindResource("Accent"),Width=16,Height=16,Stretch=Stretch.Uniform},Width=25,Height=23,Margin=new Thickness(14,2,1,2),Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Color.FromRgb(44,44,44)),ToolTip="Add hotbar page"};
        System.Windows.Automation.AutomationProperties.SetName(add,"Add hotbar page");add.Click+=AddHotbarPage_Click;HotbarPageButtons.Children.Add(add);
    }
}
