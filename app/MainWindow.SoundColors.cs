using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundboardLab.Models;

namespace SoundboardLab;

internal static class SoundColors
{
    internal const string Default = "#334155";
    internal static bool TryColor(string? value, out Color color)
    {
        color=default;
        if(string.IsNullOrWhiteSpace(value)||value.Equals(Default,StringComparison.OrdinalIgnoreCase))return false;
        try { color=(Color)ColorConverter.ConvertFromString(value);return true; }
        catch(Exception ex) when(ex is FormatException or NotSupportedException){return false;}
    }
    internal static Border Swatch(string value,double size=16) => new() {
        Width=size,Height=size,CornerRadius=new(1),BorderThickness=new(1),BorderBrush=new SolidColorBrush(Color.FromRgb(110,110,110)),
        Background=TryColor(value,out var color)?new SolidColorBrush(color):Brushes.Transparent
    };
}

public partial class MainWindow
{
    private Window? soundColorTool;
    private string? paintingSoundColor;
    private IReadOnlyList<string> SoundColorSlots()
    {
        var slots=Settings.SoundColorSlots;
        if(slots is null||slots.Count!=24){var defaults=SoundColorPalette.CreateDefaultSlots();if(slots is not null)for(int i=0;i<Math.Min(24,slots.Count);i++)defaults[i]=slots[i];Settings.SoundColorSlots=slots=defaults;}
        for(int i=0;i<slots.Count;i++)slots[i]??="";
        return slots;
    }
    private void PopulateSoundColorMenu(MenuItem menu,Func<IEnumerable<SoundEntry>> selection)
    {
        menu.Items.Clear();menu.Items.Add(Item("Open color tool",Color_Click));menu.Items.Add(new Separator());
        var slots=SoundColorSlots();
        for(int i=0;i<slots.Count;i++){
            string value=slots[i];if(!SoundColors.TryColor(value,out _))continue;
            var item=Item("Color "+(i+1),(_,_)=>ApplySoundColor(selection(),value));item.Icon=SoundColors.Swatch(value);item.Tag=value;menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());menu.Items.Add(Item("Reset color",(_,_)=>ApplySoundColor(selection(),SoundColors.Default)));
    }
    private void ApplySoundColor(IEnumerable<SoundEntry> entries,string value)
    {
        foreach(var entry in entries.ToArray())entry.Color=value;
    }
    private void SetSoundColorSlot(int index,string value)
    {
        if(index is <0 or >=24)throw new ArgumentOutOfRangeException(nameof(index));
        _=SoundColorSlots();Settings.SoundColorSlots[index]=value;preferenceStore.Save();
    }
    private Window CreateSoundColorTool() => new SoundColorToolWindow(SoundColorSlots,SetSoundColorSlot,value=>{
        paintingSoundColor=value;SoundGrid.Cursor=value is null?Cursors.Arrow:Cursors.Pen;
    }) {Owner=this};
    private void OpenSoundColorTool()
    {
        if(soundColorTool is not null){soundColorTool.Activate();return;}
        soundColorTool=CreateSoundColorTool();soundColorTool.Closed+=(_,_)=>{paintingSoundColor=null;SoundGrid.ClearValue(CursorProperty);soundColorTool=null;};soundColorTool.Show();
    }
    private bool PaintSoundRow(MouseButtonEventArgs e)
    {
        if(paintingSoundColor is null||e.ChangedButton!=MouseButton.Left||VisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not SoundEntry sound)return false;
        ApplySoundColor([sound],paintingSoundColor);e.Handled=true;return true;
    }
}
