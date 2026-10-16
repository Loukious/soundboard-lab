using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoundboardLab.Audio;
using SoundboardLab.Models;

namespace SoundboardLab;

public partial class MainWindow
{
    private MenuItem HotbarMenu = null!, RecentLibraries = null!;
    private MenuItem Item(string title, RoutedEventHandler? action = null, string gesture = "")
    {
        var item = new MenuItem { Header = title, Uid=title, InputGestureText = gesture };
        if(title=="Preferences")item.Icon=ModeMenuIcon((Geometry)FindResource("PreferencesIcon"));
        item.SubmenuOpened+=(_,_)=>{foreach(var child in item.Items.OfType<MenuItem>())if(child.Tag is Func<bool> read)child.IsChecked=read();};
        if (action is not null) item.Click += action;
        System.Windows.Automation.AutomationProperties.SetAcceleratorKey(item,gesture);
        return item;
    }
    private static void Add(ItemsControl menu, params object[] items) { foreach (var item in items) menu.Items.Add(item); }
    private MenuItem Toggle(string title, Func<bool> read, Action<bool> write)
    {
        var item=Item(title,(_,_)=>{});item.IsCheckable=true;item.IsChecked=read();item.Tag=read;
        item.Click+=(_,_)=>write(item.IsChecked);
        item.SubmenuOpened+=(_,_)=>item.IsChecked=read();
        return item;
    }
    private static MenuItem UnavailableMenu(string title,bool check=false)=>new(){Header=title,IsCheckable=check,IsChecked=check,IsEnabled=false,ToolTip="Not available in this preview."};
    private void ConfigureMenus()
    {
        var file=Item("_File");var edit=Item("_Edit");var play=Item("_Play");var window=Item("_Window");var help=Item("_Help");
        Add(MainMenu,file,edit,play,window,help);
        RecentLibraries=Item("Load recent sound list");RecentLibraries.SubmenuOpened+=(_,_)=>RecentLibraries.IsEnabled=RecentLibraries.Items.Count>0;
        var restore=Item("Restore sound list");Add(restore,Item("From backup…",RestoreBackup_Click));
        var export=Item("Export");Add(export,Item("Sound list and audio files…",Export_Click),Item("Preferences…",(_,_)=>ExportPreferences()));
        var stats=Item("Stats");Add(stats,Item("Sound list",(_,_)=>ShowText("Stats",$"Sounds: {library.Document.Entries.Count}\nPlay count: {library.Document.Entries.Sum(x=>(long)x.PlayCount)}")));
        Add(file,Item("Add sound files",AddFiles_Click),new Separator(),Item("New sound list",NewList_Click),Item("Load sound list",OpenList_Click),Item("Save sound list",SaveList_Click,"Ctrl+S"),Item("Save sound list as...",SaveListAs_Click),RecentLibraries,restore,export,stats,new Separator(),Item("Preferences",Settings_Click),new Separator(),Item("Exit",Exit_Click,"Alt+F4"));
        // Additional import sources remain available without changing the observed File menu.
        var undo=Item("Undo",Undo_Click,"Ctrl+Z");var redo=Item("Redo",Redo_Click,"Ctrl+Y");
        var recentSelect=RecentSounds("Select recently played file",false);
        var cut=Item("Cut",(_,_)=>CopyEntries(true),"Ctrl+X");var copy=Item("Copy",(_,_)=>CopyEntries(false),"Ctrl+C");var paste=Item("Paste",(_,_)=>PasteEntries(),"Ctrl+V");
        var remove=Item("Remove selected entries",Remove_Click,"Del");var shuffle=Item("Shuffle list",(_,_)=>ShuffleList());var selectAll=Item("Select all",(_,_)=>SoundGrid.SelectAll(),"Ctrl+A");var dead=Item("Find dead entries",(_,_)=>SelectMissingFiles());
        Add(edit,undo,redo,new Separator(),cut,copy,paste,remove,shuffle,new Separator(),selectAll,recentSelect,dead,new Separator(),Item("Search",Find_Click,"Ctrl+F"));
        edit.SubmenuOpened+=(_,_)=>{undo.IsEnabled=library.CanUndo;redo.IsEnabled=library.CanRedo;cut.IsEnabled=copy.IsEnabled=remove.IsEnabled=SoundGrid.SelectedItems.Count>0;paste.IsEnabled=copiedEntries.Length>0||Clipboard.ContainsFileDropList();shuffle.IsEnabled=library.Document.Entries.Count>1;selectAll.IsEnabled=dead.IsEnabled=library.Document.Entries.Count>0;recentSelect.IsEnabled=library.Document.Entries.Any(x=>x.LastPlayed.HasValue);};
        var randomAll=Item("Play random from all categories");
        Add(randomAll,RandomItem("Play (speakers and microphone)",PlaybackDestination.Both,true),RandomItem("Play on speakers",PlaybackDestination.Speakers,true),RandomItem("Play on microphone",PlaybackDestination.Microphone,true));
        Add(play,Item("Play selected file (speakers and microphone)",async (_,_)=>await PlaySelectedMode(PlaybackDestination.Both)),Item("Play selected file on speakers only",Speakers_Click),Item("Play selected file on microphone only",Microphone_Click),new Separator(),RandomItem("Play random file (speakers and microphone)",PlaybackDestination.Both),RandomItem("Play random file on speakers only",PlaybackDestination.Speakers),RandomItem("Play random file on microphone only",PlaybackDestination.Microphone),new Separator(),randomAll,new Separator(),RecentSounds("Play recently played file (speakers and microphone)",true),new Separator(),Item("Stop playback",Stop_Click),Item("Pause/resume playback",Pause_Click),Item("Play previous file",Previous_Click),Item("Play next file",Next_Click),new Separator(),Toggle("Continue playback after current file",()=>ContinueBox.IsChecked==true,x=>ContinueBox.IsChecked=x),Toggle("Repeat playback of sound list",()=>Settings.RepeatList,x=>Settings.RepeatList=x),Toggle("Repeat playback of current file",()=>RepeatBox.IsChecked==true,x=>RepeatBox.IsChecked=x),Toggle("Auto stop (recommended)",()=>Settings.StopOnNewSound,x=>Settings.StopOnNewSound=x),new Separator(),ModeMenu());
        play.SubmenuOpened+=(_,_)=> { foreach(var item in play.Items.OfType<MenuItem>().Where(x=>x.IsCheckable)) item.IsChecked=item.Uid switch {"Continue playback after current file"=>ContinueBox.IsChecked==true,"Repeat playback of sound list"=>Settings.RepeatList,"Repeat playback of current file"=>RepeatBox.IsChecked==true,_=>Settings.StopOnNewSound}; };
        var categories=CategoriesMenu();var hotbar=Item("Hotbar");var refreshHotbarMenu=PopulateHotbarMenu(hotbar,true);hotbar.SubmenuOpened+=(_,_)=>refreshHotbarMenu();
        var keyMenu=Item("Hotkeys");var keyEnabled=Toggle("Hotkeys enabled",()=>HotkeysBox.IsChecked==true,x=>HotkeysBox.IsChecked=x);
        var remember=Toggle("Remember hotkey status throughout application starts",()=>Settings.RememberHotkeyStatus,x=>Settings.RememberHotkeyStatus=x);
        var pass=Toggle("Pass hotkeys",()=>Settings.PassHotkeys,x=>{Settings.PassHotkeys=x;ConfigureHotkeys();});
        Add(keyMenu,keyEnabled,remember,pass,new Separator(),Item("Hotkey overview",HotkeyOverview_Click));keyMenu.SubmenuOpened+=(_,_)=>{keyEnabled.IsChecked=HotkeysBox.IsChecked==true;pass.IsChecked=Settings.PassHotkeys;};
        Add(window,categories,hotbar,keyMenu,Item("Sound recorder",Recorder_Click,"Ctrl+R"),Item("Color tool",Color_Click),Item("Text to speech",Tts_Click,"Ctrl+T"));
        var tools=Item("Preview tools");Add(tools,Item("Microphone setup…",MicrophoneSetup_Click),Item("Measure voice loudness…",MeasureVoice_Click),Item("Add directory…",AddFolder_Click),Item("Add sound from URL…",ImportUrl_Click),Item("Import Soundpad list…",ImportSoundpad_Click),Item("Import preferences…",(_,_)=>ImportPreferences()));
        Add(help,Item("Online manual",(_,_)=>OpenReadme()),UnavailableMenu("Online FAQ"),Item("Show log",ToggleLog_Click),Item("Troubleshooting",Diagnostics_Click),new Separator(),Item("About",About_Click,"F1"),tools);
        ConfigureSoundContext();ConfigureCategoryContext();ConfigurePlayModeMenu();ConfigureStatusMenus();
        SoundGrid.PreviewMouseRightButtonDown+=(_,e)=>{if(VisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is {} row&&!row.IsSelected){SoundGrid.SelectedItem=row.Item;}};
        CategoryTree.PreviewMouseRightButtonDown+=(_,e)=>{if(VisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is {} row){row.IsSelected=true;row.Focus();}};
    }
    private bool paginationAbove, categoriesRight;
    private bool lockHotbar {get=>Settings.HotbarLocked;set{Settings.HotbarLocked=value;UpdateHotbarLock();}}
    private MenuItem CategoriesMenu()
    {
        var menu=Item("Categories");var show=Item("Close categories",ToggleCategories_Click);show.IsCheckable=true;show.IsChecked=true;
        Add(menu,show,new Separator(),Item("Add category",AddCategory_Click),new Separator(),Item("All sounds",(_,_)=>{selectedCategory="*";RefreshLibrary();}),new Separator());
        foreach(var (name,size) in new[]{("Smaller",10d),("Small",12d),("Medium",15d),("Large",18d)}) {var option=Toggle(name,()=>CategoryTree.FontSize==size,x=>{if(x)CategoryTree.FontSize=size;});menu.Items.Add(option);}
        Add(menu,new Separator(),Toggle("Show right",()=>categoriesRight,x=>MoveCategoriesRight(x)),Toggle("Show info",()=>CategoryInfoPanel.Visibility==Visibility.Visible,x=>CategoryInfoPanel.Visibility=x?Visibility.Visible:Visibility.Collapsed),new Separator(),Item("Expand all",(_,_)=>ExpandCategories(true)),Item("Collapse all",(_,_)=>ExpandCategories(false)));
        menu.SubmenuOpened+=(_,_)=>{show.IsChecked=CategoriesPanel.Visibility==Visibility.Visible;show.Header=show.IsChecked?"Close categories":"Open categories";};return menu;
    }
    private void MoveCategoriesRight(bool right)
    {
        categoriesRight=right;var grid=(Grid)CategoriesPanel.Parent;var list=grid.Children.OfType<Grid>().First(x=>x!=grid);
        Grid.SetColumn(CategoriesPanel,right?2:0);Grid.SetColumn(list,right?0:2);
        grid.ColumnDefinitions[0].Width=right?new GridLength(1,GridUnitType.Star):new GridLength(180);
        grid.ColumnDefinitions[2].Width=right?new GridLength(180):new GridLength(1,GridUnitType.Star);
    }
    private void ExpandCategories(bool expand) {foreach(var item in CategoryTree.Items.OfType<TreeViewItem>()) Expand(item); void Expand(TreeViewItem item){item.IsExpanded=expand;foreach(var child in item.Items.OfType<TreeViewItem>())Expand(child);} }
    private MenuItem ModeMenu() {var menu=Item("Play mode");foreach(var (name,index) in new[]{("Default",0),("Speakers",1),("Microphone",2)}){var item=Toggle(name,()=>DestinationBox.SelectedIndex==index,x=>{if(x)DestinationBox.SelectedIndex=index;});menu.Items.Add(item);}menu.SubmenuOpened+=(_,_)=>{foreach(MenuItem item in menu.Items)item.IsChecked=menu.Items.IndexOf(item)==DestinationBox.SelectedIndex;};return menu;}
    private MenuItem RandomItem(string name,PlaybackDestination mode,bool all=false)=>Item(name,async (_,_)=>{var choices=(all?library.Document.Entries.AsEnumerable():view?.Cast<SoundEntry>()??[]).ToArray();if(choices.Length>0){randomPlayback=true;await PlayEntryAsync(choices[Random.Shared.Next(choices.Length)],mode);}});
    private async Task PlaySelectedMode(PlaybackDestination mode) { randomPlayback=false;if(Selected is {} sound)await PlayEntryAsync(sound,mode); }
    private MenuItem RecentSounds(string title,bool play)
    {
        var menu=Item(title);menu.SubmenuOpened+=(_,_)=>{menu.Items.Clear();foreach(var sound in library.Document.Entries.Where(x=>x.LastPlayed.HasValue).OrderByDescending(x=>x.LastPlayed).Take(20))menu.Items.Add(Item(sound.Title,async(_,_)=>{SoundGrid.SelectedItem=sound;SoundGrid.ScrollIntoView(sound);if(play)await PlayEntryAsync(sound,PlaybackDestination.Both);}));};
        menu.Items.Add(new MenuItem { Header="(No recently played files)",IsEnabled=false });return menu;
    }
    private void ConfigureSoundContext()
    {
        var context=new ContextMenu();var removeKey=Item("Remove hotkey",(_,_)=>{foreach(var sound in SoundGrid.SelectedItems.Cast<SoundEntry>()){if(Settings.SameHotkeysInAllCategories)Settings.RowHotkeys[Array.IndexOf(view?.Cast<SoundEntry>().ToArray()??[],sound)]="";else sound.Hotkey="";}SoundGrid.Items.Refresh();ConfigureHotkeys();});
        var color=Item("Color");PopulateSoundColorMenu(color,()=>SoundGrid.SelectedItems.Cast<SoundEntry>());color.SubmenuOpened+=(_,e)=>{if(ReferenceEquals(e.OriginalSource,color))PopulateSoundColorMenu(color,()=>SoundGrid.SelectedItems.Cast<SoundEntry>());};
        Add(context,Item("Play (speakers and microphone)",async (_,_)=>await PlaySelectedMode(PlaybackDestination.Both),"Enter"),Item("Play on speakers",Speakers_Click),Item("Play on microphone",Microphone_Click),new Separator(),Item("Remove",Remove_Click,"Del"),new Separator(),Item("Set hotkey",Hotkey_Click),removeKey,new Separator(),color,new Separator(),Item("Select in explorer",Explorer_Click),Item("Rename file",RenameFile_Click,"F2"),Item("Edit tag text",Rename_Click,"Shift+F2"),new Separator(),Item("Edit file",Editor_Click,"Ctrl+E"));
        context.Opened+=(_,_)=>removeKey.IsEnabled=SoundGrid.SelectedItems.Cast<SoundEntry>().Any(x=>ShownHotkey(x).Length>0);SoundGrid.ContextMenu=context;
    }
    private void ConfigureCategoryContext()
    {
        var context=new ContextMenu();var removeKey=Item("Remove hotkey",(_,_)=>{var category=library.Document.Categories.FirstOrDefault(x=>x.Name==selectedCategory);if(category is not null){category.Hotkey="";ConfigureHotkeys();}});
        Add(context,CategoriesMenu(),new Separator(),Item("Add",AddCategory_Click),Item("Edit",RenameCategory_Click,"F2"),Item("Remove",DeleteCategory_Click,"Del"),new Separator(),Item("Set hotkey",CategoryHotkey_Click),removeKey);
        context.Opened+=(_,_)=>removeKey.IsEnabled=library.Document.Categories.Any(x=>x.Name==selectedCategory&&x.Hotkey.Length>0);CategoryTree.ContextMenu=context;
    }
    private void ShuffleList() {view?.SortDescriptions.Clear();var entries=view?.Cast<SoundEntry>().OrderBy(_=>Random.Shared.Next()).ToArray()??[];foreach(var entry in entries)library.Document.Entries.Move(library.Document.Entries.IndexOf(entry),library.Document.Entries.Count-1);}
    private void SelectMissingFiles() {SoundGrid.SelectedItems.Clear();foreach(var entry in view?.Cast<SoundEntry>()??[]){entry.IsMissing=!File.Exists(entry.FilePath);if(entry.IsMissing)SoundGrid.SelectedItems.Add(entry);}}
    private void SelectAdjacent(int direction) {var entries=view?.Cast<SoundEntry>().ToArray()??[];if(entries.Length==0)return;int index=Array.IndexOf(entries,Selected);SoundGrid.SelectedItem=entries[Math.Clamp(index+direction,0,entries.Length-1)];SoundGrid.ScrollIntoView(SoundGrid.SelectedItem);}
    private void SetHotbarFormat() {if(Prompt("Set hotbar format","Rows × columns",$"{Settings.HotbarRows} × {Settings.HotbarColumns}") is not {} value)return;Guard(()=>{var parts=value.Split(['x','×',','],StringSplitOptions.TrimEntries);if(parts.Length!=2||!int.TryParse(parts[0],out int rows)||!int.TryParse(parts[1],out int columns)||rows is <1 or >12||columns is <1 or >12)throw new ArgumentException("Enter rows × columns, each between 1 and 12.");Settings.HotbarRows=rows;Settings.HotbarColumns=columns;RefreshHotbar();});}
    private void RemoveHotbarPage() {int page=HotbarPage.SelectedIndex;if(Settings.HotbarPages<=1)return;foreach(var pin in library.Document.HotbarPins.Where(x=>x.Page==page).ToArray())library.Document.HotbarPins.Remove(pin);foreach(var pin in library.Document.HotbarPins.Where(x=>x.Page>page))pin.Page--;Settings.HotbarPages--;RefreshHotbar();HotbarPage.SelectedIndex=Math.Min(page,Settings.HotbarPages-1);}
    private void OpenReadme() {Guard(()=>{string path=Path.Combine(AppContext.BaseDirectory,"README.md");if(!File.Exists(path))path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../README.md"));Process.Start(new ProcessStartInfo(path){UseShellExecute=true});});}
    private void ExportPreferences() {Guard(()=>{var dialog=new Microsoft.Win32.SaveFileDialog {Filter="Preferences JSON|*.json",FileName="preferences.json"};if(dialog.ShowDialog(this)==true)preferenceStore.Export(dialog.FileName);});}
    private async void ImportPreferences() {await GuardAsync(async()=>{var dialog=new Microsoft.Win32.OpenFileDialog {Filter="Preferences JSON|*.json"};if(dialog.ShowDialog(this)==true){preferenceStore.Import(dialog.FileName);await ApplySettingsAsync();preferenceStore.Save();}});}
    private void LaunchExternalEditor(string file)
    {
        if(!File.Exists(Settings.ExternalEditorPath))throw new FileNotFoundException("Select an existing editor in Preferences → Editor.");
        var start=new ProcessStartInfo(Settings.ExternalEditorPath){UseShellExecute=true};
        // The file is a single argument unless the user supplied their own argument template.
        if(Settings.ExternalEditorArguments=="{file}"||string.IsNullOrWhiteSpace(Settings.ExternalEditorArguments))start.ArgumentList.Add(file);
        else start.Arguments=Settings.ExternalEditorArguments.Replace("{file}","\""+file+"\"");
        Process.Start(start);
    }
}
