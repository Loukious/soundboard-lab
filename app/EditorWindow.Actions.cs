using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SoundboardLab.Audio;

namespace SoundboardLab;

internal sealed partial class EditorWindow
{
    private void StopPreview(){previewVersion++;preview.Stop();if(document is not null)waveform.Playhead=document.SelectionStart;waveform.InvalidateVisual();UpdateCommands();}
    private async Task PreviewAsync(bool selection)
    {
        if(document is null||saving||preparingPreview)return;StopPreview();int version=previewVersion;preparingPreview=true;var data=selection?document.SelectedSamples():document.Samples;playStart=selection?document.SelectionStart:0;
        try{await Task.Run(()=>AudioFiles.WriteWav(previewPath,data));if(disposed||version!=previewVersion)return;await preview.PlayAsync(previewPath,PlaybackDestination.Speakers);UpdateCommands();}
        finally{preparingPreview=false;if(disposed&&File.Exists(previewPath))File.Delete(previewPath);}
    }
    private async Task<bool> SaveAsync(bool saveAs,bool explicitClose=false)
    {
        if(document is null||saving)return false;string destination=input;
        if(saveAs||Path.GetExtension(input).ToLowerInvariant() is not (".wav" or ".mp3" or ".m4a")){
            var choice=EditorSaveDialog.Show(this,input,settings.RecordingFormat,exportBitrate);
            if(choice is null)return false;destination=choice.Path;exportBitrate=choice.Bitrate;
        }
        if(File.Exists(destination)&&overwritePrompt&&MessageBox.Show(this,"Overwrite the existing sound file?", "Confirm overwrite",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return false;
        saving=true;root.IsEnabled=false;StopPreview();string temporary=Path.Combine(Path.GetTempPath(),"SoundboardLab-edit-"+Guid.NewGuid()+".wav"),output=temporary;
        try{
            await Task.Run(()=>AudioFiles.WriteWav(temporary,document.Samples));string format=Path.GetExtension(destination).TrimStart('.').ToUpperInvariant();
            output=await RecordingExport.SaveAsync(temporary,new(format,exportBitrate));
            File.Move(output,destination,true);input=destination;document.MarkSaved();await add(destination);RefreshDocument();if(closeAfterSaving&&!explicitClose){allowClose=true;Close();}return true;
        }finally{foreach(string file in new[]{temporary,output}.Distinct())if(File.Exists(file))File.Delete(file);saving=false;root.IsEnabled=true;UpdateCommands();}
    }
    private async void HandleKey(object sender,KeyEventArgs e)
    {
        if(saving)return;bool ctrl=(Keyboard.Modifiers&ModifierKeys.Control)!=0,alt=(Keyboard.Modifiers&ModifierKeys.Alt)!=0,shift=(Keyboard.Modifiers&ModifierKeys.Shift)!=0;
        Func<Task>? action=e.Key switch {
            Key.S when ctrl=>async()=>{if(await SaveAsync(alt,shift)&&shift){allowClose=true;Close();}},
            Key.Z when ctrl=>()=>Edit("undo"),Key.Y when ctrl=>()=>Edit("redo"),Key.T when ctrl=>()=>Edit("crop"),Key.A when ctrl=>()=>Edit("select-all"),Key.D when ctrl=>()=>Edit("deselect"),
            Key.Delete=>()=>Edit("cut"),Key.Enter=>()=>PreviewAsync(!shift),Key.Add=>()=>Zoom(1.5),Key.Subtract=>()=>Zoom(1/1.5),Key.Divide=>()=>{waveform.Zoom=400;LayoutWaveform();return Task.CompletedTask;},
            Key.Escape=>()=>{Close();return Task.CompletedTask;},_=>null};
        if(action is not null){e.Handled=true;await Guard(action);}
    }
    private async void ClosingEditor(object? sender,CancelEventArgs e)
    {
        if(saving&&!allowClose){e.Cancel=true;return;}
        if(allowClose||document?.Modified!=true)return;
        var result=MessageBox.Show(this,"Save changes to this sound before closing?","Sound Editor",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(result==MessageBoxResult.No)return;e.Cancel=true;
        if(result==MessageBoxResult.Yes)await Guard(async()=>{if(await SaveAsync(false,true)){allowClose=true;Close();}});
    }
}
