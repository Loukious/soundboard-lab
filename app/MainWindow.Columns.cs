using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using SoundboardLab.Models;

namespace SoundboardLab;

public partial class MainWindow
{
    private string categorySortKey="";
    private ListSortDirection categorySortDirection;
    internal int SoundIndex(SoundEntry sound)=>library.Document.Entries.IndexOf(sound)+1;
    private object CategoryLayout()
    {
        var row=CategoryTree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
        var highlight=row?.Template.FindName("RowHighlight",row) as Border;
        var number=(row?.Header as Grid)?.Children.OfType<TextBlock>().FirstOrDefault();
        return new {PaneWidth=CategoryTree.ActualWidth,SelectionWidth=highlight?.ActualWidth??0,SelectionLeft=highlight?.TranslatePoint(new Point(),CategoryTree).X,
            CountCenter=number is null?0:number.TranslatePoint(new Point(number.ActualWidth/2,0),CategoryTree).X,
            CountHeaderCenter=CategoryCountHeader.TranslatePoint(new Point(CategoryCountHeader.ActualWidth/2,0),CategoryTree).X,
            HeaderWidth=CategoryNameHeader.ActualWidth+CategoryCountHeader.ActualWidth,DividerWidth=CategoryNameHeader.BorderThickness.Right};
    }

    private void SoundGrid_HotkeyCell_Click(object sender,MouseButtonEventArgs e)
    {
        if(VisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is not {} cell||cell.Column.SortMemberPath!="@Hotkey"||cell.DataContext is not SoundEntry sound)return;
        SoundGrid.SelectedItem=sound;e.Handled=true;
        Dispatcher.BeginInvoke(()=>Hotkey_Click(this,new RoutedEventArgs()));
    }
    private void SoundGrid_Sorting(object sender,DataGridSortingEventArgs e)
    {
        e.Handled=true;
        if(Settings.DisableSorting||view is not ListCollectionView list)return;
        var direction=e.Column.SortDirection==ListSortDirection.Ascending?ListSortDirection.Descending:ListSortDirection.Ascending;
        ApplySoundSort(e.Column,direction);
    }
    private void ApplySoundSort(DataGridColumn columnToSort,ListSortDirection direction)
    {
        if(view is not ListCollectionView list)return;
        var original=library.Document.Entries.Select((sound,index)=>(sound,index)).ToDictionary(x=>x.sound,x=>x.index);
        string field=columnToSort.SortMemberPath;
        var property=typeof(SoundEntry).GetProperty(field);
        var keys=library.Document.Entries.ToDictionary(sound=>sound,sound=>field switch {
            "@Index"=>(object)original[sound],"@Filename"=>Path.GetFileName(sound.FilePath),
            "@Hotkey"=>ShownHotkey(sound),_=>property?.GetValue(sound)
        });
        foreach(var column in SoundGrid.Columns)column.SortDirection=null;
        list.CustomSort=new SoundColumnComparer(keys,original,direction);
        columnToSort.SortDirection=direction;
    }
    private sealed class SoundColumnComparer(Dictionary<SoundEntry,object?> keys,Dictionary<SoundEntry,int> original,ListSortDirection direction):IComparer
    {
        public int Compare(object? left,object? right)
        {
            if(left is not SoundEntry a||right is not SoundEntry b)return 0;
            // Newly imported entries can arrive before the view is sorted again.
            if(!keys.TryGetValue(a,out var ka)||!keys.TryGetValue(b,out var kb))return 0;
            int result=ka is string sa&&kb is string sb?StringComparer.CurrentCultureIgnoreCase.Compare(sa,sb):Comparer.DefaultInvariant.Compare(ka,kb);
            if(result!=0)return direction==ListSortDirection.Descending?-Math.Sign(result):Math.Sign(result);
            return original[a].CompareTo(original[b]);
        }
    }
    private void RefreshSoundSorting(){var column=SoundGrid.Columns.FirstOrDefault(x=>x.SortDirection.HasValue);if(column?.SortDirection is {} direction)ApplySoundSort(column,direction);}
    private void ClearSoundSorting()
    {
        if(view is ListCollectionView list)list.CustomSort=null;
        view?.SortDescriptions.Clear();foreach(var column in SoundGrid.Columns)column.SortDirection=null;
    }
    private int CountCategorySounds(string category)=>library.Document.Entries.Count(x=>x.Category==category||x.Category.StartsWith(category+"/",StringComparison.Ordinal));
    private IEnumerable<CategoryEntry> OrderedCategories()
    {
        var categories=library.Document.Categories.OrderBy(x=>x.Name.Count(c=>c=='/'));
        return (categorySortKey,categorySortDirection) switch {
            ("Name",ListSortDirection.Ascending)=>categories.ThenBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase),
            ("Name",ListSortDirection.Descending)=>categories.ThenByDescending(x=>x.Name,StringComparer.CurrentCultureIgnoreCase),
            ("Count",ListSortDirection.Ascending)=>categories.ThenBy(x=>CountCategorySounds(x.Name)),
            ("Count",ListSortDirection.Descending)=>categories.ThenByDescending(x=>CountCategorySounds(x.Name)),_=>categories
        };
    }
    private void CategoryHeader_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button header)return;
        string key=header.Uid;
        categorySortDirection=categorySortKey==key&&categorySortDirection==ListSortDirection.Ascending?ListSortDirection.Descending:ListSortDirection.Ascending;
        categorySortKey=key;CategoryNameHeader.Tag=key=="Name"?categorySortDirection.ToString():null;CategoryCountHeader.Tag=key=="Count"?categorySortDirection.ToString():null;
        RefreshCategories();e.Handled=true;
    }
}
