using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using SoundboardLab.Audio;

namespace SoundboardLab;

internal sealed class EditorWaveform : FrameworkElement
{
    internal EditorDocument? Document;
    internal double Zoom=400,Offset,Viewport=600,Playhead;
    internal Action? SelectionChanged;
    internal bool ShowSelection=true;
    private bool selecting;
    public EditorWaveform(){Focusable=true;MinHeight=100;ClipToBounds=true;}
    protected override AutomationPeer OnCreateAutomationPeer()=>new FrameworkElementAutomationPeer(this);
    private double Pointer(MouseEventArgs e)=>Math.Clamp(e.GetPosition(this).X/Zoom,0,Document?.Duration??0);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){base.OnMouseLeftButtonDown(e);if(Document is null)return;Focus();CaptureMouse();selecting=true;Document.Start=Document.End=Pointer(e);Playhead=Document.Start;SelectionChanged?.Invoke();InvalidateVisual();e.Handled=true;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(!selecting||Document is null)return;Document.End=Pointer(e);SelectionChanged?.Invoke();InvalidateVisual();e.Handled=true;}
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){base.OnMouseLeftButtonUp(e);if(!selecting||Document is null)return;Document.End=Pointer(e);selecting=false;ReleaseMouseCapture();SelectionChanged?.Invoke();InvalidateVisual();e.Handled=true;}
    protected override void OnLostMouseCapture(MouseEventArgs e){base.OnLostMouseCapture(e);selecting=false;}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);double height=ActualHeight,top=24,middle=top+(height-top)/2;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(56,56,56)),null,new Rect(0,0,ActualWidth,height));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(70,70,70)),null,new Rect(0,0,ActualWidth,top));
        if(Document is null)return;var textBrush=new SolidColorBrush(Color.FromRgb(210,210,210));var pen=new Pen(textBrush,1);
        double step=Zoom>=250?.2:Zoom>=100?.5:Zoom>=35?1:5;
        double first=Math.Floor(Offset/Zoom/step)*step,last=Math.Min(Document.Duration,(Offset+Viewport)/Zoom+step);
        for(double time=first;time<=last;time+=step){double x=time*Zoom;var stamp=new FormattedText(Stamp(time),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,textBrush,VisualTreeHelper.GetDpi(this).PixelsPerDip);dc.DrawText(stamp,new Point(x+2,0));for(int i=0;i<5;i++){double tick=x+i*step*Zoom/5;dc.DrawLine(pen,new Point(tick,18),new Point(tick,i==0?11:14));}}
        if(ShowSelection&&Document.HasSelection)dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(85,0,125,184)),null,new Rect(Document.SelectionStart*Zoom,top,(Document.SelectionEnd-Document.SelectionStart)*Zoom,height-top));
        dc.DrawLine(new Pen(Brushes.Gray,1),new Point(Offset,middle),new Point(Math.Min(ActualWidth,Offset+Viewport),middle));
        int left=(int)Math.Max(0,Offset-2),right=(int)Math.Min(ActualWidth,Offset+Math.Min(Viewport,4000)+2);
        var wave=new StreamGeometry();using(var geometry=wave.Open())for(int x=left;x<right;x++){
            int begin=(int)Math.Clamp(x/Zoom*48000,0,Document.Samples.Length/2),end=(int)Math.Clamp((x+1)/Zoom*48000,0,Document.Samples.Length/2);float peak=0;
            for(int frame=begin;frame<end;frame++){peak=Math.Max(peak,Math.Abs(Document.Samples[frame*2]));peak=Math.Max(peak,Math.Abs(Document.Samples[frame*2+1]));}
            double amplitude=Math.Min(1,peak)*(height-top-6)/2;geometry.BeginFigure(new Point(x,middle-amplitude),false,false);geometry.LineTo(new Point(x,middle+amplitude),true,false);
        }
        wave.Freeze();dc.DrawGeometry(null,new Pen(textBrush,1),wave);dc.DrawLine(new Pen(Brushes.Gray,1),new Point(Playhead*Zoom,top),new Point(Playhead*Zoom,height));
    }
    internal static string Stamp(double time){int tenths=(int)Math.Round(Math.Max(0,time)*10),whole=tenths/10;return $"{whole/60}:{whole%60:00}"+(tenths%10!=0?CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator+(tenths%10):"");}
}
