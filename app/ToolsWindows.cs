using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundboardLab.Audio;
using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

internal sealed class RecorderWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    public RecorderWindow(AudioService audio, AppPreferences settings, string stateDirectory, Action<string>? notify = null,Func<RecordingSource,int,Task>? startRecording=null)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Sound recorder"; Width = 520; Height = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Capture a new sound", FontSize = 21, Margin = new(3, 0, 0, 14) });
        var source = new ComboBox { ItemsSource = Enum.GetValues<RecordingSource>(), SelectedIndex = 0 }; panel.Children.Add(source);
        var limit = Dialogs.Field(panel, "Maximum duration in seconds", settings.RecordingLimitSeconds.ToString());
        var status = new TextBlock { Text = "Recording will be saved and added to your library.", TextWrapping = TextWrapping.Wrap, Margin = new(5, 12, 0, 12) }; panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        DateTime started = DateTime.Now;
        var start = Dialogs.Button(buttons, "● Start recording", async (_, _) => {
            try {
                if (audio.IsRecording) throw new InvalidOperationException("A recording is already in progress.");
                int seconds=(int)Dialogs.Number(limit,0,86400);if(startRecording is not null)await startRecording((RecordingSource)source.SelectedItem,seconds);started=DateTime.Now;
                status.Text = "Recording…";
            } catch (Exception ex) { Dialogs.Error(this, ex); }
        });
        Dialogs.Button(buttons, "■ Stop and save", async (_, _) => { try { await audio.StopRecordingAsync(); status.Text = "Saved: " + audio.LastRecordingPath; } catch (Exception ex) { Dialogs.Error(this, ex); } });
        timer.Tick += (_, _) => { start.IsEnabled = !audio.IsRecording; if (audio.IsRecording) status.Text = $"Recording · {(DateTime.Now - started).TotalSeconds:F1} seconds"; };
        Loaded += (_, _) => timer.Start(); Closing += async (_, _) => { timer.Stop(); if (audio.IsRecording) await audio.StopRecordingAsync(); };
    }
}

internal sealed class SpeechWindow : Window
{
    private TextBox speechText=null!;
    internal Func<Task<string>> GenerateAsync {get;private set;}=null!;
    internal void AppendText(string value){Activate();speechText.Focus();speechText.AppendText(value);speechText.CaretIndex=speechText.Text.Length;}
    private readonly AudioService preview = new();
    public SpeechWindow(Func<string, Task> add, string stateDirectory, AppPreferences settings)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Text to speech"; Width = 650; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(22) }; Content = panel;
        var text = Dialogs.Field(panel, "Text", "", 5);speechText=text; var voices = new ComboBox(); panel.Children.Add(voices);
        var rate = Dialogs.Field(panel, "Voice speed (-10 to 10)", settings.TtsRate.ToString());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        async Task<string> Synthesize()
        { string folder = string.IsNullOrWhiteSpace(settings.RecordingFolder)?System.IO.Path.Combine(stateDirectory,"speech"):settings.RecordingFolder; Directory.CreateDirectory(folder); string path = System.IO.Path.Combine(folder, "Speech-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".wav"); await SpeechService.SynthesizeAsync(text.Text, path, voices.SelectedItem as string, (int)Dialogs.Number(rate, -10, 10)); return await RecordingExport.SaveAsync(path,new(settings.RecordingFormat,int.Parse(settings.RecordingBitrate.Split(' ')[0])*1000,settings.NormalizeRecordings,settings.RecordingTargetDb,settings.TrimRecordingSilence)); }
        GenerateAsync=Synthesize;
        Dialogs.Button(buttons, "Preview", async (_, _) => { try { await preview.PlayAsync(await Synthesize(), PlaybackDestination.Speakers); } catch (Exception ex) { Dialogs.Error(this, ex); } });
        Dialogs.Button(buttons, "Stop", (_, _) => preview.Stop());
        Dialogs.Button(buttons, "Generate and add", async (_, _) => { try { string path = await Synthesize(); await add(path); settings.TtsVoice = voices.SelectedItem as string ?? ""; settings.TtsRate = (int)Dialogs.Number(rate, -10, 10); MessageBox.Show(this, "Speech added to your library.", "Text to speech"); } catch (Exception ex) { Dialogs.Error(this, ex); } });
        Loaded += (_, _) => { try { voices.ItemsSource = SpeechService.GetVoices(); voices.SelectedItem = settings.TtsVoice; if (voices.SelectedIndex < 0 && voices.Items.Count > 0) voices.SelectedIndex = 0; } catch (Exception ex) { Dialogs.Error(this, ex); } };
        Closed += (_, _) => preview.Dispose();
    }
}

internal sealed class MicrophoneSetupWindow : Window
{
    public MicrophoneSetupWindow(AudioService audio, AppPreferences settings, Func<Task> test)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Microphone mixing"; Width = 680; Height = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Use your existing microphone", FontSize = 21, Margin = new(3, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Enable mixing on the same microphone selected in your voice apps. Your previous audio effects are backed up for restoration. Windows will ask for administrator access when applying or restoring setup.", TextWrapping = TextWrapping.Wrap, Margin = new(5) });
        string selectedId;
        using (var selected = audio.GetInputDevice()) selectedId = selected.ID;
        var devices = new ComboBox { ItemsSource = audio.Devices.Where(x => x.IsInput).ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = selectedId }; if (devices.SelectedIndex < 0) devices.SelectedIndex = 0; panel.Children.Add(devices);
        var slot = new ComboBox { ItemsSource = new[] { "PostMix", "Stream", "Mode", "Endpoint" }, SelectedIndex = 0 }; panel.Children.Add(new TextBlock { Text = "Compatibility mode", Margin = new(5, 12, 0, 2) }); panel.Children.Add(slot);
        var status = new TextBlock { Text = audio.GetInjectionStatus().Message, TextWrapping = TextWrapping.Wrap, Margin = new(5, 12, 0, 12) }; panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        async Task Run(bool install)
        {
            if (devices.SelectedItem is not AudioDevice device) throw new InvalidOperationException("Select a microphone.");
            await DeviceSetupClient.RunAsync(new(install?"install":"restore",[device.Id],install,install?(string)slot.SelectedItem:null));
            settings.CaptureDeviceId = device.Id; status.Text = "Setup completed. Windows Audio may need a restart to reload the effects. Then reopen recording apps and test the microphone.";
        }
        Dialogs.Button(buttons, "Enable mixing", async (_, _) => { try { await Run(true); } catch (Exception ex) { Dialogs.Error(this, ex); } });
        Dialogs.Button(buttons, "Restore original settings", async (_, _) => { try { await Run(false); } catch (Exception ex) { Dialogs.Error(this, ex); } });
        Dialogs.Button(panel, "Restart Windows Audio…", async (_, _) => {
            if (MessageBox.Show(this, "This briefly interrupts playback and recording in all applications. Some apps may need reopening. Restart Windows Audio now?", "Restart Windows Audio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try {
                await DeviceSetupClient.RunAsync(new("restart",[]));
                status.Text = "Windows Audio restarted. Reopen your voice apps, then test the microphone.";
            } catch (Exception ex) { Dialogs.Error(this, ex); }
        });
        Dialogs.Button(panel, "Test selected microphone", async (_, _) => { try { if (devices.SelectedItem is AudioDevice d) { settings.CaptureDeviceId = d.Id; audio.SelectDevices(d.Id, string.IsNullOrWhiteSpace(settings.RenderDeviceId) ? null : settings.RenderDeviceId); } await test(); status.Text = audio.GetInjectionStatus().Message; } catch (Exception ex) { Dialogs.Error(this, ex); } });
        panel.Children.Add(new TextBlock { Text = "This test build uses a development audio component. If Windows refuses to load it, restore the original settings; changing Windows security settings is not part of setup.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new(5, 18, 0, 0) });
    }
}
