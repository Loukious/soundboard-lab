using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed partial class SettingsWindow
{
    private void BuildRecorder(Panel page)
    {
        if(string.IsNullOrWhiteSpace(draft.RecordingFolder))draft.RecordingFolder=Path.Combine(main.StateDirectory,"recordings");
        var group=Group(page,"Recorder");Grid.SetIsSharedSizeScope(group,true);
        var path=PathField(group,"Save recordings in:",nameof(AppPreferences.RecordingFolder),true);
        var pathRow=(FrameworkElement)path.Parent;pathRow.Width=404;pathRow.HorizontalAlignment=HorizontalAlignment.Left;pathRow.Margin=new(0,0,0,8);
        void AlignValues(Grid row){row.ColumnDefinitions[0].SharedSizeGroup="RecorderLabels";row.ColumnDefinitions[1].SharedSizeGroup="RecorderValues";row.ColumnDefinitions[1].Width=new(166);}
        ComboBox Option(string label,string property,string[] values,string selected){var combo=Choice(group,label,values,selected,property,236);combo.Width=162;AlignValues((Grid)combo.Parent);return combo;}
        var format=Option("Save as:",nameof(AppPreferences.RecordingFormat),["M4A","MP3","WAV"],draft.RecordingFormat);
        var bitrate=Option("Bitrate:",nameof(AppPreferences.RecordingBitrate),["320 Kbps","256 Kbps","224 Kbps","192 Kbps","160 Kbps","128 Kbps","112 Kbps","96 Kbps"],draft.RecordingBitrate);
        void UpdateBitrate()=>((UIElement)bitrate.Parent).IsEnabled=format.SelectedItem as string=="MP3";
        format.SelectionChanged+=(_,_)=>UpdateBitrate();UpdateBitrate();
        Option("Add to category:",nameof(AppPreferences.RecordingCategory),new[]{"Selected category","My Sounds"}.Concat(main.CategoryNames).Distinct().ToArray(),draft.RecordingCategory);
        Option("Insert position in sound list:",nameof(AppPreferences.RecordingInsertPosition),["Beginning","End"],draft.RecordingInsertPosition);
        var normalizeRow=Row(group,"",236);AlignValues(normalizeRow);normalizeRow.Children.Clear();
        var normalize=Check(normalizeRow,"Automatically normalize recordings to:",nameof(AppPreferences.NormalizeRecordings));normalize.Margin=new(0,2,0,2);normalize.VerticalAlignment=VerticalAlignment.Center;
        var target=new ComboBox {IsEditable=true,Width=162,HorizontalAlignment=HorizontalAlignment.Left,ItemsSource=Enumerable.Range(0,17).Select(i=>(59+i*3)+".0 dB")};
        AutomationProperties.SetAutomationId(target,nameof(AppPreferences.RecordingTargetDb));
        target.SetBinding(ComboBox.TextProperty,new Binding(nameof(AppPreferences.RecordingTargetDb)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,Converter=new RecordingDbConverter(),ValidatesOnExceptions=true});
        Grid.SetColumn(target,1);normalizeRow.Children.Add(target);
        void UpdateNormalization()=>target.IsEnabled=normalize.IsChecked==true;
        normalize.Checked+=(_,_)=>UpdateNormalization();normalize.Unchecked+=(_,_)=>UpdateNormalization();UpdateNormalization();
        var trim=Check(group,"Trim silence",nameof(AppPreferences.TrimRecordingSilence));trim.Margin=new(2,4,0,4);
        var limit=Field(group,"Maximum recording time in seconds:",nameof(AppPreferences.RecordingLimitSeconds),162,236);
        limit.ToolTip="Set to 0 to record without limitation.";
        var limitRow=(Grid)limit.Parent;limitRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var duration=new TextBlock {Margin=new(8,0,0,0),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetAutomationId(duration,"RecordingDuration");Grid.SetColumn(duration,2);limitRow.Children.Add(duration);
        // Fix the value column so every recorder input begins at the same x coordinate.
        AlignValues(limitRow);
        void UpdateDuration(){if(int.TryParse(limit.Text,out int seconds)&&seconds>=0)duration.Text=seconds==0?"(∞)":$"({seconds/60}:{seconds%60:00})";else duration.Text="";}
        limit.TextChanged+=(_,_)=>UpdateDuration();UpdateDuration();
    }
    private void BuildEditor(Panel page)
    {
        var group=Group(page,"Editor");var external=Check(group,"Use external sound editor",nameof(AppPreferences.UseExternalEditor));
        var fields=new StackPanel();AutomationProperties.SetAutomationId(fields,"ExternalEditorFields");group.Children.Add(fields);
        var path=PathField(fields,"External sound editor:",nameof(AppPreferences.ExternalEditorPath));
        var pathRow=(FrameworkElement)path.Parent;pathRow.Width=404;pathRow.HorizontalAlignment=HorizontalAlignment.Left;pathRow.Margin=new(0,0,0,8);
        var parameters=Field(fields,"Param:",nameof(AppPreferences.ExternalEditorArguments),150,42);parameters.ToolTip="Use {file} for the selected sound file.";
        void UpdateEditor()=>fields.IsEnabled=external.IsChecked==true;
        external.Checked+=(_,_)=>UpdateEditor();external.Unchecked+=(_,_)=>UpdateEditor();UpdateEditor();
    }
}

internal sealed class RecordingDbConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)=>((double)value).ToString("0.0",culture)+" dB";
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)
    {
        string text=(value?.ToString()??"").Replace("dB","",StringComparison.OrdinalIgnoreCase).Trim();
        if(!double.TryParse(text,NumberStyles.Float,culture,out double db)||!double.IsFinite(db)||db is <42 or >112)throw new FormatException("Recording volume must be 42 to 112 dB.");return db;
    }
}
