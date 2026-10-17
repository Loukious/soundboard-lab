using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Paint native chrome on a hidden HWND with the app window's caption/style.
// Keeping this HWND separate avoids altering WPF layout while taking a picture.
internal static class NativeWindowFrame
{
    [StructLayout(LayoutKind.Sequential)]private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo
    {
        public uint Size;public int Width,Height;public ushort Planes,BitCount;
        public uint Compression,ImageSize;public int XPelsPerMeter,YPelsPerMeter;public uint ColorsUsed,ColorsImportant;
    }
    internal sealed record Frame(BitmapSource Image,System.Windows.Rect Client);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint extendedStyle,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint LoadImage(nint instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool AdjustWindowRectExForDpi(ref Rect rect,uint style,bool menu,uint extendedStyle,uint dpi);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(nint window,ref Point point);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint SendMessage(nint window,uint message,nint parameter,nint flags);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint pixels,nint section,uint offset);
    [DllImport("gdi32.dll")]private static extern nint SelectObject(nint dc,nint item);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);

    internal static Frame Capture(Window window,double clientWidth,double clientHeight)
    {
        uint style=0x00C80000; // caption and system menu, never WS_VISIBLE
        if(window.ResizeMode==ResizeMode.CanMinimize)style|=0x20000;
        else if(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)style|=0x70000;
        uint extendedStyle=window.WindowStyle==WindowStyle.ToolWindow?0x80u:0;
        nint handle=CreateWindowEx(extendedStyle,"STATIC",window.Title,style,-10000,-10000,100,100,0,0,0,0);
        if(handle==0)throw new Win32Exception();
        nint icon=0;
        try{
        uint dpi=GetDpiForWindow(handle);double scale=dpi/96d;
        string iconPath=Path.Combine(AppContext.BaseDirectory,"assets","app-icon.ico");
        if(File.Exists(iconPath)){icon=LoadImage(0,iconPath,1,(int)Math.Round(16*scale),(int)Math.Round(16*scale),0x10);if(icon!=0){SendMessage(handle,0x80,0,icon);SendMessage(handle,0x80,1,icon);}}
        var requested=new Rect {Right=(int)Math.Round(clientWidth*scale),Bottom=(int)Math.Round(clientHeight*scale)};
        if(!AdjustWindowRectExForDpi(ref requested,style,false,extendedStyle,dpi))throw new Win32Exception();
        // NOZORDER | NOACTIVATE, deliberately without SHOWWINDOW.
        if(!SetWindowPos(handle,0,-10000,-10000,requested.Right-requested.Left,requested.Bottom-requested.Top,0x14))throw new Win32Exception();
        if(!GetWindowRect(handle,out var bounds)||!GetClientRect(handle,out var client))throw new Win32Exception();
        var origin=new Point();if(!ClientToScreen(handle,ref origin))throw new Win32Exception();
        int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
        nint dc=CreateCompatibleDC(0);if(dc==0)throw new Win32Exception();
        var info=new BitmapInfo {Size=(uint)Marshal.SizeOf<BitmapInfo>(),Width=width,Height=-height,Planes=1,BitCount=32};
        nint bitmap=CreateDIBSection(dc,ref info,0,out nint pixels,0,0);if(bitmap==0){DeleteDC(dc);throw new Win32Exception();}
        nint previous=SelectObject(dc,bitmap);
        try{
            // WM_PRINT with PRF_NONCLIENT paints even a hidden window. Calling
            // PrintWindow on an unshown DWM surface leaves its caption black.
            SendMessage(handle,0x317,dc,2);
            var bytes=new byte[checked(width*height*4)];Marshal.Copy(pixels,bytes,0,bytes.Length);
            for(int index=3;index<bytes.Length;index+=4)bytes[index]=255;
            var image=BitmapSource.Create(width,height,dpi,dpi,PixelFormats.Bgra32,null,bytes,width*4);image.Freeze();
            return new(image,new((origin.X-bounds.Left)/scale,(origin.Y-bounds.Top)/scale,client.Right/scale,client.Bottom/scale));
        }finally{SelectObject(dc,previous);DeleteObject(bitmap);DeleteDC(dc);}
        }finally{DestroyWindow(handle);if(icon!=0)DestroyIcon(icon);}
    }
}
