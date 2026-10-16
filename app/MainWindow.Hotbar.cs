using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SoundboardLab;

public partial class MainWindow
{
    private void UpdateHotbarLock()
    {
        if(HotbarLockGlyph is null)return;
        HotbarLockGlyph.Data=Geometry.Parse(lockHotbar?"M4,7 V4 C4,0 12,0 12,4 V7 M2,7 H14 V16 H2 Z M8,10 V13":"M4,7 V4 C4,0 12,0 12,4 M2,7 H14 V16 H2 Z M8,10 V13");
        HotbarLockButton.ToolTip=lockHotbar?"Unlock hotbar":"Lock hotbar";
        System.Windows.Automation.AutomationProperties.SetName(HotbarLockButton,lockHotbar?"Unlock hotbar":"Lock hotbar");
        HotbarLockButton.Background=(Brush)FindResource(lockHotbar?"Palette_484848":"Palette_353535");
    }
    private void HotbarLock_Click(object sender,RoutedEventArgs e){lockHotbar=!lockHotbar;preferenceStore.Save();}
    private Action PopulateHotbarMenu(ItemsControl owner,bool main)
    {
        var show=Item(Settings.ShowHotbar?"Close hotbar":"Open hotbar",ToggleHotbar_Click);show.IsCheckable=true;
        if(main)HotbarMenu=show;
        var format=Item("Set hotbar format",(_,_)=>SetHotbarFormat());
        var navigation=Toggle("Show navigation",()=>HotbarPageButtons.Visibility==Visibility.Visible,x=>HotbarPageButtons.Visibility=x?Visibility.Visible:Visibility.Collapsed);
        var above=Toggle("Show pagination above",()=>paginationAbove,x=>{paginationAbove=x;var bar=HotbarPanel.Children.OfType<DockPanel>().Single();HotbarPanel.Children.Remove(bar);HotbarPanel.Children.Insert(x?0:HotbarPanel.Children.Count,bar);});
        var same=Toggle("Same colors on all pages",()=>Settings.SameHotbarColors,x=>{Settings.SameHotbarColors=x;RefreshHotbar();});
        var select=Toggle("Select sound file on playback",()=>Settings.SelectHotbarSound,x=>Settings.SelectHotbarSound=x);
        var locked=Toggle("Lock hotbar drag and drop",()=>lockHotbar,x=>{lockHotbar=x;preferenceStore.Save();});
        var remove=Item("Remove selected page",(_,_)=>RemoveHotbarPage());
        Add(owner,show,format,new Separator(),navigation,above,same,select,locked,new Separator(),remove);
        return ()=>{
            show.Header=Localization.Text(Settings.ShowHotbar?"Close hotbar":"Open hotbar");show.IsChecked=Settings.ShowHotbar;
            foreach(var item in new[]{format,navigation,above,same,select,locked})item.IsEnabled=Settings.ShowHotbar;
            remove.IsEnabled=Settings.ShowHotbar&&HotbarPage.Items.Count>1;
            foreach(var item in new[]{navigation,above,same,select,locked})if(item.Tag is Func<bool> read)item.IsChecked=read();
        };
    }
    private void HotbarOptions_Click(object sender,RoutedEventArgs e)
    {
        var menu=new ContextMenu {PlacementTarget=HotbarOptionsButton,Placement=PlacementMode.Top};
        HotbarOptionsButton.ContextMenu=menu;var refresh=PopulateHotbarMenu(menu,false);refresh();Localization.Apply(menu);
        menu.Opened+=(_,_)=>refresh();menu.IsOpen=true;
    }
}
