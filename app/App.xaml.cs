using System.Windows;
using System.Diagnostics;
using System.IO;
using SoundboardLab.Services;
namespace SoundboardLab;
public partial class App : Application
{
    private InstanceLease? instanceLease;
    private void App_Startup(object sender, StartupEventArgs e)
    {
        bool isolated=e.Args.Contains("--pipe-name")||e.Args.Contains("--smoke-test");
        if(!isolated&&Process.GetProcessesByName("SoundboardLab").Any(x=>{using(x)return x.Id!=Environment.ProcessId;})){
            MessageBox.Show("Soundboard Lab is already running. Exit the other preview from its tray menu before starting this version.","Soundboard Lab",MessageBoxButton.OK,MessageBoxImage.Information);Shutdown();return;
        }
        int dataIndex=Array.IndexOf(e.Args,"--data-dir");string directory=dataIndex>=0&&dataIndex+1<e.Args.Length?Path.GetFullPath(e.Args[dataIndex+1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SoundboardLab");
        try{instanceLease=new(directory);}catch(IOException){MessageBox.Show("Another Soundboard Lab instance is using this library. Exit that instance before starting this version.","Soundboard Lab",MessageBoxButton.OK,MessageBoxImage.Information);Shutdown();return;}
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Soundboard Lab", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        new MainWindow(e.Args).Show();
    }
    protected override void OnExit(ExitEventArgs e){instanceLease?.Dispose();base.OnExit(e);}
}
