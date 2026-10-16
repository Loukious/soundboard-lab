using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private void SetSpecialHotkey(ListView table)
    {
        if(table.SelectedItem is not HotkeyRow row)return;
        var assignments=hotkeyRows.Select(x=>new HotkeyAssignment("action:"+x.Action,()=>x.Hotkey,value=>x.Hotkey=value,true));
        if(hotkeyEditor(this,row.Hotkey,"action:"+row.Action,assignments,draft.AllowLeftRightModifiers) is not {} key)return;
        row.Hotkey=key;table.Items.Refresh();Dirty();
    }
    private void SpecialHotkey_Pressed(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left||e.ClickCount<2||e.ClickCount%2!=0||sender is not ListView table)return;
        if(ItemsControl.ContainerFromElement(table,e.OriginalSource as DependencyObject) is not ListViewItem item||item.Content is not HotkeyRow)return;
        table.SelectedItem=item.Content;e.Handled=true;SetSpecialHotkey(table);
    }
    private static void ConfigureActionIcons(ListView table)
    {
        var column=((GridView)table.View).Columns[0];column.DisplayMemberBinding=null;
        var heading=new Style(typeof(GridViewColumnHeader));heading.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Left));table.Resources[typeof(GridViewColumnHeader)]=heading;
        var cell=new DataTemplate();var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
        var icon=new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));icon.SetValue(FrameworkElement.WidthProperty,15d);icon.SetValue(FrameworkElement.HeightProperty,15d);icon.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,3,0));icon.SetValue(System.Windows.Shapes.Shape.StretchProperty,Stretch.Uniform);icon.SetValue(System.Windows.Shapes.Shape.FillProperty,new SolidColorBrush(Color.FromRgb(0,103,171)));
        icon.SetBinding(System.Windows.Shapes.Path.DataProperty,new Binding(nameof(HotkeyRow.Action)){Converter=new SpecialActionIconConverter()});icon.SetBinding(UIElement.VisibilityProperty,new Binding(nameof(HotkeyRow.Action)){Converter=new SpecialActionIconConverter(),ConverterParameter="Visibility"});panel.AppendChild(icon);
        var text=new FrameworkElementFactory(typeof(TextBlock));text.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);text.SetBinding(TextBlock.TextProperty,new Binding(nameof(HotkeyRow.Title)){Converter=new LocalizedTextConverter()});panel.AppendChild(text);cell.VisualTree=panel;column.CellTemplate=cell;
        table.SizeChanged+=(_,_)=>((GridView)table.View).Columns[1].Width=Math.Max(100,table.ActualWidth-column.Width-22);
    }
    private void ModifierRow(Panel parent,string label,string property,bool numcode)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new(190)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});parent.Children.Add(row);
        var enabled=Check(row,label,property);enabled.VerticalAlignment=VerticalAlignment.Center;var change=Button("Change modifier",(_,_)=>ChangeModifier(numcode));change.Width=110;change.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(change,1);row.Children.Add(change);
    }
    private void ChangeModifier(bool numcode)
    {
        var dialog=new Window {Title="Change modifier",Owner=this,Width=370,Height=numcode?230:165,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=SystemColors.ControlBrush};
        dialog.Resources.MergedDictionaries.Add(new(){Source=new Uri("/SoundboardLab;component/SettingsResources.xaml",UriKind.Relative)});
        var panel=new StackPanel {Margin=new(16)};dialog.Content=panel;panel.Children.Add(new TextBlock {Text="Modifier keys (Ctrl, Alt, Shift, Win)",Margin=new(2,0,0,6)});
        var input=new TextBox {Text=numcode?draft.NumcodeModifier:draft.IndexModifier};panel.Children.Add(input);
        var category=new CheckBox {Content="Enable category index",IsChecked=draft.NumcodeCategoryIndex};var digits=new CheckBox {Content="Two digits",IsChecked=draft.NumcodeTwoDigits};if(numcode){panel.Children.Add(category);panel.Children.Add(digits);}
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,10,0,0)};panel.Children.Add(buttons);
        buttons.Children.Add(Button("OK",(_,_)=>{try{string value=GlobalHotkeyService.NormalizeGesture(input.Text+"+F24");value=value[..^4];if(value.Length==0)throw new ArgumentException("Select at least one modifier.");if(numcode){draft.NumcodeModifier=value;draft.NumcodeCategoryIndex=category.IsChecked==true;draft.NumcodeTwoDigits=digits.IsChecked==true;}else draft.IndexModifier=value;Dirty();dialog.DialogResult=true;}catch(Exception ex){Dialogs.Error(dialog,ex);}}));
        var cancel=Button("Cancel",(_,_)=>dialog.Close());cancel.IsCancel=true;buttons.Children.Add(cancel);dialog.ShowDialog();
    }
}

internal sealed class SpecialActionIconConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)
    {
        string? shape=(value as string) switch {
            "forwardingToggle" or "forwardingPush"=>"M0,4 H8 V0 L16,8 8,16 V12 H0 Z",
            "stop" or "recordEnd"=>"M0,0 H16 V16 H0 Z",
            "play" or "playSelected" or "playCurrent" or "playPreviouslyPlayed" or "playModeDefault"=>"M1,0 L16,8 1,16 Z",
            "pauseResume"=>"M1,0 H6 V16 H1 Z M10,0 H15 V16 H10 Z",
            "playPrevious"=>"M0,0 H3 V16 H0 Z M16,0 L4,8 16,16 Z",
            "playNext"=>"M13,0 H16 V16 H13 Z M0,0 L12,8 0,16 Z",
            "recordBegin" or "recordBeginSpeakers" or "recordBeginMicrophone"=>"M8,0 A8,8 0 1 1 7.99,0 Z",
            "playModeSpeakers" or "playTtsRender"=>"M1,15 V7 C1,-2 15,-2 15,7 V15 H11 V7 H13 C13,0 3,0 3,7 H5 V15 Z",
            "playModeMicrophone" or "playTtsCapture"=>"M5,1 C5,-1 11,-1 11,1 V8 C11,12 5,12 5,8 Z M2,7 H4 V9 C4,14 12,14 12,9 V7 H14 V9 C14,13 12,15 9,15 V17 H12 V19 H4 V17 H7 V15 C4,15 2,13 2,9 Z",
            _=>null};
        return parameter as string=="Visibility"?(shape is null?Visibility.Collapsed:Visibility.Visible):shape is null?Geometry.Empty:Geometry.Parse(shape);
    }
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
