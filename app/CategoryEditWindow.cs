using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

internal static class CategoryIcons
{
    internal static readonly Dictionary<string,string> Stock=new(){
        ["Home"]="M0,8 L8,0 16,8 H13 V16 H10 V11 H6 V16 H3 V8 Z",
        ["Folder"]="M0,2 H6 L8,4 H16 V14 H0 Z",
        ["Music"]="M6,2 L15,0 V12 C15,17 8,17 8,13 C8,10 12,10 13,11 V4 L8,5 V14 C8,19 1,19 1,15 C1,12 5,12 6,13 Z",
        ["Star"]="M8,0 L10,5 16,6 12,10 13,16 8,13 3,16 4,10 0,6 6,5 Z",
        ["Heart"]="M8,15 L1,8 C-4,1 5,-3 8,3 C11,-3 20,1 15,8 Z",
        ["Play"]="M2,0 L16,8 2,16 Z"};
    internal static FrameworkElement Create(CategoryEntry category,string label,double size,Brush fallback)
    {
        Brush color=fallback;try{if(category.IconColor.Length>0)color=new SolidColorBrush((Color)ColorConverter.ConvertFromString(category.IconColor));}catch(FormatException){}
        if(category.IconMode=="Custom"&&File.Exists(category.IconPath)){
            try{using var stream=File.OpenRead(category.IconPath);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return new Image {Source=bitmap,Width=size,Height=size,Stretch=Stretch.Uniform};}catch(Exception ex)when(ex is IOException or NotSupportedException or FileFormatException){}
        }
        if(category.IconMode=="Generated")return new Border {Width=size,Height=size,Background=color,CornerRadius=new(2),Child=new TextBlock {Text=label.Length>0?label[..1].ToUpperInvariant():"?",Foreground=Brushes.White,FontWeight=FontWeights.Bold,FontSize=size*.65,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
        return new System.Windows.Shapes.Path {Data=Geometry.Parse(Stock.GetValueOrDefault(category.StockIcon,Stock["Folder"])),Fill=color,Width=size,Height=size,Stretch=Stretch.Uniform};
    }
}

internal sealed class CategoryEditWindow : Window
{
    internal CategoryEntry Value {get;}
    public CategoryEditWindow(CategoryEntry category, Func<Window,string,string?> editHotkey)
    {
        Value=JsonSerializer.Deserialize<CategoryEntry>(JsonSerializer.Serialize(category))!;
        Title="Edit category";Width=386;Height=345;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=SystemColors.ControlBrush;
        Resources.MergedDictionaries.Add(new(){Source=new Uri("/SoundboardLab;component/SettingsResources.xaml",UriKind.Relative)});
        var root=new DockPanel {Margin=new(10,12,10,10)};Content=root;
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();root.Children.Add(body);
        StackPanel Group(string title){var panel=new StackPanel();body.Children.Add(new GroupBox {Header=new TextBlock {Text=title,FontWeight=FontWeights.Bold,FontSize=16,Foreground=Brushes.Black},Margin=new(0,0,0,10),Padding=new(8,5,8,7),Content=panel});return panel;}
        var group=Group("Name");var name=new TextBox {Text=Value.Name.Split('/').Last()};AutomationProperties.SetAutomationId(name,"CategoryEditName");group.Children.Add(name);
        group=Group("Icon");var icons=new Grid();icons.ColumnDefinitions.Add(new(){Width=new(74)});icons.ColumnDefinitions.Add(new(){Width=new(54)});icons.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});group.Children.Add(icons);
        var modes=new StackPanel();icons.Children.Add(modes);var image=new ContentControl {Width=48,Height=48};Grid.SetColumn(image,1);icons.Children.Add(image);
        var choices=new StackPanel();Grid.SetColumn(choices,2);icons.Children.Add(choices);
        void Preview()=>image.Content=CategoryIcons.Create(Value,name.Text,48,(Brush)Application.Current.FindResource("Accent"));
        Button browse=new(){Content="Browse",HorizontalAlignment=HorizontalAlignment.Left};choices.Children.Add(browse);
        var palette=CategoryColors.All.ToList();if(Value.IconColor.Length>0&&!palette.Any(x=>x.Value.Equals(Value.IconColor,StringComparison.OrdinalIgnoreCase)))palette.Add(new("Custom color",Value.IconColor));
        var color=new ComboBox {ItemsSource=palette,SelectedItem=palette.FirstOrDefault(x=>x.Value.Equals(Value.IconColor,StringComparison.OrdinalIgnoreCase))??palette[0],ItemTemplate=CategoryColors.Template(),MinWidth=0,MaxDropDownHeight=252};AutomationProperties.SetAutomationId(color,"CategoryIconColor");choices.Children.Add(color);
        foreach(string mode in new[]{"Generated","Stock","Custom"}){var radio=new RadioButton {Content=mode,GroupName="CategoryIconMode",IsChecked=Value.IconMode==mode};AutomationProperties.SetAutomationId(radio,"CategoryIcon"+mode);radio.Checked+=(_,_)=>{Value.IconMode=mode;color.IsEnabled=mode!="Custom";browse.IsEnabled=mode!="Generated";Preview();};modes.Children.Add(radio);}
        color.SelectionChanged+=(_,_)=>{if(color.SelectedItem is CategoryColor selected)Value.IconColor=selected.Value;Preview();};
        browse.Click+=(_,_)=>{if(Value.IconMode=="Custom"){var file=new OpenFileDialog {Filter="Images|*.png;*.ico;*.jpg;*.jpeg;*.bmp"};if(file.ShowDialog(this)==true){Value.IconPath=file.FileName;Preview();}}else{var picker=new Window {Title="Choose icon",Owner=this,Width=300,Height=210,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=SystemColors.ControlBrush};picker.Resources.MergedDictionaries.Add(new(){Source=new Uri("/SoundboardLab;component/SettingsResources.xaml",UriKind.Relative)});var grid=new System.Windows.Controls.Primitives.UniformGrid {Columns=3,Margin=new(12)};picker.Content=grid;foreach(string stock in CategoryIcons.Stock.Keys){var button=new Button {Content=CategoryIcons.Create(new(){StockIcon=stock,IconColor=Value.IconColor},stock,32,(Brush)Application.Current.FindResource("Accent")),ToolTip=stock,MinWidth=0};button.Click+=(_,_)=>{Value.StockIcon=stock;picker.Close();Preview();};grid.Children.Add(button);}picker.ShowDialog();}};
        name.TextChanged+=(_,_)=>Preview();Preview();browse.IsEnabled=Value.IconMode!="Generated";color.IsEnabled=Value.IconMode!="Custom";
        group=Group("Misc");var hotkeys=new StackPanel {Orientation=Orientation.Horizontal};group.Children.Add(hotkeys);hotkeys.Children.Add(new TextBlock {Text="Play random file:",VerticalAlignment=VerticalAlignment.Center});var key=new TextBlock {Text=Value.Hotkey.Length>0?Value.Hotkey:"None",VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,4,0)};hotkeys.Children.Add(key);var assign=new Button {Content="Set hotkey",MinWidth=77,Padding=new(4,1,4,1)};hotkeys.Children.Add(assign);assign.Click+=(_,_)=>{if(editHotkey(this,Value.Hotkey) is {} gesture){Value.Hotkey=gesture;key.Text=gesture.Length>0?gesture:"None";}};
        var ok=new Button {Content="OK",IsDefault=true};ok.Click+=(_,_)=>{string text=name.Text.Trim();if(text.Length==0||text.Contains('/')){Dialogs.Error(this,new ArgumentException("Enter a category name without '/'."));return;}Value.Name=text;DialogResult=true;};footer.Children.Add(ok);
        var cancel=new Button {Content="Cancel",IsCancel=true};footer.Children.Add(cancel);Loaded+=(_,_)=>{name.Focus();name.SelectAll();};
    }
}
