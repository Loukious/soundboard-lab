using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SoundboardLab;

internal sealed class DecibelMeter : FrameworkElement
{
    public double Level {get;set;}=18;
    public double Measured {get;set;}=18;
    public DecibelMeter(){Width=355;Height=40;Margin=new(0,4,0,7);}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240,240,240)),new Pen(Brushes.Silver,1),new Rect(0,0,ActualWidth,ActualHeight));
        double Scale(double db)=>65+Math.Clamp((db-20)/90,0,1)*(ActualWidth-85);
        var blue=new SolidColorBrush(Color.FromRgb(0,103,171));
        if(Level>20)dc.DrawRectangle(blue,null,new Rect(65,3,Math.Max(0,Scale(Level)-65),8));
        dc.DrawLine(new Pen(Brushes.DarkOrange,1),new Point(Scale(Measured),1),new Point(Scale(Measured),12));
        void Text(string value,double x,double y){var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,new SolidColorBrush(Color.FromRgb(0,35,74)),VisualTreeHelper.GetDpi(this).PixelsPerDip);dc.DrawText(text,new Point(x,y));}
        Text("(dB)",3,23);
        for(int db=20;db<=100;db+=10){double x=Scale(db);dc.DrawLine(new Pen(Brushes.Black,1),new Point(x,13),new Point(x,21));Text(db.ToString(),x-7,23);}
    }
}
