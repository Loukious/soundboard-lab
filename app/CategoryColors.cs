using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SoundboardLab;

internal sealed record CategoryColor(string Name,string Value)
{
    public Brush Swatch {
        get {
            var color=Value.Length>0?(Color)ColorConverter.ConvertFromString(Value):(Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color??Color.FromRgb(0,103,171);
            return new LinearGradientBrush(color,Color.FromRgb((byte)(color.R*.8),(byte)(color.G*.8),(byte)(color.B*.8)),90);
        }
    }
    public override string ToString()=>Name;
}

internal static class CategoryColors
{
    internal static readonly CategoryColor[] All=[
        new("Default icon color",""),new("Light blue","#2AA8CF"),new("Blue","#0067AB"),new("Navy","#034C70"),new("Teal","#2F9D88"),
        new("Light green","#90BB47"),new("Green","#42AF57"),new("Dark green","#34763C"),new("Light orange","#F6AE44"),new("Orange","#F17C22"),new("Dark orange","#D34E18"),
        new("Light brown","#C6A777"),new("Brown","#994F00"),new("Beige","#DDD8B9"),new("Yellow","#F4EB28"),new("Pink","#F9A5C4"),new("Magenta","#FF00FF"),new("Dark magenta","#BC0076"),
        new("Light purple","#D37CFF"),new("Purple","#7300AB"),new("Red","#D40000"),new("Dark red","#AF0000"),new("Black","#000000"),new("White","#FFFFFF"),
        new("Silver","#BFBFBF"),new("Gray","#999999"),new("Dark gray","#666666"),new("Jet","#333333")];
    internal static DataTemplate Template()
    {
        var template=new DataTemplate(typeof(CategoryColor));var row=new FrameworkElementFactory(typeof(StackPanel));row.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
        var swatch=new FrameworkElementFactory(typeof(Border));swatch.SetValue(FrameworkElement.WidthProperty,22d);swatch.SetValue(FrameworkElement.HeightProperty,22d);swatch.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));swatch.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,6,0));swatch.SetBinding(Border.BackgroundProperty,new Binding(nameof(CategoryColor.Swatch)));row.AppendChild(swatch);
        var label=new FrameworkElementFactory(typeof(TextBlock));label.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);label.SetBinding(TextBlock.TextProperty,new Binding(nameof(CategoryColor.Name)){Converter=new LocalizedTextConverter()});row.AppendChild(label);template.VisualTree=row;return template;
    }
}
