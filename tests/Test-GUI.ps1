[CmdletBinding()]
param([switch]$ColumnsOnly,[switch]$DetailsOnly,[switch]$AllowDesktopInput)
$ErrorActionPreference='Stop'
if(-not $AllowDesktopInput){throw 'Interactive GUI tests move the mouse and change window focus. Run explicitly with -AllowDesktopInput only when you want desktop input tests.'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
if(-not ('SeekMouseTest' -as [type])) { Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
public static class SeekMouseTest {
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd,ref POINT point);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 public delegate bool EnumProc(IntPtr hwnd,IntPtr ignored);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr owner,EnumProc callback,IntPtr ignored);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder name,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder name,int count);
 [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
 [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")] static extern IntPtr ReadText(IntPtr hwnd,uint message,IntPtr wparam,StringBuilder text);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")] static extern IntPtr WriteText(IntPtr hwnd,uint message,IntPtr wparam,string text);
 public static IntPtr[] Children(IntPtr owner,string className){var result=new List<IntPtr>();EnumProc callback=(hwnd,ignored)=>{var name=new StringBuilder(256);GetClassName(hwnd,name,name.Capacity);if(name.ToString()==className)result.Add(hwnd);return true;};EnumChildWindows(owner,callback,IntPtr.Zero);return result.ToArray();}
 public static string[] ComboItems(IntPtr hwnd){int count=(int)SendMessage(hwnd,0x146,IntPtr.Zero,IntPtr.Zero);if(count<0||count>1000)return new string[0];var result=new List<string>();for(int i=0;i<count;i++){int length=(int)SendMessage(hwnd,0x149,new IntPtr(i),IntPtr.Zero);if(length<0||length>4096)continue;var text=new StringBuilder(length+1);ReadText(hwnd,0x148,new IntPtr(i),text);result.Add(text.ToString());}return result.ToArray();}
 [StructLayout(LayoutKind.Sequential)] struct RECT {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
 public static void ComboSelect(IntPtr hwnd,int index){RECT rect;GetWindowRect(hwnd,out rect);uint pid;GetWindowThreadProcessId(hwnd,out pid);RealClick(GetAncestor(hwnd,2),(int)pid,rect.Right-8,(rect.Top+rect.Bottom)/2,false,true);foreach(ushort key in ComboKeys(index)){INPUT down=new INPUT{Type=1,Keyboard=new KEY{VirtualKey=key}},up=new INPUT{Type=1,Keyboard=new KEY{VirtualKey=key,Flags=2}};if(SendInput(2,new[]{down,up},40)!=2)throw new InvalidOperationException("Combo keyboard input failed.");}System.Threading.Thread.Sleep(100);}
 static IEnumerable<ushort> ComboKeys(int index){yield return 0x24;for(int i=0;i<index;i++)yield return 0x28;yield return 0xD;}
 public static void FileName(IntPtr dialog,string path){foreach(var hwnd in Children(dialog,"Edit"))if(GetDlgCtrlID(hwnd)==1001){WriteText(hwnd,0xC,IntPtr.Zero,path);return;}throw new InvalidOperationException("Filename input is missing.");}
 public static void NativeButton(IntPtr dialog,string label){foreach(var hwnd in Children(dialog,"Button")){var text=new StringBuilder(256);GetWindowText(hwnd,text,text.Capacity);if(text.ToString().Replace("&","")==label){SendMessage(hwnd,0xF5,IntPtr.Zero,IntPtr.Zero);return;}}throw new InvalidOperationException("Missing dialog button "+label);}
 [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [StructLayout(LayoutKind.Sequential)] struct MOUSE {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Sequential)] struct KEY {public ushort VirtualKey,ScanCode;public uint Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Explicit,Size=40)] struct INPUT {[FieldOffset(0)]public uint Type;[FieldOffset(8)]public MOUSE Mouse;[FieldOffset(8)]public KEY Keyboard;}
 [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,INPUT[] input,int size);
 static void Input(uint flags){if(SendInput(1,new[]{new INPUT{Mouse=new MOUSE{Flags=flags}}},40)!=1)throw new InvalidOperationException("Mouse input failed.");}
 public static void RealClick(IntPtr hwnd,int processId,double x,double y,bool right=false,bool keepFocus=false){RealPointer(hwnd,processId,x,y,x,y,right,keepFocus);}
 public static void RealDrag(IntPtr hwnd,int processId,double x,double y,double endX,double endY){RealPointer(hwnd,processId,x,y,endX,endY,false,false);}
 static void RealPointer(IntPtr hwnd,int processId,double x,double y,double endX,double endY,bool right,bool keepFocus){var dpi=SetThreadDpiAwarenessContext(new IntPtr(-4));POINT saved;GetCursorPos(out saved);var focus=GetForegroundWindow();bool down=false;try{SetForegroundWindow(hwnd);SetCursorPos((int)x,(int)y);System.Threading.Thread.Sleep(50);uint pid;GetWindowThreadProcessId(WindowFromPoint(new POINT{X=(int)x,Y=(int)y}),out pid);if(pid!=processId)throw new InvalidOperationException("Pointer target is outside the test process: expected "+processId+", hit "+pid+", at "+x+","+y);Input(right?8u:2u);down=true;System.Threading.Thread.Sleep(50);SetCursorPos((int)endX,(int)endY);System.Threading.Thread.Sleep(50);Input(right?16u:4u);down=false;System.Threading.Thread.Sleep(80);}finally{if(down)Input(right?16u:4u);SetCursorPos(saved.X,saved.Y);if(!keepFocus&&focus!=IntPtr.Zero)SetForegroundWindow(focus);if(dpi!=IntPtr.Zero)SetThreadDpiAwarenessContext(dpi);}}
 static POINT previousPoint;static IntPtr previousWindow;static bool active;
 public static void Send(IntPtr hwnd,double screenX,double screenY,uint message,int buttons) {
  var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
  try {
   if(!active){GetCursorPos(out previousPoint);previousWindow=GetForegroundWindow();active=true;SetForegroundWindow(hwnd);}
   SetCursorPos((int)screenX,(int)screenY);
   var p=new POINT {X=(int)screenX,Y=(int)screenY};ScreenToClient(hwnd,ref p);var location=new IntPtr((p.Y<<16)|(p.X&65535));SendMessage(hwnd,0x200,new IntPtr(buttons),location);SendMessage(hwnd,message,new IntPtr(buttons),location);
   if(message==0x202)Restore();
  }
  finally { if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous); }
 }
 public static void Context(IntPtr hwnd,double screenX,double screenY) {
  var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
  try {SetForegroundWindow(hwnd);SendMessage(hwnd,0x7B,hwnd,new IntPtr(((int)screenY<<16)|((int)screenX&65535)));}
  finally {if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
 }
 public static void Restore(){if(active){active=false;SetCursorPos(previousPoint.X,previousPoint.Y);if(previousWindow!=IntPtr.Zero)SetForegroundWindow(previousWindow);}}
}
'@
}
$pipeName='SoundboardLab.GUI.'+[Guid]::NewGuid().ToString('N')
$projectRoot=Split-Path $PSScriptRoot -Parent
$remote=Join-Path $projectRoot 'Remote.ps1'
$results=[Collections.Generic.List[string]]::new()
$runRoot=Join-Path $PSScriptRoot ('gui-state-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
@{SpeakerVolume=0;AlwaysOnTop=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runRoot 'preferences.json')
function Check($condition,$name) { if(-not $condition){throw $name};$results.Add('PASS '+$name);Write-Host ('PASS '+$name) }
function Request($data) { $value=& $remote -PipeName $pipeName -Command $data.command -Json ($data|ConvertTo-Json -Compress)|ConvertFrom-Json;if(-not $value.ok){throw $value.error};return $value }
function FindName($name,$type) {
    $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$type))
    $element=$null
    for($attempt=0;$attempt -lt 40;$attempt++) { $element=$script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition);if($element){break};Start-Sleep -Milliseconds 50 }
    if(-not $element){$script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name+' '+$_.Current.ControlType.ProgrammaticName}|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'gui-missing-control.txt');throw ('Missing UI control: '+$name)};return $element
}
function FindId($id) {
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    for($attempt=0;$attempt -lt 40;$attempt++) {
        $element=$script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if($element){return $element};Start-Sleep -Milliseconds 50
    }
    throw ('Missing UI control ID: '+$id)
}
function InvokeButton($name) { (FindName $name ([System.Windows.Automation.ControlType]::Button)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 100 }
function MenuAction($name) {
    (FindName 'Window' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    if($name -eq 'Search') {
      (FindName 'Window' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
      (FindName 'Edit' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    } elseif($name -eq 'Hotbar') {
      (FindName 'Hotbar' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
      $name=if((Request @{command='state'}).hotbarVisible){'Close hotbar'}else{'Open hotbar'}
    } else {
      (FindName 'Categories' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
      if($name -eq 'Category information'){$name='Show info'}else{$open=$script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Open categories'));$name=if($open){'Open categories'}else{'Close categories'}}
    }
    (FindName $name ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 100
}
function SeekMouse($fraction,$message=0x201,$buttons=1) {
    $rect=(FindId 'SeekSlider').Current.BoundingRectangle
    [SeekMouseTest]::Send($app.MainWindowHandle,$rect.X+$rect.Width*$fraction,$rect.Y+$rect.Height/2,$message,$buttons)
}
function New-SilentClip($path,$seconds) {
@'
import wave,sys
with wave.open(sys.argv[1], "wb") as audio:
    audio.setnchannels(2)
    audio.setsampwidth(2)
    audio.setframerate(48000)
    audio.writeframes(bytes(48000*4*int(sys.argv[2])))
'@ | & python - $path $seconds
    if($LASTEXITCODE -ne 0){throw 'Seek fixture generation failed'}
}
$app=Start-Process -FilePath (Join-Path $projectRoot 'app\bin\Debug\net10.0-windows\SoundboardLab.exe') -WindowStyle Hidden -ArgumentList @('--data-dir',('"'+$runRoot+'"'),'--pipe-name',$pipeName) -PassThru
try {
    $initial=Request @{command='state'};if($initial.processId -ne $app.Id){throw 'GUI test connected to the wrong app instance'};$app.Refresh()
    $script:root=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $initial|ConvertTo-Json -Depth 8|Set-Content (Join-Path $PSScriptRoot 'menu-render-state.json')
    Check (@($initial.renderedMenuLabels|Where-Object {$_ -match '\S'}).Count -eq 5) 'all five top menu headings contain rendered text'
    $grid=FindId 'SoundGrid';Check ($grid.Current.BoundingRectangle.Height -gt 200) 'compact sound list occupies the main workspace'
    if($DetailsOnly){. (Join-Path $PSScriptRoot 'GUI-Details.ps1')}
    elseif($ColumnsOnly){
    function ClickSoundHeader($name) {
        $header=$grid.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::HeaderItem)))
        $bounds=$header.Current.BoundingRectangle
        [SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2)
        Start-Sleep -Milliseconds 100
    }
    $testClip=Join-Path $runRoot 'long.wav';$secondClip=Join-Path $runRoot 'second.wav'
    New-SilentClip $testClip 4
    Copy-Item -LiteralPath $testClip -Destination $secondClip
    $shortClip=Join-Path $runRoot 'short.wav'
    New-SilentClip $shortClip 2
    Request @{command='add';paths=@($testClip,$secondClip,$shortClip)}|Out-Null
    Request @{command='rename';index=0;title='Zulu'}|Out-Null
    Request @{command='rename';index=1;title='Alpha'}|Out-Null
    Request @{command='rename';index=2;title='Mike'}|Out-Null
    Start-Sleep -Milliseconds 250
    $grid=FindId 'SoundGrid'
    $bounds=$grid.Current.BoundingRectangle;[SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+120,$bounds.Y+180)
    ClickSoundHeader 'Tag'
    Request @{command='state'}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $PSScriptRoot 'sort-state.json')
    Check (((Request @{command='state'}).visibleEntries.Title -join ',') -eq 'Alpha,Mike,Zulu') 'clicking Tag sorts sound titles in ascending order'
    ClickSoundHeader 'Tag'
    Check (((Request @{command='state'}).visibleEntries.Title -join ',') -eq 'Zulu,Mike,Alpha') 'clicking Tag again reverses the sort'
    ClickSoundHeader 'Duration'
    Check ((Request @{command='state'}).visibleEntries[0].Title -eq 'Mike') 'Duration sorts by numeric duration'
    ClickSoundHeader '#';ClickSoundHeader '#'
    Check (((Request @{command='state'}).visibleEntries.Title -join ',') -eq 'Mike,Alpha,Zulu') 'Index sorts by the original sound-list order'
    $tagHeader=$grid.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Tag'),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::HeaderItem)))
    $bounds=$tagHeader.Current.BoundingRectangle
    [SeekMouseTest]::Send($app.MainWindowHandle,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2,0x200,0)
    Start-Sleep -Milliseconds 100
    $hover=@((Request @{command='state'}).columnHeaders|Where-Object {$_.Title -eq 'Tag'})[0]
    Check ($hover.Hovered -and $hover.Background -eq '#FF484848') 'column headers visibly highlight under the pointer'
    [SeekMouseTest]::Restore()
    $layout=(Request @{command='state'}).categoryLayout
    Check ([Math]::Abs($layout.SelectionWidth-$layout.PaneWidth) -lt 2 -and [Math]::Abs($layout.SelectionLeft) -lt 2) 'My Sounds selection fills the category pane from edge to edge'
    Check ($layout.DividerWidth -eq 1 -and [Math]::Abs($layout.CountCenter-$layout.CountHeaderCenter) -lt 2) 'category count aligns with its divided header column'
    $tree=(FindId 'CategoryTree').Current.BoundingRectangle
    [SeekMouseTest]::RealDrag($app.MainWindowHandle,$app.Id,$tree.Right+2,$tree.Y+$tree.Height/2,$tree.Right+92,$tree.Y+$tree.Height/2)
    Start-Sleep -Milliseconds 100
    $wide=(Request @{command='state'}).categoryLayout
    Check ($wide.PaneWidth -gt $layout.PaneWidth+40 -and [Math]::Abs($wide.SelectionWidth-$wide.PaneWidth) -lt 2 -and [Math]::Abs($wide.CountCenter-$wide.CountHeaderCenter) -lt 2) 'resizing the category pane stretches selection and keeps counts aligned'
    Request @{command='category';name='Zulu category'}|Out-Null;Request @{command='move';index=1;category='Zulu category'}|Out-Null;Request @{command='move';index=2;category='Zulu category'}|Out-Null
    Request @{command='category';name='Alpha category'}|Out-Null;Request @{command='move';index=0;category='Alpha category'}|Out-Null;Request @{command='category';name='*'}|Out-Null
    $bounds=(FindId 'CategoryNameHeader').Current.BoundingRectangle;[SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2);Start-Sleep -Milliseconds 100
    Check (((Request @{command='state'}).categoryOrder -join ',') -eq '*,Alpha category,Zulu category') 'Category header sorts sibling categories by name'
    $bounds=(FindId 'CategoryCountHeader').Current.BoundingRectangle;[SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2);Start-Sleep -Milliseconds 80
    $bounds=(FindId 'CategoryCountHeader').Current.BoundingRectangle;[SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2);Start-Sleep -Milliseconds 100
    Check (((Request @{command='state'}).categoryOrder -join ',') -eq '*,Zulu category,Alpha category') 'category number header sorts by sound count'
    $bounds=(FindId 'ModeButton').Current.BoundingRectangle
    [SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2,$false,$true)
    Start-Sleep -Milliseconds 100
    $defaultMode=FindName 'Set play mode to default (speakers and microphone)' ([System.Windows.Automation.ControlType]::MenuItem)
    $modeMenu=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($defaultMode)
    $modeItems=$modeMenu.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem))
    Check (($modeItems.Current.Name -join '|') -eq 'Set play mode to default (speakers and microphone)|Set play mode to speakers|Set play mode to microphone|Preferences') 'left click opens the original four-item play-mode menu'
    Check (-not (FindName 'Set play mode to default (speakers and microphone)' ([System.Windows.Automation.ControlType]::MenuItem)).Current.IsEnabled) 'current playback mode is disabled in the menu'
    (FindName 'Set play mode to microphone' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Check ((Request @{command='state'}).playMode -eq 'Microphone') 'mode menu changes the selected mode without starting playback'
    $bounds=(FindId 'ModeButton').Current.BoundingRectangle
    [SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Y+$bounds.Height/2,$true,$true);Start-Sleep -Milliseconds 100
    Check (-not (FindName 'Set play mode to microphone' ([System.Windows.Automation.ControlType]::MenuItem)).Current.IsEnabled) 'right click opens the same play-mode menu with updated selection'
    (FindName 'Set play mode to speakers' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $soundValue=(FindId 'CategorySoundCount').Current.BoundingRectangle
    $selectedValue=(FindId 'SelectedCountLabel').Current.BoundingRectangle
    Check ([Math]::Abs($soundValue.X-$selectedValue.X) -lt 1) 'bottom Sounds and Selected numbers align in their own column'
    $hotkeyHeader=$grid.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Hotkey'),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::HeaderItem)))
    $bounds=$hotkeyHeader.Current.BoundingRectangle
    [SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.X+$bounds.Width/2,$bounds.Bottom+16,$false,$true)
    for($attempt=0;$attempt -lt 30;$attempt++){if((Request @{command='tool-state'}).windows.Title -contains 'Edit hotkey'){break};Start-Sleep -Milliseconds 50}
    Check ((Request @{command='tool-state'}).windows.Title -contains 'Edit hotkey') 'clicking a Hotkey cell opens Edit hotkey'
    Request @{command='close-tools'}|Out-Null
    Request @{command='select';index=-1}|Out-Null
    $editMenu=FindName 'Edit' ([System.Windows.Automation.ControlType]::MenuItem)
    $editMenu.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $editItems=$editMenu.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem))
    Check (($editItems.Current.Name -join '|') -eq 'Undo|Redo|Cut|Copy|Paste|Remove selected entries|Shuffle list|Select all|Select recently played file|Find dead entries|Search') 'Edit menu matches the observed original entries and order'
    Check (-not (FindName 'Cut' ([System.Windows.Automation.ControlType]::MenuItem)).Current.IsEnabled -and -not (FindName 'Copy' ([System.Windows.Automation.ControlType]::MenuItem)).Current.IsEnabled -and -not (FindName 'Remove selected entries' ([System.Windows.Automation.ControlType]::MenuItem)).Current.IsEnabled) 'Edit disables selection-dependent commands when no sound is selected'
    $editMenu.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'gui-headers-categories.png')}|Out-Null
    }else{
    Check (-not (Request @{command='state'}).hotbarVisible) 'default window hides the hotbar shortcut tiles, hamburger and numbered pages'
    Check ($script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Uncategorized')).Count -eq 0) 'category list has no invented Uncategorized row'
    $before=$grid.Current.BoundingRectangle.Height;MenuAction 'Hotbar';Check ($grid.Current.BoundingRectangle.Height -lt $before-80) 'Window menu opens the optional hotbar';MenuAction 'Hotbar'
    Check ($grid.Current.BoundingRectangle.Height -ge $before-1) 'closing the optional hotbar returns its space to the list'
    $before=$grid.Current.BoundingRectangle.Width;MenuAction 'Categories';Check ($grid.Current.BoundingRectangle.Width -gt $before+100) 'Window menu closes the categories pane';MenuAction 'Categories'
    $before=$grid.Current.BoundingRectangle.Height;MenuAction 'Category information';Check ($grid.Current.BoundingRectangle.Height -gt $before+50) 'Window menu toggles category information';MenuAction 'Category information'
    MenuAction 'Search';Check (-not (FindId 'SearchBox').Current.IsOffscreen) 'Find opens the search field';(FindId 'CloseSearchButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    MenuAction 'Hotbar'
    $shortcutText=$script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Ctrl+Alt+F1'))
    Check ($shortcutText.Count -eq 0) 'optional hotbar starts without invented shortcut labels'
    Check ((Request @{command='state'}).hotbarPages -eq 1 -and (Request @{command='state'}).hotbarRows -eq 2 -and (Request @{command='state'}).hotbarColumns -eq 6) 'optional hotbar starts as a two-by-six grid with one page'
    InvokeButton 'Lock hotbar';Check ((Request @{command='state'}).hotbarLocked) 'hotbar lock button locks editing'
    InvokeButton 'Hotbar menu'
    $lockItem=FindName 'Lock hotbar drag and drop' ([System.Windows.Automation.ControlType]::MenuItem)
    Check ($lockItem.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'three-bar hotbar menu shares the lock state'
    $lockItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Check (-not (Request @{command='state'}).hotbarLocked) 'hotbar context menu unlocks the toolbar control'
    InvokeButton 'Add hotbar page'
    InvokeButton '1'
    InvokeButton '2'
    Check ((Request @{command='state'}).hotbarPage -eq 1) 'numbered hotbar buttons select the corresponding page'
    $pages=(Request @{command='state'}).hotbarPages;InvokeButton 'Add hotbar page'
    $added=Request @{command='state'};Check ($added.hotbarPages -eq $pages+1 -and $added.hotbarPage -eq $pages) 'plus beside the page strip creates and selects a new page'
    InvokeButton 'Show or hide log';Check (-not (FindId 'LogTextBox').Current.IsOffscreen) 'speech bubble opens the playback log';InvokeButton 'Show or hide log'
    MenuAction 'Hotbar'
    $testClip=Join-Path $runRoot 'seek-test.wav'
    New-SilentClip $testClip 4
    Request @{command='add';paths=@($testClip)}|Out-Null
    Request @{command='select';index=0}|Out-Null;Request @{command='volume';value=0}|Out-Null
    InvokeButton 'Play on speakers only'
    for($attempt=0;$attempt -lt 20;$attempt++){$started=Request @{command='state'};if($started.playing){break};Start-Sleep -Milliseconds 50}
    Check $started.playing 'blue speaker toolbar button starts real playback'
    $range=(FindId 'SeekSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $cursorPositions=[Collections.Generic.List[double]]::new();$cursorTime=[Diagnostics.Stopwatch]::StartNew()
    while($cursorTime.Elapsed.TotalMilliseconds -lt 250){$cursorPositions.Add($range.Current.Value);Start-Sleep -Milliseconds 8}
    $advances=0;for($i=1;$i -lt $cursorPositions.Count;$i++){if($cursorPositions[$i] -gt $cursorPositions[$i-1]+.0001){$advances++}}
    Check ($advances -ge 6) 'running seek cursor advances between coarse audio progress notifications'
    $beforeHold=Request @{command='state'}
    SeekMouse .65
    Start-Sleep -Milliseconds 180
    $clicked=Request @{command='state'}
    $clicked|ConvertTo-Json -Depth 8|Set-Content (Join-Path $PSScriptRoot 'seek-click-results.json')
    Check ($clicked.playing -and $clicked.position -gt $beforeHold.position+.1 -and $clicked.position -lt $beforeHold.position+.6 -and $clicked.seekingWithMouse) 'holding the seek track leaves the current audio playing uninterrupted'
    Check ($clicked.seekCursorVisible -and [Math]::Abs($clicked.displayPosition-2.6) -lt .08) 'mouse-down moves the cursor to the pointer and holds its preview while audio continues'
    SeekMouse .65 0x202 0
    Start-Sleep -Milliseconds 80
    $released=Request @{command='state'}
    Check ($released.playing -and $released.position -gt 2.4 -and $released.position -lt 3) 'releasing the seek track applies the new position while playback is still active'
    InvokeButton 'Pause or resume';Check (Request @{command='state'}).paused 'pause toolbar icon pauses playback'
    $pausedCursor=$range.Current.Value;Start-Sleep -Milliseconds 100;Check ([Math]::Abs($range.Current.Value-$pausedCursor) -lt .001) 'rendered cursor stays still while paused'
    Request @{command='seek';seconds=.1}|Out-Null;Start-Sleep -Milliseconds 80;Check ([Math]::Abs($range.Current.Value-.1) -lt .01) 'rendered cursor snaps to a backward seek while paused'
    SeekMouse .75;SeekMouse .75 0x202 0;Start-Sleep -Milliseconds 80
    $clicked=Request @{command='state'}
    Check ($clicked.paused -and [Math]::Abs($clicked.position-3) -lt .08) 'click-to-seek works while paused without resuming playback'
    SeekMouse .2;SeekMouse .2 0x202 0;Start-Sleep -Milliseconds 80
    $clicked=Request @{command='state'}
    Check ($clicked.paused -and [Math]::Abs($clicked.position-.8) -lt .08) 'backward track clicks seek to the clicked location'
    SeekMouse .25;SeekMouse .55 0x200 1;Start-Sleep -Milliseconds 60
    $dragged=Request @{command='state'}
    Check ($dragged.paused -and [Math]::Abs($dragged.position-.8) -lt .08 -and [Math]::Abs($dragged.displayPosition-2.2) -lt .08) 'dragging previews the pointer position without seeking paused audio until release'
    SeekMouse .55 0x202 0
    Start-Sleep -Milliseconds 80
    Check ([Math]::Abs((Request @{command='state'}).position-2.2) -lt .08) 'releasing a drag seeks to the release position without resuming paused audio'
    InvokeButton 'Stop playback';Check (-not (Request @{command='state'}).playing) 'stop toolbar button ends playback'
    Request @{command='play';index=0;mode='Speakers'}|Out-Null
    Request @{command='seek';seconds=3.4}|Out-Null
    Start-Sleep -Milliseconds 80
    SeekMouse .2
    Start-Sleep -Milliseconds 850
    $heldCompleted=Request @{command='state'}
    Check (-not $heldCompleted.playing -and -not $heldCompleted.seekCursorVisible -and -not $heldCompleted.seekingWithMouse -and $heldCompleted.displayPosition -eq 0) 'playback finishing during a seek hold hides the cursor and cancels the gesture'
    SeekMouse .2 0x202 0
    Start-Sleep -Milliseconds 80
    $releasedCompleted=Request @{command='state'}
    Check (-not $releasedCompleted.playing -and $releasedCompleted.position -eq 0 -and -not $releasedCompleted.seekCursorVisible) 'releasing after completion does not seek or restore the cursor'
    Request @{command='play';index=0;mode='Speakers'}|Out-Null
    Request @{command='seek';seconds=3.7}|Out-Null
    Start-Sleep -Milliseconds 650
    $completed=Request @{command='state'}
    Check (-not $completed.playing -and $completed.position -eq 0 -and $completed.displayPosition -eq 0) 'natural playback completion returns engine and displayed cursor to the start'
    $prefsFile=Join-Path $runRoot 'preferences.json'
    $repeat=FindName 'Repeat current file' ([System.Windows.Automation.ControlType]::CheckBox)
    $repeat.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    (FindName 'Play' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (FindName 'Play mode' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (FindName 'Speakers' ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $beforeRepeat=(Request @{command='state'}).entries[0].playCount
    Request @{command='play';index=0;mode='Speakers'}|Out-Null
    Request @{command='seek';seconds=3.7}|Out-Null
    Start-Sleep -Milliseconds 650
    $repeated=Request @{command='state'}
    Check ($repeated.playing -and $repeated.entries[0].playCount -ge $beforeRepeat+2 -and $repeated.position -lt 2) 'completion still starts a fresh playback when repeat is enabled'
    InvokeButton 'Stop playback';$repeat.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $secondClip=Join-Path $runRoot 'second.wav';Copy-Item -LiteralPath $testClip -Destination $secondClip
    Request @{command='add';paths=@($secondClip)}|Out-Null
    (FindName 'Continue to next file' ([System.Windows.Automation.ControlType]::CheckBox)).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Request @{command='play';index=0;mode='Speakers'}|Out-Null
    Request @{command='seek';seconds=3.7}|Out-Null
    Start-Sleep -Milliseconds 650
    $continued=Request @{command='state'}
    Check ($continued.playing -and $continued.entries[1].playCount -eq 1 -and $continued.position -lt 2) 'continuous playback advances to the next sound after a cursor reset'
    Request @{command='seek';seconds=3.7}|Out-Null;Start-Sleep -Milliseconds 650
    $finishedList=Request @{command='state'}
    Check (-not $finishedList.playing -and $finishedList.position -eq 0 -and $finishedList.displayPosition -eq 0) 'last sound in continuous playback returns the cursor to zero without looping the list'
    (FindName 'Continue to next file' ([System.Windows.Automation.ControlType]::CheckBox)).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $row=$grid.Current.BoundingRectangle
    [SeekMouseTest]::Send($app.MainWindowHandle,$row.X+100,$row.Y+65,0x204,2)
    [SeekMouseTest]::Send($app.MainWindowHandle,$row.X+100,$row.Y+65,0x205,0)
    [SeekMouseTest]::Restore();[SeekMouseTest]::Context($app.MainWindowHandle,$row.X+100,$row.Y+65);Start-Sleep -Milliseconds 150
    $context=FindName 'Play (speakers and microphone)' ([System.Windows.Automation.ControlType]::MenuItem)
    Check ($context.Current.AcceleratorKey -match 'Enter') 'sound context menu displays the observed Enter shortcut'
    $rename=FindName 'Rename file' ([System.Windows.Automation.ControlType]::MenuItem)
    $tag=FindName 'Edit tag text' ([System.Windows.Automation.ControlType]::MenuItem)
    $editFile=FindName 'Edit file' ([System.Windows.Automation.ControlType]::MenuItem)
    Check ($rename.Current.AcceleratorKey -match 'F2' -and $tag.Current.AcceleratorKey -match 'Shift.*F2' -and $editFile.Current.AcceleratorKey -match 'Ctrl.*E') 'sound context menu uses F2, Shift+F2 and Ctrl+E for their original actions'
    $bars=$script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ScrollBar))
    $visibleBars=@($bars|Where-Object {-not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 -and $_.Current.BoundingRectangle.X -gt $context.Current.BoundingRectangle.X})
    Check ($visibleBars.Count -eq 0) 'fitting sound context menu has no visible scrollbars'
    [SeekMouseTest]::Send($app.MainWindowHandle,$row.X+200,$row.Y+120,0x201,1)
    [SeekMouseTest]::Send($app.MainWindowHandle,$row.X+200,$row.Y+120,0x202,0)
    $script:root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(760,450)
    Start-Sleep -Milliseconds 150
    Check ((FindId 'SeekSlider').Current.BoundingRectangle.Width -gt 100 -and $grid.Current.BoundingRectangle.Height -gt 50) 'transport and sound list remain usable at the minimum window size'
    Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'gui-compact.png')}|Out-Null
    $script:root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1020,700)
    Request @{command='open-tool';tool='settings'}|Out-Null
    $preferences=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Preferences')))
    if(-not $preferences){$preferences=$script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Preferences'))}
    Check ($null -ne $preferences) 'Preferences opens as a separate dialog'
    $tabs=$preferences.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TabItem))
    Check ($tabs.Count -eq 7 -and (($tabs|ForEach-Object {$_.Current.Name}) -join ',') -eq 'Audio,Hotkeys,Interface,Devices,Recorder,Editor,Notifications') 'Preferences uses the original seven tabs in their observed order'
    foreach($tab in $tabs) { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 60;Check (-not $tab.Current.IsOffscreen) ('Preferences tab '+$tab.Current.Name+' is reachable') }
    $mainRoot=$script:root;$script:root=$preferences
    function SelectPreference($id,$value) {
        Start-Sleep -Milliseconds 100
        $combo=FindId $id
        $index=switch($id){'Theme'{3}'Zoom'{1}'Language'{if($value -eq 'French (fr)'){11}else{8}}}
        $handle=[IntPtr]$preferences.Current.NativeWindowHandle
        [SeekMouseTest]::SetForegroundWindow($handle)|Out-Null
        $combo.SetFocus()
        [SeekMouseTest]::SendMessage($handle,0x100,[IntPtr]0x24,[IntPtr]0)|Out-Null
        [SeekMouseTest]::SendMessage($handle,0x101,[IntPtr]0x24,[IntPtr]0)|Out-Null
        for($i=0;$i -lt $index;$i++){[SeekMouseTest]::SendMessage($handle,0x100,[IntPtr]0x28,[IntPtr]0)|Out-Null;[SeekMouseTest]::SendMessage($handle,0x101,[IntPtr]0x28,[IntPtr]0)|Out-Null}
        Start-Sleep -Milliseconds 80
    }
    (FindName 'Hotkeys' ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $actions=(FindId 'SpecialHotkeyActions').FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    Check ($null -ne (FindName 'Stop playback' ([System.Windows.Automation.ControlType]::Text))) 'Special Hotkeys renders the recovered action list'
    (FindId 'SpecialHotkeyActions').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1,100)
    Check ($null -ne (FindName 'Select next hotbar page' ([System.Windows.Automation.ControlType]::Text))) 'Special Hotkeys scrolls through to the final recovered action'
    (FindName 'Audio' ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-audio.png')}|Out-Null
    Check (-not (FindName 'Apply' ([System.Windows.Automation.ControlType]::Button)).Current.IsEnabled) 'Apply begins disabled until a preference is edited'
    $measure=FindId 'MeasureVoiceVolume'
    Check ($measure.Current.BoundingRectangle.Width -lt 45) 'voice volume uses a compact measurement icon button'
    function CheckPreferenceHelp($id,$text) {
        $help=FindId $id;$help.SetFocus();$invoke=$help.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern);$invoke.Invoke()
        $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ToolTip))
        $tip=$null
        for($attempt=0;$attempt -lt 20;$attempt++){$tip=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition);if($tip){break};Start-Sleep -Milliseconds 50}
        $tipText=if($tip){($tip.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)).Current.Name -join ' ')}else{''}
        Check ($help.Current.HelpText.Contains($text) -and $tipText.Contains($text)) ($id+' opens its option description')
        $invoke.Invoke()
    }
    CheckPreferenceHelp 'DynamicNormalizationHelp' 'different volume levels'
    CheckPreferenceHelp 'FixedNormalizationHelp' 'already normalized'
    Check (-not (FindId 'PreferencesApply').Current.IsEnabled) 'opening option descriptions leaves preferences unchanged'
    (FindId 'FixedNormalization').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 100
    $fixed=FindId 'FixedAdjustmentDb';$fixed.SetFocus()
    $dropdown=$fixed.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $dropdown.Expand();Start-Sleep -Milliseconds 100
    Check ($fixed.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox -and $fixed.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)).Count -eq 21) 'fixed volume adjustment opens the original 21-choice decibel dropdown'
    $dropdown.Collapse()
    $fixed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('-15 dB')
    Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-normalization.png')}|Out-Null
    Check ((FindId 'MeasureFixedAdjustment').Current.BoundingRectangle.Width -lt 45 -and $null -eq $preferences.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Analyze sound files'))) 'fixed adjustment uses a measure icon instead of an Analyze sound files button'
    $activation=FindId 'VoiceActivationPath';$activation.SetFocus();Start-Sleep -Milliseconds 100
    $browse=(FindId 'BrowseVoiceActivation').Current.BoundingRectangle;$listen=(FindId 'PreviewVoiceActivation').Current.BoundingRectangle
    Check ($listen.X -ge $browse.Right-2 -and [Math]::Abs(($listen.Y+$listen.Height/2)-($browse.Y+$browse.Height/2)) -lt 3 -and $listen.Width -lt 45) 'voice activation preview sits immediately beside Browse on the same row'
    $edge=[System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($listen.Right-2,$listen.Y+$listen.Height/2))
    Check ($edge.Current.AutomationId -eq 'PreviewVoiceActivation') 'right edge of the activation preview remains fully visible and clickable'
    $delay=FindId 'PlaybackDelayMs';$delay.SetFocus();$delayValue=$delay.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $delayValue.SetValue('123');(FindId 'PlaybackDelayMsIncrease').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Check ($delayValue.Current.Value -eq '124') 'push-to-talk spinner increases its numeric value'
    (FindId 'PlaybackDelayMsDecrease').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Check ($delayValue.Current.Value -eq '123') 'push-to-talk spinner decreases its numeric value'
    foreach($invalid in @('abc','1.5','-1','10001')){$delayValue.SetValue($invalid)}
    Check ($delayValue.Current.Value -eq '123') 'push-to-talk input rejects letters, decimals, negative values and out-of-range values'
    $delayValue.SetValue('0');(FindId 'PlaybackDelayMsDecrease').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Check ($delayValue.Current.Value -eq '0') 'push-to-talk spinner stops at zero'
    $delayValue.SetValue('123')
    $karaoke=FindId 'KaraokeDelayMs';$karaoke.SetFocus();$karaokeValue=$karaoke.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $karaokeValue.SetValue('-25');(FindId 'KaraokeDelayMsIncrease').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Check ($karaokeValue.Current.Value -eq '-24') 'karaoke spinner supports signed millisecond values and increments'
    (FindId 'KaraokeDelayMsDecrease').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $karaokeValue.SetValue('text');$karaokeValue.SetValue('2.5')
    Check ($karaokeValue.Current.Value -eq '-25') 'karaoke input rejects noninteger text without losing its value'
    $reduction=FindId 'ReduceOtherApplications';$reduction.SetFocus();Start-Sleep -Milliseconds 100
    CheckPreferenceHelp 'VolumeReductionHelp' 'browsers and other media players'
    $reductionNames=@('Mute all other sounds','Reduce the volume of other sounds by 80%','Reduce the volume of other sounds by 50%')
    $reductionRadios=@($reductionNames|ForEach-Object {FindName $_ ([System.Windows.Automation.ControlType]::RadioButton)})
    Check (@($reductionRadios|Where-Object {$_.Current.IsEnabled -or $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected}).Count -eq 0) 'disabled volume reduction greys and clears every radio option'
    $reduction.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Check (@($reductionRadios|Where-Object {$_.Current.IsEnabled}).Count -eq 3 -and $reductionRadios[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'enabling volume reduction activates its options and restores the saved choice'
    $reductionRadios[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $reduction.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $reduction.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Check ($reductionRadios[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'volume reduction remembers a changed choice across disable and enable'
    $reduction.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-audio-controls.png')}|Out-Null
    (FindName 'Recorder' ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $fields=$preferences.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))
    $maximum=@($fields|Where-Object {$_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '300'})[0]
    $maximum.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('301')
    InvokeButton 'Apply'
    for($attempt=0;$attempt -lt 40;$attempt++){if((Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json).recordingLimitSeconds -eq 301){break};Start-Sleep -Milliseconds 50}
    Check ((Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json).recordingLimitSeconds -eq 301) 'Apply persists edited recorder settings without closing Preferences'
    $audioSaved=Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json
    Check ($audioSaved.playbackDelayMs -eq 123 -and $audioSaved.karaokeDelayMs -eq -25 -and $audioSaved.fixedAdjustmentDb -eq -15 -and -not $audioSaved.normalize -and -not $audioSaved.reduceOtherApplications) 'Apply persists the Audio dropdown, spinner values and disabled volume reduction'
    (FindName 'Interface' ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 100
    SelectPreference 'Theme' 'Navy'
    SelectPreference 'Zoom' '150%'
    (FindId 'DisableSorting').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    InvokeButton 'Apply'
    for($attempt=0;$attempt -lt 40;$attempt++){if((Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json).theme -eq 'Navy'){break};Start-Sleep -Milliseconds 50}
    $appearance=(Request @{command='state'}).appearance
    Check ($appearance.Theme -eq 'Navy' -and $appearance.Background -eq '#FF17202E') 'applying a theme changes the main application palette'
    Check ($appearance.Scale -eq 1.5 -and -not $appearance.Sorting) 'zoom and disable-sorting preferences change live controls'
    SelectPreference 'Language' 'French (fr)'
    InvokeButton 'Apply'
    for($attempt=0;$attempt -lt 40;$attempt++){if((Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json).language -eq 'French (fr)'){break};Start-Sleep -Milliseconds 50}
    $script:root=$mainRoot
    Check ($null -ne (FindName 'Fichier' ([System.Windows.Automation.ControlType]::MenuItem))) 'French preference translates the actual main menu'
    $script:root=$preferences
    SelectPreference 'Language' 'English (en_US)'
    (FindId 'PreferencesApply').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for($attempt=0;$attempt -lt 40;$attempt++){if((Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json).language -eq 'English (en_US)'){break};Start-Sleep -Milliseconds 50}
    (FindName 'Recorder' ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $maximum=FindId 'RecordingLimitSeconds'
    $maximum.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('302')
    InvokeButton 'Cancel';$script:root=$mainRoot
    Check ((Get-Content -LiteralPath $prefsFile -Raw|ConvertFrom-Json).recordingLimitSeconds -eq 301) 'Cancel discards subsequent edits after Apply'
    Request @{command='close-tools'}|Out-Null
    }
    $results | Set-Content -LiteralPath (Join-Path $PSScriptRoot $(if($DetailsOnly){'details-results.txt'}elseif($ColumnsOnly){'columns-results.txt'}else{'gui-results.txt'}))
    'PASS '+$results.Count+' GUI interaction checks'
} finally {
    [SeekMouseTest]::Restore()
    try { Request @{command='close-tools'}|Out-Null;Request @{command='close'}|Out-Null }catch{}
    if(-not $app.WaitForExit(5000)){$app.Kill();$app.WaitForExit()}
    if($app.ExitCode -ne 0){throw ('GUI test app exit code '+$app.ExitCode)}
}
