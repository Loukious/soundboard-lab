using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SoundboardLab.Models;

namespace SoundboardLab;

internal sealed class SoundColorToolWindow : Window
{
    internal SoundColorToolWindow(Func<IReadOnlyList<string>> read,Action<int,string> write,Action<string?> paint)
    {
        Title="Color tool";Width=354;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new StackPanel {Margin=new(8)};Content=root;
        var tools=new StackPanel {Orientation=Orientation.Horizontal};root.Children.Add(tools);
        Dialogs.Button(tools,"Stop coloring",(_,_)=>paint(null)).ToolTip="Stop coloring";
        Dialogs.Button(tools,"Erase tool",(_,_)=>paint(SoundColors.Default)).ToolTip="Click a sound to reset its color";
        var grid=new UniformGrid {Columns=6,Margin=new(0,5,0,5)};root.Children.Add(grid);
        void Refresh(){
            grid.Children.Clear();var colors=read();
            for(int index=0;index<24;index++){
                int slot=index;string value=colors[slot];
                var button=new Button {Content=SoundColors.Swatch(value,28),Width=45,Height=39,Padding=new(4),Margin=new(3),ToolTip="Color "+(slot+1)+(value.Length==0?" (empty)":" · "+value)};
                AutomationProperties.SetAutomationId(button,"SoundColorSlot"+(slot+1));
                void Edit(){if(Dialogs.Prompt(this,"Set color "+(slot+1),"Hex color",value.Length==0?"#FFFFFF":value) is not {} chosen)return;if(!SoundColors.TryColor(chosen,out _)){Dialogs.Error(this,new ArgumentException("Enter a color such as #34A0D6."));return;}write(slot,chosen);Refresh();}
                button.Click+=(_,_)=>{if(value.Length==0)Edit();else paint(value);};
                var menu=new ContextMenu();var edit=new MenuItem {Header="Set color..."};edit.Click+=(_,_)=>Edit();menu.Items.Add(edit);
                var clear=new MenuItem {Header="Clear color"};clear.Click+=(_,_)=>{write(slot,"");Refresh();};menu.Items.Add(clear);button.ContextMenu=menu;grid.Children.Add(button);
            }
        }
        Refresh();
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};root.Children.Add(footer);
        Dialogs.Button(footer,"Reset palette",(_,_)=>{var defaults=SoundColorPalette.CreateDefaultSlots();for(int i=0;i<24;i++)write(i,defaults[i]);Refresh();});
        Dialogs.Button(footer,"Close color tool",(_,_)=>Close());
    }
}
