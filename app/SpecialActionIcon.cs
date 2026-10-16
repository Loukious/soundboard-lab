using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundboardLab;

public sealed class SpecialActionIcon : FrameworkElement
{
    public static readonly DependencyProperty ActionProperty=DependencyProperty.Register(nameof(Action),typeof(string),typeof(SpecialActionIcon),new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.AffectsRender,(owner,_)=>((SpecialActionIcon)owner).UpdateVisibility()));
    public string Action {get=>(string)GetValue(ActionProperty);set=>SetValue(ActionProperty,value);}
    private static readonly Dictionary<string,BitmapFrame[]> images=[];
    private static readonly Brush blue=new SolidColorBrush(Color.FromRgb(0,103,171));
    public SpecialActionIcon()=>UpdateVisibility();
    private void UpdateVisibility()=>Visibility=ResourceName(Action) is null&&!Action.StartsWith("forwarding",StringComparison.Ordinal)?Visibility.Collapsed:Visibility.Visible;
    internal static string? ResourceName(string action)=>action switch {
        "stop"=>"stop",
        "play" or "playSelected" or "playCurrent" or "playPreviouslyPlayed" or "playModeDefault" or "playTtsDefault"=>"play",
        "pauseResume"=>"pause","playPrevious"=>"play_previous","playNext"=>"play_next",
        "randomPlayback" or "randomPlaybackAll"=>"random",
        "recordBegin" or "recordBeginSpeakers" or "recordBeginMicrophone"=>"record_on","recordEnd"=>"record_off",
        "volumeLower1" or "volumeLower2" or "volumeRaise1" or "volumeRaise2" or "volumeSet1" or "volumeSet2" or "playModeSpeakers" or "playTtsRender"=>"headphones-mid",
        "volumeToggleMute"=>"headphones_muted",
        "playModeMicrophone" or "playTtsCapture"=>"microphone",
        "autoKeysEnable" or "autoKeysDisable"=>"auto_keys","hotkeysEnable" or "hotkeysDisable"=>"hotkeys",
        _=>null};
    private BitmapFrame Frame(string name)
    {
        if(!images.TryGetValue(name,out var frames)){
            string path=name=="headphones-mid"?"headphones-mid.ico":"special-hotkeys/"+name+".ico";
            using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/SoundboardLab;component/assets/"+path)).Stream;
            frames=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.ToArray();
            foreach(var frame in frames)frame.Freeze();images[name]=frames;
        }
        return frames.MinBy(x=>Math.Abs(x.PixelWidth-16*VisualTreeHelper.GetDpi(this).PixelsPerDip))!;
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);InvalidateVisual();}
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);if(ActualWidth<=0||ActualHeight<=0)return;
        if(ResourceName(Action) is not {} name){if(Action.StartsWith("forwarding",StringComparison.Ordinal)){drawing.PushTransform(new ScaleTransform(ActualWidth/16,ActualHeight/16));drawing.DrawGeometry(blue,null,Geometry.Parse("M0,4 H8 V0 L16,8 8,16 V12 H0 Z"));drawing.Pop();}return;}
        var bounds=new Rect(0,0,ActualWidth,ActualHeight);
        void Paint(string resource,Brush colour){drawing.PushOpacityMask(new ImageBrush(Frame(resource)){Stretch=Stretch.Fill});drawing.DrawRectangle(colour,null,bounds);drawing.Pop();}
        if(name is "record_on" or "record_off")drawing.DrawImage(Frame(name),bounds);
        else Paint(name,blue);
        if(Action is "autoKeysDisable" or "hotkeysDisable")Paint("button_disabled",Brushes.Red);
    }
}
