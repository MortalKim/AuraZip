param([string]$ScreenshotPath, [switch]$BringToFront)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$process = Get-Process AuraZip | Where-Object MainWindowHandle -ne 0 | Select-Object -Last 1
if (!$process) { throw 'AuraZip has no visible top-level window.' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if ($BringToFront) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeWindowInspection {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
}
'@
    [void][NativeWindowInspection]::ShowWindow($process.MainWindowHandle, 9)
    [void][NativeWindowInspection]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Milliseconds 400
}
$nodes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($node in $nodes) {
    $current = $node.Current
    if ($current.Name -or $current.AutomationId) { '{0} | {1} | {2}' -f $current.ControlType.ProgrammaticName, $current.AutomationId, $current.Name }
}
if ($ScreenshotPath) {
    Add-Type -AssemblyName System.Drawing
    $rect = $window.Current.BoundingRectangle
    $image = New-Object Drawing.Bitmap ([int]$rect.Width),([int]$rect.Height)
    $graphics = [Drawing.Graphics]::FromImage($image)
    $graphics.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$image.Size)
    $image.Save($ScreenshotPath)
    $graphics.Dispose(); $image.Dispose()
}
