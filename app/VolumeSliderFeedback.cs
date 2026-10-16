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
        bool dragging=false;
        void Move(MouseEventArgs e){if(slider.Template?.FindName("PART_Track",slider) is Track track)SetValueFromPointer(slider,track,e.GetPosition(track));Position(e);}
        slider.ValueChanged+=(_,_)=>Update();slider.Loaded+=(_,_)=>Update();Update();
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler((_,e)=>{
            if(!slider.IsEnabled)return;
            // Own the entire gesture, including track clicks. WPF's default
            // move-to-point handler only drags when the thumb was clicked.
            dragging=slider.CaptureMouse();if(!dragging)return;
            e.Handled=true;Move(e);Update();popup.IsOpen=true;
        }),true);
        slider.AddHandler(UIElement.PreviewMouseMoveEvent,new MouseEventHandler((_,e)=>{
            if(!dragging)return;
            if(e.LeftButton!=MouseButtonState.Pressed){dragging=false;slider.ReleaseMouseCapture();return;}
            Move(e);e.Handled=true;
        }),true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler((_,e)=>{
            if(!dragging)return;
            Move(e);dragging=false;popup.IsOpen=false;slider.ReleaseMouseCapture();e.Handled=true;
        }),true);
        slider.LostMouseCapture+=(_,_)=>{dragging=false;popup.IsOpen=false;};
        slider.Unloaded+=(_,_)=>{dragging=false;popup.IsOpen=false;if(slider.IsMouseCaptured)slider.ReleaseMouseCapture();};
    }

    internal static void SetValueFromPointer(Slider slider,Track track,Point point)
    {
        // Vertical distance never cancels a horizontal volume drag.
        double value=track.ValueFromPoint(new Point(point.X,track.ActualHeight/2));
        if(double.IsFinite(value))slider.SetCurrentValue(Slider.ValueProperty,Math.Clamp(value,slider.Minimum,slider.Maximum));
    }
}
