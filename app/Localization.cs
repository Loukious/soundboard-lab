using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace SoundboardLab;

internal static class Localization
{
    public static string[] Languages {get;} = ["System default", "Arabic (ar)", "Bulgarian (Bulgaria) (bg_BG)", "Chinese (Simplified, China) (zh_CN)", "Croatian (Croatia) (hr_HR)", "Czech (cs)", "Danish (da)", "Dutch (nl)", "English (en_US)", "Estonian (Estonia) (et_EE)", "Finnish (fi)", "French (fr)", "German (de)", "Greek (Greece) (el_GR)", "Hebrew (Israel) (he_IL)", "Hindi (India) (hi_IN)", "Hungarian (Hungary) (hu_HU)", "Indonesian (Indonesia) (id_ID)", "Italian (it)", "Japanese (ja)", "Korean (ko)", "Latvian (Latvia) (lv_LV)", "Lithuanian (Lithuania) (lt_LT)", "Malay (Malaysia) (ms_MY)", "Norwegian Bokmål (Norway) (nb_NO)", "Polish (pl)", "Portuguese (Brazil) (pt_BR)", "Romanian (Romania) (ro_RO)", "Russian (ru)", "Serbian (Serbia) (sr_RS)", "Slovak (Slovakia) (sk_SK)", "Slovenian (Slovenia) (sl_SI)", "Spanish (Spain, International Sort) (es_ES)", "Swedish (sv)", "Thai (th)", "Turkish (tr)", "Ukrainian (uk)", "Vietnamese (Vietnam) (vi_VN)"];
    private static readonly CultureInfo SystemCulture=CultureInfo.CurrentUICulture;
    private static Dictionary<string,string> catalog=new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<DependencyObject,Dictionary<DependencyProperty,string>> originals=new();
    public static void Load(string language)
    {
        catalog.Clear();string code=language=="System default"?SystemCulture.Name.Replace('-','_'):language[(language.LastIndexOf('(')+1)..].TrimEnd(')');
        if(!System.Text.RegularExpressions.Regex.IsMatch(code,@"^[a-zA-Z]{2,3}(?:_[a-zA-Z]{2})?$"))return;
        try{var culture=CultureInfo.GetCultureInfo(code.Replace('_','-'));CultureInfo.CurrentCulture=culture;CultureInfo.CurrentUICulture=culture;}catch(CultureNotFoundException){}
        string root=Path.Combine(AppContext.BaseDirectory,"languages");string file=Path.Combine(root,code,"translation.mo");
        if(!File.Exists(file))file=Path.Combine(root,code.Split('_')[0],"translation.mo");if(!File.Exists(file))return;
        byte[] bytes=File.ReadAllBytes(file);if(bytes.Length<28)return;bool big=BinaryPrimitives.ReadUInt32LittleEndian(bytes)==0xde120495;
        uint Read(int position){if(position<0||position>bytes.Length-4)throw new InvalidDataException("Invalid translation table.");return big?BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position)):BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position));}
        int count=checked((int)Read(8)),from=checked((int)Read(12)),to=checked((int)Read(16));if(count>100000)throw new InvalidDataException("Invalid translation count.");
        string Entry(int table,int index){int length=checked((int)Read(table+index*8)),offset=checked((int)Read(table+index*8+4));if(offset<0||length<0||offset>bytes.Length-length)throw new InvalidDataException("Invalid translation offset.");return Encoding.UTF8.GetString(bytes,offset,length).Split('\0')[0];}
        for(int i=0;i<count;i++){string key=Entry(from,i).Split('\u0004').Last(),value=Entry(to,i);if(key.Length>0&&value.Length>0){catalog[key]=value;catalog.TryAdd(Normalize(key),value);}}
    }
    private static string Normalize(string text)=>text.Replace("&","").Replace("_","").Trim().TrimEnd(':','.','…').Trim();
    public static string Text(string value)
    {
        if(catalog.TryGetValue(value,out var translated)||catalog.TryGetValue(Normalize(value),out translated))return translated.Replace('&','_');return value;
    }
    public static void Apply(DependencyObject root)
    {
        var seen=new HashSet<DependencyObject>();Visit(root);
        void Translate(DependencyObject item,DependencyProperty property){if(BindingOperations.IsDataBound(item,property))return;if(item.GetValue(property) is not string current)return;var record=originals.GetOrCreateValue(item);if(!record.TryGetValue(property,out string? original))record[property]=original=current;item.SetCurrentValue(property,Text(original));}
        void Visit(DependencyObject item){if(!seen.Add(item))return;
            if(item is MenuItem menu){Translate(menu,HeaderedItemsControl.HeaderProperty);foreach(var child in menu.Items.OfType<DependencyObject>())Visit(child);}
            else if(item is HeaderedContentControl header)Translate(header,HeaderedContentControl.HeaderProperty);
            else if(item is ContentControl control&&control is not ListViewItem&&control is not DataGridCell)Translate(control,ContentControl.ContentProperty);
            if(item is TextBlock block){if(block.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Converter is LocalizedTextConverter)block.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();else if(!HasDataAncestor(block)&&!IsGeneratedText(block)&&block.Name is not ("CategoryNameLabel" or "CategorySoundCount" or "SelectedCountLabel" or "CategoryPlayCount" or "TimeLabel" or "NowPlaying" or "MicStatus" or "LibraryCount" or "StatusText"))Translate(block,TextBlock.TextProperty);}
            if(item is FrameworkElement element)Translate(element,FrameworkElement.ToolTipProperty);
            if(item is DataGrid grid)foreach(var column in grid.Columns)Translate(column,DataGridColumn.HeaderProperty);
            if(item is ListView list&&list.View is GridView columns)foreach(var column in columns.Columns)Translate(column,GridViewColumn.HeaderProperty);
            foreach(var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())Visit(child);
            if(item is Visual)for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)Visit(VisualTreeHelper.GetChild(item,i));
        }
    }
    private static bool HasDataAncestor(DependencyObject child){for(var parent=VisualTreeHelper.GetParent(child);parent is not null;parent=VisualTreeHelper.GetParent(parent))if(parent is DataGridCell or TreeViewItem or ListViewItem)return true;return false;}
    // AccessText renders mnemonic text with generated Runs. Writing its internal
    // TextBlock.Text clears those Runs even though MenuItem.Header remains intact.
    // Translate the owning Header/Content instead of touching template internals.
    private static bool IsGeneratedText(TextBlock block){if(block.TemplatedParent is not null)return true;for(var parent=VisualTreeHelper.GetParent(block);parent is not null;parent=VisualTreeHelper.GetParent(parent))if(parent is AccessText)return true;return false;}
}

internal sealed class LocalizedTextConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)=>Localization.Text(value?.ToString()??"");
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>Binding.DoNothing;
}

internal sealed class VoiceDbConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)=>((double)value+112).ToString("0.#",culture)+" dB";
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture){string text=(value?.ToString()??"").Replace("dB","",StringComparison.OrdinalIgnoreCase).Trim();if(!double.TryParse(text,System.Globalization.NumberStyles.Float,culture,out double db)||!double.IsFinite(db)||db is <42 or >112)throw new FormatException("Voice volume must be 42 to 112 dB.");return db-112;}
}
