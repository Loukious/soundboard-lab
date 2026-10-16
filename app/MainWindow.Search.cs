using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundboardLab.Models;

namespace SoundboardLab;

public partial class MainWindow
{
    private SoundEntry[] SearchMatches()
    {
        string query=SearchBox.Text.Trim();if(query.Length==0)return [];
        var entries=view?.Cast<SoundEntry>()??[];
        if(int.TryParse(query,out int index)&&index>0)return entries.Where(x=>SoundIndex(x)==index).ToArray();
        if(query.Length<2)return [];
        return entries.Where(x=>(x.Title+" "+x.Artist+" "+x.MetadataTitle+" "+x.Tags+" "+x.FilePath).Contains(query,StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
    private void UpdateSearchResults()
    {
        if(SearchPreviousButton is null||SearchNextButton is null)return;
        var matches=SearchMatches();SearchPreviousButton.IsEnabled=SearchNextButton.IsEnabled=matches.Length>0;
        bool noMatch=SearchBox.Text.Trim().Length>=2&&matches.Length==0;
        SearchBox.BorderBrush=noMatch?new SolidColorBrush(Color.FromRgb(200,57,57)):(Brush)FindResource("Palette_555555");
        if(matches.Length>0&&!matches.Contains(Selected))SelectSearchMatch(matches[0]);
    }
    private void SelectSearchMatch(SoundEntry sound){SoundGrid.SelectedItem=sound;SoundGrid.ScrollIntoView(sound);}
    private void FindSearchMatch(int direction)
    {
        var matches=SearchMatches();if(matches.Length==0)return;
        int index=Array.IndexOf(matches,Selected);if(index<0)index=direction>0?-1:0;
        SelectSearchMatch(matches[(index+direction+matches.Length)%matches.Length]);
    }
    private void SearchNext_Click(object sender,RoutedEventArgs e)=>FindSearchMatch(1);
    private void SearchPrevious_Click(object sender,RoutedEventArgs e)=>FindSearchMatch(-1);
    private void ResetSearch(){SearchBox.Clear();SearchPanel.Visibility=Visibility.Collapsed;}
    private void SearchBox_KeyDown(object sender,KeyEventArgs e)
    {
        switch(e.Key){
            case Key.Enter:FindSearchMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?-1:1);break;
            case Key.Down:FindSearchMatch(1);break;
            case Key.Up:FindSearchMatch(-1);break;
            case Key.Escape:ResetSearch();SoundGrid.Focus();break;
            default:return;
        }
        e.Handled=true;
    }
}
