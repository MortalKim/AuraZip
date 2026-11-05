param([int]$AppProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FluentUiNative {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
[void][FluentUiNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$app = if ($AppProcessId) { Get-Process -Id $AppProcessId } else { Get-Process AuraZip | Where-Object MainWindowTitle -like '*AuraZip-sample.zip*' | Select-Object -Last 1 }
if (!$app -or !$app.MainWindowHandle) { throw 'Open artifacts/Samples/AuraZip-sample.zip before running this test.' }
[void][FluentUiNative]::SetForegroundWindow($app.MainWindowHandle)
$window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
$original = $window.Current.BoundingRectangle
$scale = [FluentUiNative]::GetDpiForWindow($app.MainWindowHandle) / 96.0
$output = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/FluentUi-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($output)
function Find-Id([string]$id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Find-Name([string[]]$names) {
    foreach ($name in $names) {
        $item=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
        if ($item) { return $item }
    }
}
function Check([string]$checkName,[scriptblock]$condition) {
    for ($attempt=0;$attempt -lt 50;$attempt++) { if (& $condition) { Write-Output "PASS $checkName"; return }; Start-Sleep -Milliseconds 100 }
    throw "UI check failed: $checkName"
}
function Invoke($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Capture([string]$name) {
    Start-Sleep -Milliseconds 300
    $rect=$window.Current.BoundingRectangle
    $bitmap=[Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$bitmap.Size); $bitmap.Save((Join-Path $output ($name+'.png')),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Change-Theme([string[]]$choice) {
    Check 'tools menu is available' { $null -ne (Find-Name @('工具','Tools')) }
    Invoke (Find-Name @('工具','Tools'))
    Check 'settings menu is available' { $null -ne (Find-Name @('设置','Settings')) }
    Invoke (Find-Name @('设置','Settings'))
    Check 'appearance selector is accessible' { $null -ne (Find-Id 'ThemeSelector') }
    $selector=Find-Id 'ThemeSelector'
    $script:previousTheme=$selector.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
    $selector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 150
    (Find-Name $choice).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Check 'settings save button is available' { $null -ne (Find-Name @('保存','Save')) }
    Invoke (Find-Name @('保存','Save'))
    Start-Sleep -Milliseconds 300
}
$restoreTheme=$null
try {
    foreach ($id in @('BackButton','UpButton','FileList')) {
        $control=Find-Id $id
        if (!$control -or [string]::IsNullOrWhiteSpace($control.Current.Name)) { throw "Missing accessible name: $id" }
        Write-Output "PASS accessible name for $id"
    }
    $list=Find-Id 'FileList'
    $readme=$list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'README.md'))
    $readme.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Check 'selected file has a preview' { (Find-Id 'PreviewName').Current.Name -eq 'README.md' }
    $transform=$window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $transform.Resize(760*$scale,820*$scale)
    Check 'preview remains visible in a narrow window' { $item=Find-Id 'PreviewName'; $item -and !$item.Current.IsOffscreen }
    Check 'narrow preview has an accessible toggle' { $item=Find-Id 'PreviewPaneCommand'; $item -and !$item.Current.IsOffscreen -and $item.Current.Name }
    Invoke (Find-Id 'PreviewPaneCommand')
    Check 'narrow preview can be hidden' { $item=Find-Id 'PreviewName'; !$item -or $item.Current.IsOffscreen }
    Invoke (Find-Id 'PreviewPaneCommand')
    Check 'narrow preview can be restored' { $item=Find-Id 'PreviewName'; $item -and !$item.Current.IsOffscreen }
    Capture 'narrow'
    $transform.Resize(1180*$scale,820*$scale)
    Check 'wide preview is restored' { $item=Find-Id 'PreviewName'; $item -and !$item.Current.IsOffscreen }
    Change-Theme @('浅色','Light')
    $restoreTheme=$previousTheme
    Capture 'light'
    Change-Theme @('深色','Dark')
    Capture 'dark'
    Write-Output "PASS light/dark and narrow/wide review captures: $output"
} finally {
    if (Find-Id 'ThemeSelector') { Invoke (Find-Name @('取消','Cancel')); Start-Sleep -Milliseconds 200 }
    if ($restoreTheme) { Change-Theme @($restoreTheme) }
    $window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize($original.Width,$original.Height)
}
