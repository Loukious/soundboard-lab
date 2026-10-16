using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SoundboardLab;

internal static class VolumeSliderFeedback
{
    internal static void Attach(Slider slider,Func<double,double> gain)
    {
        var popup=new ToolTip {Placement=PlacementMode.Top,PlacementTarget=slider,StaysOpen=true};slider.ToolTip=popup;
        void Update(){popup.Content=Math.Round(gain(slider.Value)*100).ToString("0",CultureInfo.CurrentCulture)+"%";if(slider.Template?.FindName("PART_Track",slider) is Track {Thumb:{} thumb})popup.PlacementTarget=thumb;}
        slider.ValueChanged+=(_,_)=>Update();slider.Loaded+=(_,_)=>Update();Update();
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler((_,_)=>{Update();popup.IsOpen=slider.IsEnabled;}),true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler((_,_)=>popup.IsOpen=false),true);
        slider.LostMouseCapture+=(_,_)=>popup.IsOpen=false;slider.Unloaded+=(_,_)=>popup.IsOpen=false;
    }
}
