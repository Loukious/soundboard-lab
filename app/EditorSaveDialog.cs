using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoundboardLab;

// The Windows Save dialog's customization interface keeps format and bitrate
// together, and lets the shell retain normal folder/filename/keyboard behavior.
internal static class EditorSaveDialog
{
    internal sealed record Choice(string Path,int Bitrate);
    private const uint BitrateControl=2001;
    private static readonly uint[] Bitrates=[320000,256000,224000,192000,160000,128000,112000,96000];
    private static readonly string[] Extensions=["m4a","mp3","wav"];
    internal static Choice? Show(Window owner,string input,string format,int bitrate)
    {
        object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"),true)!)!;
        var dialog=(IFileDialog)instance;uint cookie=0;bool advised=false;
        try {
            dialog.SetTitle("Save sound");dialog.SetFileTypes(3,[new("M4A (*.m4a)","*.m4a"),new("MP3 (*.mp3)","*.mp3"),new("WAV (*.wav)","*.wav")]);
            uint index=(uint)Math.Max(0,Array.IndexOf(Extensions,format.ToLowerInvariant()))+1;
            dialog.SetFileTypeIndex(index);dialog.SetDefaultExtension(Extensions[index-1]);dialog.SetFileName(Path.GetFileNameWithoutExtension(input));
            dialog.GetOptions(out uint options);dialog.SetOptions((options&~2u)|0x40u|0x800u|8u|4u|0x8000u);
            var itemId=typeof(IShellItem).GUID;
            if(SHCreateItemFromParsingName(Path.GetDirectoryName(Path.GetFullPath(input))!,IntPtr.Zero,ref itemId,out var folder)==0)try{dialog.SetFolder(folder);}finally{Marshal.ReleaseComObject(folder);}
            var custom=(IFileDialogCustomize)instance;
            custom.StartVisualGroup(2000,"Bitrate:");custom.AddComboBox(BitrateControl);
            foreach(uint value in Bitrates)custom.AddControlItem(BitrateControl,value,$"{value/1000} Kbps");
            custom.SetSelectedControlItem(BitrateControl,Bitrates.Contains((uint)bitrate)?(uint)bitrate:320000);custom.EndVisualGroup();
            custom.SetControlState(BitrateControl,index==2?3u:2u);
            var events=new DialogEvents(custom);dialog.Advise(events,out cookie);advised=true;
            int result=dialog.Show(new WindowInteropHelper(owner).Handle);GC.KeepAlive(events);
            if(result==unchecked((int)0x800704C7))return null;Marshal.ThrowExceptionForHR(result);
            custom.GetSelectedControlItem(BitrateControl,out uint selected);
            dialog.GetResult(out var file);
            try{file.GetDisplayName(0x80058000,out var name);try{return new(Marshal.PtrToStringUni(name)!,checked((int)selected));}finally{Marshal.FreeCoTaskMem(name);}}
            finally{Marshal.ReleaseComObject(file);}
        }
        finally{if(advised)dialog.Unadvise(cookie);Marshal.ReleaseComObject(instance);}
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    private sealed class DialogEvents(IFileDialogCustomize custom):IFileDialogEvents
    {
        public int OnFileOk(IFileDialog dialog)=>0;
        public int OnFolderChanging(IFileDialog dialog,IShellItem folder)=>0;
        public int OnFolderChange(IFileDialog dialog)=>0;
        public int OnSelectionChange(IFileDialog dialog)=>0;
        public int OnShareViolation(IFileDialog dialog,IShellItem file,out uint response){response=0;return 0;}
        public int OnTypeChange(IFileDialog dialog){try{dialog.GetFileTypeIndex(out uint index);custom.SetControlState(BitrateControl,index==2?3u:2u);dialog.SetDefaultExtension(Extensions[index-1]);return 0;}catch(Exception ex){return Marshal.GetHRForException(ex);}}
        public int OnOverwrite(IFileDialog dialog,IShellItem file,out uint response){response=0;return 0;}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]
    private static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItem item);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct FilterSpec(string name,string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)]public string Name=name;
        [MarshalAs(UnmanagedType.LPWStr)]public string Pattern=pattern;
    }
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind,out IntPtr name);
        void GetAttributes(uint mask,out uint attributes);
        void Compare(IShellItem other,uint hint,out int order);
    }
    [ComImport,Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]int Show(IntPtr owner);
        void SetFileTypes(uint count,[MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)]FilterSpec[] filters);
        void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events,out uint cookie);void Unadvise(uint cookie);
        void SetOptions(uint options);void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);void SetFolder(IShellItem folder);void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)]out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetResult(out IShellItem result);void AddPlace(IShellItem folder,uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)]string extension);
        void Close(int result);void SetClientGuid(ref Guid id);void ClearClientData();void SetFilter(IntPtr filter);
    }
    [ComImport,Guid("973510DB-7D7F-452B-8975-74A85828D354"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig]int OnFileOk(IFileDialog dialog);
        [PreserveSig]int OnFolderChanging(IFileDialog dialog,IShellItem folder);
        [PreserveSig]int OnFolderChange(IFileDialog dialog);
        [PreserveSig]int OnSelectionChange(IFileDialog dialog);
        [PreserveSig]int OnShareViolation(IFileDialog dialog,IShellItem file,out uint response);
        [PreserveSig]int OnTypeChange(IFileDialog dialog);
        [PreserveSig]int OnOverwrite(IFileDialog dialog,IShellItem file,out uint response);
    }
    [ComImport,Guid("E6FDD21A-163F-4975-9C8C-A69F1BA37034"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint control);
        void AddMenu(uint control,[MarshalAs(UnmanagedType.LPWStr)]string label);
        void AddPushButton(uint control,[MarshalAs(UnmanagedType.LPWStr)]string label);
        void AddComboBox(uint control);void AddRadioButtonList(uint control);
        void AddCheckButton(uint control,[MarshalAs(UnmanagedType.LPWStr)]string label,[MarshalAs(UnmanagedType.Bool)]bool value);
        void AddEditBox(uint control,[MarshalAs(UnmanagedType.LPWStr)]string value);void AddSeparator(uint control);
        void AddText(uint control,[MarshalAs(UnmanagedType.LPWStr)]string text);
        void SetControlLabel(uint control,[MarshalAs(UnmanagedType.LPWStr)]string label);
        void GetControlState(uint control,out uint state);void SetControlState(uint control,uint state);
        void GetEditBoxText(uint control,[MarshalAs(UnmanagedType.LPWStr)]out string text);
        void SetEditBoxText(uint control,[MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetCheckButtonState(uint control,[MarshalAs(UnmanagedType.Bool)]out bool value);
        void SetCheckButtonState(uint control,[MarshalAs(UnmanagedType.Bool)]bool value);
        void AddControlItem(uint control,uint item,[MarshalAs(UnmanagedType.LPWStr)]string label);
        void RemoveControlItem(uint control,uint item);void RemoveAllControlItems(uint control);
        void GetControlItemState(uint control,uint item,out uint state);void SetControlItemState(uint control,uint item,uint state);
        void GetSelectedControlItem(uint control,out uint item);void SetSelectedControlItem(uint control,uint item);
        void StartVisualGroup(uint control,[MarshalAs(UnmanagedType.LPWStr)]string label);void EndVisualGroup();
    }
}
