using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SoundboardLab;

public partial class MainWindow
{
    private void ConfigurePlayModeMenu()
    {
        var menu=new ContextMenu {PlacementTarget=ModeButton,Placement=PlacementMode.MousePoint};
        var choices=new[]{("Set play mode to default (speakers and microphone)","PlayIcon"),("Set play mode to speakers","HeadphonesIcon"),("Set play mode to microphone","MicIcon")};
        for(int i=0;i<choices.Length;i++){
            int mode=i;var item=Item(choices[i].Item1,(_,_)=>DestinationBox.SelectedIndex=mode);
            item.Icon=ModeMenuIcon((Geometry)FindResource(choices[i].Item2));menu.Items.Add(item);
        }
        Add(menu,new Separator(),Item("Preferences",Settings_Click));
        menu.Opened+=(_,_)=>{for(int i=0;i<3;i++)((MenuItem)menu.Items[i]).IsEnabled=DestinationBox.SelectedIndex!=i;Localization.Apply(menu);};
        ModeButton.ContextMenu=menu;
    }
    private System.Windows.Shapes.Path ModeMenuIcon(Geometry shape)=>new(){Data=shape,Fill=(Brush)FindResource("Accent"),Width=15,Height=15,Stretch=Stretch.Uniform};
    private void OpenPlayModeMenu_Click(object sender,RoutedEventArgs e)
    {
        if(ModeButton.ContextMenu is {} menu){menu.PlacementTarget=ModeButton;menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;}
    }
}
