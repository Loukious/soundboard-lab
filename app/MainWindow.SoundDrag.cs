using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoundboardLab.Models;

namespace SoundboardLab;

public partial class MainWindow
{
    private Point soundDragOrigin;
    private Guid? soundDragId;
    private void PrepareSoundDrag(DependencyObject? source,Point position,int clickCount)
    {
        soundDragId=null;
        // Headers and their resize thumbs share the DataGrid's mouse events.
        // Only a press in a sound cell may start the library's file drag.
        if(clickCount!=1||paintingSoundColor is not null||VisualAncestor<DataGridCell>(source) is not {} cell
            ||cell.Column.SortMemberPath=="@Hotkey"||cell.DataContext is not SoundEntry sound)return;
        soundDragOrigin=position;soundDragId=sound.Id;
    }
    private SoundEntry? TakeSoundDrag(Point position,MouseButtonState leftButton)
    {
        if(leftButton!=MouseButtonState.Pressed){soundDragId=null;return null;}
        if(soundDragId is not {} id||(position-soundDragOrigin).Length<=10)return null;
        // Clear before entering the native drag loop so reentrant mouse moves
        // cannot start another drag. Resolve the pressed sound, not a later selection.
        soundDragId=null;
        return library.Document.Entries.FirstOrDefault(x=>x.Id==id);
    }
}
