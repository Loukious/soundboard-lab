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
        slider.IsMoveToPointEnabled=false; // This handler owns track clicks as well as thumb drags.
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
        // Track.ValueFromPoint adds a delta to Value using the thumb's last
        // arranged position. Value can already have changed during this input
        // event, so that delta would be applied twice until the next layout.
        // Map the absolute pointer coordinate instead, independent of Value.
        double thumbWidth=track.Thumb?.ActualWidth??0,travel=track.ActualWidth-thumbWidth;
        if(travel<=0||!double.IsFinite(point.X))return;
        double ratio=Math.Clamp((point.X-thumbWidth/2)/travel,0,1);
        if(track.IsDirectionReversed)ratio=1-ratio;
        slider.SetCurrentValue(Slider.ValueProperty,slider.Minimum+ratio*(slider.Maximum-slider.Minimum));
    }
}
