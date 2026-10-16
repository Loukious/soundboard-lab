using System.Runtime.InteropServices;
namespace SoundboardLab;

internal static class FileMetadata
{
    public static (string Title, string Tags,string Artist) Read(string path)
    {
        object? shell = null, folder = null, item = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace(Path.GetDirectoryName(Path.GetFullPath(path)));
            item = ((dynamic)folder!).ParseName(Path.GetFileName(path));
            string title = Convert.ToString(((dynamic)item!).ExtendedProperty("System.Title")) ?? "";
            var artists = ((dynamic)item!).ExtendedProperty("System.Music.Artist");
            string artist = artists is Array list ? string.Join(", ", list.Cast<object>()) : Convert.ToString(artists) ?? "";
            string album = Convert.ToString(((dynamic)item!).ExtendedProperty("System.Music.AlbumTitle")) ?? "";
            return (title, string.Join(" · ", new[] { artist, album }.Where(x => !string.IsNullOrWhiteSpace(x))),artist);
        }
        catch { return ("", "",""); }
        finally { foreach (object? obj in new[] { item, folder, shell }) if (obj is not null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
    }
}
