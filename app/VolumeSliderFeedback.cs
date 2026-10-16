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
        var popup=new ToolTip {Placement=PlacementMode.RelativePoint,PlacementTarget=slider,StaysOpen=true};
        popup.SetResourceReference(FrameworkElement.StyleProperty,"VolumeFeedbackToolTip");slider.ToolTip=popup;
        ToolTipService.SetIsEnabled(slider,false); // Percentage feedback belongs to a drag, not a hover timer.
        void Update()=>popup.Content=Math.Round(gain(slider.Value)*100).ToString("0",CultureInfo.CurrentCulture)+"%";
        void Position(MouseEventArgs e){var point=e.GetPosition(slider);popup.HorizontalOffset=point.X;popup.VerticalOffset=point.Y+20;}
        slider.ValueChanged+=(_,_)=>Update();slider.Loaded+=(_,_)=>Update();Update();
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler((_,e)=>{Update();Position(e);popup.IsOpen=slider.IsEnabled;}),true);
        slider.AddHandler(UIElement.PreviewMouseMoveEvent,new MouseEventHandler((_,e)=>{if(popup.IsOpen)Position(e);}),true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler((_,_)=>popup.IsOpen=false),true);
        slider.LostMouseCapture+=(_,_)=>popup.IsOpen=false;slider.Unloaded+=(_,_)=>popup.IsOpen=false;
    }
}
