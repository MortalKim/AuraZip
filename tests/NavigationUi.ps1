param([int]$AppProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NavigationUiNative {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@
[void][NavigationUiNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$app = if ($AppProcessId) { Get-Process -Id $AppProcessId } else {
    Get-Process AuraZip | Where-Object MainWindowTitle -like '*AuraZip-sample.zip*' | Select-Object -Last 1
}
if (!$app -or !$app.MainWindowHandle) { throw 'Open artifacts/Samples/AuraZip-sample.zip before running this test.' }
[void][NavigationUiNative]::SetForegroundWindow($app.MainWindowHandle)
$window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
$scale = [NavigationUiNative]::GetDpiForWindow($app.MainWindowHandle) / 96.0
function Find-Id([string]$id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Find-File([string]$name) {
    $list = Find-Id 'FileList'
    if (!$list) { return $null }
    $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
}
function Wait-Check([string]$checkName, [scriptblock]$condition) {
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if (& $condition) { Write-Output "PASS $checkName"; return }
        Start-Sleep -Milliseconds 100
    }
    throw "UI check failed: $checkName"
}
function Click-File([string]$name, [switch]$Double, [switch]$Control, [switch]$Shift) {
    $item = Find-File $name
    if (!$item) { throw "File list item not found: $name" }
    $scroll = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) { $scroll.ScrollIntoView() }
    $rect = $item.Current.BoundingRectangle
    [void][NavigationUiNative]::SetPhysicalCursorPos([int]($rect.X + 50 * $scale), [int]($rect.Y + $rect.Height / 2))
    # Use real mouse input: SelectionItemPattern.Select() bypasses the affected gesture path.
    if ($Control) { [NavigationUiNative]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero) }
    if ($Shift) { [NavigationUiNative]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero) }
    try {
        for ($click = 0; $click -lt $(if ($Double) { 2 } else { 1 }); $click++) {
            [NavigationUiNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 35
            [NavigationUiNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 60
        }
    } finally {
        if ($Control) { [NavigationUiNative]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
        if ($Shift) { [NavigationUiNative]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero) }
    }
    Start-Sleep -Milliseconds 150
}
function Assert-Selected([string]$name) {
    Wait-Check "only $name is selected" {
        $list = Find-Id 'FileList'
        $selection = ([System.Windows.Automation.SelectionPattern]$list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
        $selection.Count -eq 1 -and $selection[0].Current.Name -eq $name
    }
}
function Invoke-Id([string]$id) {
    $element = Find-Id $id
    if (!$element) { throw "Control not found: $id" }
    ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
Wait-Check 'sample archive root is visible' { $null -ne (Find-File 'README.md') }
for ($cycle = 1; $cycle -le 3; $cycle++) {
    Click-File '文档' -Double
    Wait-Check "cycle ${cycle}: double-click opens the clicked folder" { $null -ne (Find-File '快捷键.txt') }
    Click-File '快捷键.txt'
    Assert-Selected '快捷键.txt'
    Invoke-Id 'BackButton'
    Wait-Check "cycle ${cycle}: Back restores the root" { $null -ne (Find-File 'README.md') }
    Click-File 'README.md'
    Assert-Selected 'README.md'
    Click-File 'LICENSE.txt'
    Assert-Selected 'LICENSE.txt'
    Click-File 'src'
    Assert-Selected 'src'
    Click-File 'README.md'
    Assert-Selected 'README.md'
    Click-File 'src' -Double
    Wait-Check "cycle ${cycle}: a selected file does not hijack a folder double-click" { $null -ne (Find-File 'hello.cs') }
    Invoke-Id 'UpButton'
    Wait-Check "cycle ${cycle}: Up restores the root" { $null -ne (Find-File 'README.md') }
}
Click-File 'README.md'
Click-File 'LICENSE.txt' -Control
Wait-Check 'Ctrl+click adds to the selection after repeated navigation' {
    $selection = ([System.Windows.Automation.SelectionPattern](Find-Id 'FileList').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
    $names = @($selection | ForEach-Object { $_.Current.Name })
    $names.Count -eq 2 -and $names -contains 'README.md' -and $names -contains 'LICENSE.txt'
}
Click-File 'README.md'
Click-File 'LICENSE.txt' -Shift
Wait-Check 'Shift+click selects a range after repeated navigation' {
    $selection = ([System.Windows.Automation.SelectionPattern](Find-Id 'FileList').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
    $names = @($selection | ForEach-Object { $_.Current.Name })
    $names.Count -eq 2 -and $names -contains 'README.md' -and $names -contains 'LICENSE.txt'
}
Click-File '文档'
Assert-Selected '文档'
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Wait-Check 'Enter opens the selected folder' { $null -ne (Find-File '快捷键.txt') }
Invoke-Id 'BackButton'
Wait-Check 'Back restores the root after keyboard navigation' { $null -ne (Find-File 'README.md') }
Click-File 'README.md'
Assert-Selected 'README.md'
Write-Output 'Archive navigation and mouse selection regression checks passed.'
