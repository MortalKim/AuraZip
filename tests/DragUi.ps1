param([switch]$Multiple)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DragUiNative {
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out Point p);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
 [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h,uint flag);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint processId);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int ht,uint flags);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetPhysicalCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
'@
[void][DragUiNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$app=Get-Process AuraZip | Where-Object MainWindowTitle -like '*AuraZip-sample.zip*' | Select-Object -Last 1
if(!$app){throw 'Open artifacts/Samples/AuraZip-sample.zip before testing file drag-out.'}
$output=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/DragUi-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$shell=New-Object -ComObject Shell.Application
$shell.Open($output)
$explorer=$null
for($i=0;$i -lt 80;$i++){
 foreach($candidate in $shell.Windows()){try{if($candidate.LocationURL -and ([Uri]$candidate.LocationURL).LocalPath.TrimEnd('\') -eq $output.TrimEnd('\')){$explorer=$candidate;break}}catch{}}
 if($explorer){break};Start-Sleep -Milliseconds 100
}
if(!$explorer){throw 'The destination Explorer window did not open.'}
$explorerFrame=[DragUiNative]::GetAncestor([IntPtr]$explorer.HWND,2)
[void][DragUiNative]::ShowWindow($explorerFrame,9)
[void][DragUiNative]::SetForegroundWindow($explorerFrame)
Start-Sleep -Milliseconds 400
$original=[DragUiNative+Rect]::new();[void][DragUiNative]::GetWindowRect($app.MainWindowHandle,[ref]$original)
try{
 $screen=[System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
 $desktop=[System.Windows.Forms.SystemInformation]::VirtualScreen
 $half=[int]($screen.Width/2);$height=[Math]::Min(760,$screen.Height)
 [void][DragUiNative]::SetWindowPos($app.MainWindowHandle,[IntPtr](-1),$screen.Left,$screen.Top,$half,$height,0)
 [void][DragUiNative]::SetWindowPos($explorerFrame,[IntPtr](-1),($screen.Left+$half),$screen.Top,$half,$height,0)
 Start-Sleep -Milliseconds 400
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $item=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'README.md'),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
 if(!$item){throw 'The sample README.md is not visible.'}
 ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 if($Multiple){
  $second=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'LICENSE.txt'),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
  if(!$second){throw 'The sample LICENSE.txt is not visible.'}
  $second.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
 }
 $item.SetFocus()
 $scrollItem=$null
 if($item.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scrollItem)){$scrollItem.ScrollIntoView()}
 for($i=0;$i -lt 30;$i++){if(!$item.Current.IsOffscreen -and !$item.Current.BoundingRectangle.IsEmpty){break};Start-Sleep -Milliseconds 100}
 if($item.Current.IsOffscreen -or $item.Current.BoundingRectangle.IsEmpty){throw 'The sample README.md could not be brought into view.'}
 [void][DragUiNative]::ShowWindow($app.MainWindowHandle,9)
 [void][DragUiNative]::SetWindowPos($app.MainWindowHandle,[IntPtr](-1),$screen.Left,$screen.Top,$half,$height,0)
 [void][DragUiNative]::SetForegroundWindow($app.MainWindowHandle)
 Start-Sleep -Milliseconds 300
 $rect=$item.Current.BoundingRectangle;$x=[int]($rect.X+70);$y=[int]($rect.Y+$rect.Height/2)
 $targetX=[int]($screen.Left+$half+$half/2);$targetY=[int]($screen.Top+$height/2)
 [void][DragUiNative]::SetPhysicalCursorPos($x,$y)
 Start-Sleep -Milliseconds 100
 $sourcePoint=[DragUiNative+Point]::new();[void][DragUiNative]::GetPhysicalCursorPos([ref]$sourcePoint)
 $sourceHit=[DragUiNative]::WindowFromPoint($sourcePoint);$sourcePid=[uint32]0
 [void][DragUiNative]::GetWindowThreadProcessId($sourceHit,[ref]$sourcePid)
 if($sourcePid -ne $app.Id){throw ('Another window covers the drag source. Expected PID='+$app.Id+'; hit PID='+$sourcePid)}
 [DragUiNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 150
 for($i=1;$i -le 25;$i++){$moveX=[int]($x+($targetX-$x)*$i/25);$moveY=[int]($y+($targetY-$y)*$i/25);[DragUiNative]::mouse_event(0xC001,[uint32](($moveX-$desktop.Left)*65535/($desktop.Width-1)),[uint32](($moveY-$desktop.Top)*65535/($desktop.Height-1)),0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 35}
 for($wait=0;$wait -lt 80;$wait++){
  $status=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'StatusText'))
  [DragUiNative]::mouse_event(0xC001,[uint32](($targetX+($wait%2)-$desktop.Left)*65535/($desktop.Width-1)),[uint32](($targetY-$desktop.Top)*65535/($desktop.Height-1)),0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  if($wait -gt 10 -and ($status.Current.Name -in @('就绪','Ready') -or $status.Current.Name -like '*文件已准备*' -or $status.Current.Name -like 'Files are ready*')){break}
 }
 # Changing the foreground window during an active OLE drag can cancel capture.
 # Both test windows are already placed side by side before the gesture starts.
 [DragUiNative]::mouse_event(0xC001,[uint32](($targetX-$desktop.Left)*65535/($desktop.Width-1)),[uint32](($targetY-$desktop.Top)*65535/($desktop.Height-1)),0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 250
 $actual=[DragUiNative+Point]::new();[void][DragUiNative]::GetPhysicalCursorPos([ref]$actual)
 $rootHit=[DragUiNative]::GetAncestor([DragUiNative]::WindowFromPoint($actual),2)
 if($rootHit -ne $explorerFrame){throw 'Another window covers the Explorer drop target.'}
 [DragUiNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 for($i=0;$i -lt 100;$i++){if(Test-Path -LiteralPath (Join-Path $output 'README.md')){break};Start-Sleep -Milliseconds 100}
 if(!(Test-Path -LiteralPath (Join-Path $output 'README.md'))){$status=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'StatusText'));throw ('Dragging did not produce the expected file in Explorer. App status: '+$status.Current.Name)}
 if((Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $output 'README.md')) -notlike '*原生版本验证压缩包*'){throw 'The dropped file does not contain the expected data.'}
 if($Multiple){
  for($i=0;$i -lt 50;$i++){if(Test-Path -LiteralPath (Join-Path $output 'LICENSE.txt')){break};Start-Sleep -Milliseconds 100}
  if(!(Test-Path -LiteralPath (Join-Path $output 'LICENSE.txt'))){throw 'The second selected file was not dropped.'}
  Write-Output 'PASS dragging multiple selected files preserves the complete selection'
 }
 Write-Output "PASS dragging a real archive entry into Explorer: $output"
}
finally{
 [DragUiNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 [void][DragUiNative]::SetWindowPos($app.MainWindowHandle,[IntPtr](-2),$original.Left,$original.Top,($original.Right-$original.Left),($original.Bottom-$original.Top),0)
 $explorer.Quit()
 [void][DragUiNative]::SetForegroundWindow($app.MainWindowHandle)
}
