using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundboardLab;

public sealed class HeadphonesVolumeIcon : FrameworkElement
{
    public static readonly DependencyProperty FillProperty=DependencyProperty.Register(nameof(Fill),typeof(Brush),typeof(HeadphonesVolumeIcon),new FrameworkPropertyMetadata(Brushes.DodgerBlue,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VolumeProperty=DependencyProperty.Register(nameof(Volume),typeof(double),typeof(HeadphonesVolumeIcon),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender),value=>double.IsFinite((double)value));
    public Brush Fill {get=>(Brush)GetValue(FillProperty);set=>SetValue(FillProperty,value);}
    public double Volume {get=>(double)GetValue(VolumeProperty);set=>SetValue(VolumeProperty,value);}
    public bool IsMuted=>VolumeBand==0;
    private int VolumeBand {
        get {
            // The reference selects from the slider's integer percentage:
            // muted at 0, low through 33, medium through 66, then high.
            int percent=(int)Math.Round(Math.Clamp(Volume,0,1)*100,MidpointRounding.AwayFromZero);
            return percent==0?0:percent<=33?1:percent<=66?2:3;
        }
    }
    private static readonly BitmapFrame[][] frames=[LoadFrames("empty"),LoadFrames("low"),LoadFrames("mid"),LoadFrames("high")];
    private static BitmapFrame[] LoadFrames(string level)
    {
        using var stream=Application.GetResourceStream(new Uri($"pack://application:,,,/SoundboardLab;component/assets/headphones-{level}.ico")).Stream;
        var decoded=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.ToArray();
        foreach(var frame in decoded)frame.Freeze();return decoded;
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);InvalidateVisual();}
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);if(ActualWidth<=0||ActualHeight<=0)return;
        var frame=frames[VolumeBand].MinBy(x=>Math.Abs(x.PixelWidth-16*VisualTreeHelper.GetDpi(this).PixelsPerDip))!;
        int size=frame.PixelWidth;
        drawing.PushTransform(new ScaleTransform(ActualWidth/size,ActualHeight/size));
        // Use the original resource's alpha, retaining its exact headband and
        // ear pads while allowing the application theme to supply the colour.
        drawing.PushOpacityMask(new ImageBrush(frame){Stretch=Stretch.Fill});
        drawing.DrawRectangle(Fill,null,new Rect(0,0,size,size));drawing.Pop();
        if(IsMuted){
            // The native renderer composes this cross over HEADPHONES_EMPTY.
            // Its size/offset table and two-pixel pen are retained at each DPI.
            var (x,y,width,height)=size switch {24=>(8,12,8,11),32=>(11,17,11,14),48=>(15,27,18,19),_=>(5,7,6,9)};
            drawing.PushClip(new RectangleGeometry(new Rect(x,y,width,height)));
            var pen=new Pen(Brushes.Red,2);
            drawing.DrawLine(pen,new Point(x,y),new Point(x+width-1,y+height-1));
            drawing.DrawLine(pen,new Point(x+width-1,y),new Point(x,y+height-1));drawing.Pop();
        }
        drawing.Pop();
    }
}
