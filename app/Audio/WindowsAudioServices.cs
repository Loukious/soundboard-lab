using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoundboardLab.Audio;

// This path is called only by the elevated helper after an explicit Restart
// Win-Audio command. Device selection and repair never restart system audio.
internal static class WindowsAudioServices
{
    private const uint Running=4,Stopped=1;
    [StructLayout(LayoutKind.Sequential)]private struct Status {public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Dependent {public nint Name,Display;public Status Status;}
    private sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle():base(true){}
        protected override bool ReleaseHandle()=>CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ServiceHandle OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatus(ServiceHandle service,out Status status);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(ServiceHandle service,uint control,out Status status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool StartService(ServiceHandle service,uint count,nint args);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool EnumDependentServices(ServiceHandle service,uint state,nint buffer,uint length,out uint needed,out uint count);
    private static void Require(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    private static ServiceHandle Open(ServiceHandle manager,string name)
    {
        var service=OpenService(manager,name,0x4|0x8|0x10|0x20);if(service.IsInvalid){int error=Marshal.GetLastWin32Error();service.Dispose();throw new Win32Exception(error);}return service;
    }
    private static Status Read(ServiceHandle service){Require(QueryServiceStatus(service,out var status));return status;}
    private static void Wait(ServiceHandle service,uint state)
    {
        var clock=Stopwatch.StartNew();while(Read(service).State!=state){if(clock.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Windows Audio services did not respond in time.");Thread.Sleep(100);}
    }
    private static void Start(ServiceHandle service)
    {
        var state=Read(service).State;if(state==Running)return;if(state==3)Wait(service,Stopped);
        if(!StartService(service,0,0)&&Marshal.GetLastWin32Error()!=1056)throw new Win32Exception(Marshal.GetLastWin32Error());Wait(service,Running);
    }
    internal static void Restart()
    {
        using var manager=OpenSCManager(null,null,1);if(manager.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        using var audio=Open(manager,"Audiosrv");var stopped=new List<string>();
        void StopDependents(ServiceHandle service)
        {
            bool ok=EnumDependentServices(service,1,0,0,out uint needed,out _);int error=Marshal.GetLastWin32Error();
            if(ok)return;if(error!=234)throw new Win32Exception(error);
            nint buffer=Marshal.AllocHGlobal(checked((int)needed));
            try{
                Require(EnumDependentServices(service,1,buffer,needed,out _,out uint count));
                for(int index=0;index<count;index++){
                    var entry=Marshal.PtrToStructure<Dependent>(buffer+index*Marshal.SizeOf<Dependent>());string name=Marshal.PtrToStringUni(entry.Name)!;
                    using var dependent=Open(manager,name);StopDependents(dependent);stopped.Add(name);
                    if(Read(dependent).State!=Stopped){Require(ControlService(dependent,1,out _));Wait(dependent,Stopped);}
                }
            }finally{Marshal.FreeHGlobal(buffer);}
        }
        Exception? failure=null;
        try{StopDependents(audio);if(Read(audio).State!=Stopped){Require(ControlService(audio,1,out _));Wait(audio,Stopped);}Start(audio);}
        catch(Exception error){failure=error;}
        finally{
            try{Start(audio);}catch(Exception error){failure??=error;}
            foreach(string name in stopped.AsEnumerable().Reverse())try{using var service=Open(manager,name);Start(service);}catch(Exception error){failure??=error;}
        }
        if(failure is not null)throw new InvalidOperationException("Windows Audio restart did not complete: "+failure.Message,failure);
    }
}
