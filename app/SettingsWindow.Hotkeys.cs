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
        var assignments=hotkeyRows.Select(x=>new HotkeyAssignment("action:"+x.Action,()=>x.Hotkey,value=>x.Hotkey=value,true)).ToArray();
        if(hotkeyEditor(this,row.Hotkey,"action:"+row.Action,assignments,draft.AllowLeftRightModifiers) is not {} key)return;
        HotkeyAssignments.ClearConflicts(assignments,"action:"+row.Action,key,draft.AllowLeftRightModifiers);
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
        var icon=new FrameworkElementFactory(typeof(SpecialActionIcon));icon.SetValue(FrameworkElement.WidthProperty,16d);icon.SetValue(FrameworkElement.HeightProperty,16d);icon.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,3,0));icon.SetBinding(SpecialActionIcon.ActionProperty,new Binding(nameof(HotkeyRow.Action)));panel.AppendChild(icon);
        var text=new FrameworkElementFactory(typeof(TextBlock));text.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);text.SetBinding(TextBlock.TextProperty,new Binding(nameof(HotkeyRow.Title)){Converter=new LocalizedTextConverter()});panel.AppendChild(text);cell.VisualTree=panel;column.CellTemplate=cell;
        table.SizeChanged+=(_,_)=>((GridView)table.View).Columns[2].Width=Math.Max(100,table.ActualWidth-column.Width-40-22);
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

