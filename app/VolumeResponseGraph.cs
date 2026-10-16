using System.Windows;
using System.Windows.Media;

namespace SoundboardLab;

internal sealed class VolumeResponseGraph : FrameworkElement
{
    public static readonly DependencyProperty LinearProperty=DependencyProperty.Register(nameof(Linear),typeof(bool),typeof(VolumeResponseGraph),new FrameworkPropertyMetadata(true,FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Linear {get=>(bool)GetValue(LinearProperty);set=>SetValue(LinearProperty,value);}
    public VolumeResponseGraph(){Width=100;Height=26;VerticalAlignment=VerticalAlignment.Center;}
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);drawing.DrawRectangle(Brushes.White,null,new Rect(RenderSize));
        var curve=new StreamGeometry();using(var path=curve.Open()){
            path.BeginFigure(new Point(1,ActualHeight-1),false,false);
            for(int i=1;i<=100;i++){double x=i/100d;path.LineTo(new Point(1+x*(ActualWidth-2),(1-(Linear?x:x*x))*(ActualHeight-2)+1),true,false);}
        }
        curve.Freeze();drawing.DrawGeometry(null,new Pen(new SolidColorBrush(Color.FromRgb(137,201,232)),1.3),curve);
    }
}
