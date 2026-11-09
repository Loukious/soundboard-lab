using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
namespace SoundboardLab.Services;

internal sealed class TrayIcon:IDisposable
{
    private const int Callback=0x8000+42;
    private readonly HwndSource source;
    private readonly Action show,exit;
    private readonly ContextMenu menu=new();
    private readonly Action<ContextMenu> openMenu;
    private readonly IntPtr icon=LoadImage(IntPtr.Zero,System.IO.Path.Combine(AppContext.BaseDirectory,"assets","app-icon.ico"),1,0,0,0x10|0x40);
    private bool visible,menuQueued,disposed;
    public TrayIcon(Window window,Action show,Action exit,Action<ContextMenu>? openMenu=null){
        this.show=show;this.exit=exit;this.openMenu=openMenu??(value=>value.IsOpen=true);
        var open=new MenuItem {Header="Open Soundboard Lab"};open.Click+=(_,_)=>show();
        var close=new MenuItem {Header="Exit"};close.Click+=(_,_)=>exit();menu.Items.Add(open);menu.Items.Add(close);
        source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);source.AddHook(Message);
    }
    public void SetVisible(bool value){if(visible==value)return;var data=Data();if(!Shell_NotifyIcon(value?0u:2u,ref data))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());visible=value;}
    private DataValue Data()=>new(){Size=(uint)Marshal.SizeOf<DataValue>(),Window=source.Handle,Id=1,Flags=1|2|4,Callback=(uint)Callback,Icon=icon!=IntPtr.Zero?icon:LoadIcon(IntPtr.Zero,new IntPtr(32512)),Tip="Soundboard Lab",Info="",InfoTitle=""};
    private IntPtr Message(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled){
        if(message!=Callback||disposed)return IntPtr.Zero;
        if(l.ToInt32()==0x203)show();
        else if(l.ToInt32()==0x205&&!menuQueued){
            menuQueued=true;
            // Return Explorer's notification before constructing/showing popup
            // windows. Reuse the menu instead of recreating its themed controls.
            source.Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>{menuQueued=false;if(!disposed)openMenu(menu);});
        }
        handled=true;return IntPtr.Zero;
    }
    public void Dispose(){if(disposed)return;disposed=true;menu.IsOpen=false;if(visible){var data=Data();Shell_NotifyIcon(2,ref data);visible=false;}source.RemoveHook(Message);if(icon!=IntPtr.Zero)DestroyIcon(icon);}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DataValue{public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;public uint State,StateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;public uint Timeout;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool Shell_NotifyIcon(uint command,ref DataValue data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr LoadIcon(IntPtr instance,IntPtr id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr LoadImage(IntPtr instance,string path,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
}
