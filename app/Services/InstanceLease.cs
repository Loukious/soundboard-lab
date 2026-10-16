using System.IO;

namespace SoundboardLab.Services;

// Released by Windows even after a crash. Shared library/preferences files and
// the microphone producer must not be owned by two interactive instances.
public sealed class InstanceLease:IDisposable
{
    private readonly FileStream lease;
    public InstanceLease(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        lease=new(Path.Combine(dataDirectory,".instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public void Dispose()=>lease.Dispose();
}
