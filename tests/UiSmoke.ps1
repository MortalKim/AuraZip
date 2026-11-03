param([Parameter(Mandatory=$true)][string]$SampleArchive)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AuraZipUiCheck {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
}
'@
$process = Get-Process AuraZip | Where-Object MainWindowTitle -like '*AuraZip-sample.zip*' | Select-Object -Last 1
if (!$process) { throw 'Launch AuraZip with artifacts/Samples/AuraZip-sample.zip before running this test.' }
[void][AuraZipUiCheck]::ShowWindow($process.MainWindowHandle,9)
[void][AuraZipUiCheck]::SetForegroundWindow($process.MainWindowHandle)
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
function Find-Id([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Find-Name([string]$name, $kind) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)
    if ($kind) {
        $type = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$kind)
        $condition = New-Object System.Windows.Automation.AndCondition($condition,$type)
    }
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Wait-Check([string]$name,[scriptblock]$condition) {
    for ($attempt=0;$attempt -lt 60;$attempt++) {
        if (& $condition) { Write-Output "PASS $name"; return }
        Start-Sleep -Milliseconds 100
    }
    throw "UI check failed: $name"
}
function Invoke-Element($element) {
    if (!$element) { throw 'The UI element was not found.' }
    ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Select-File([string]$name) {
    $element=Find-Name $name ([System.Windows.Automation.ControlType]::ListItem)
    if (!$element) { throw "File list item not found: $name" }
    ([System.Windows.Automation.SelectionItemPattern]$element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $element.SetFocus()
    Start-Sleep -Milliseconds 150
}
Select-File 'README.md'
Wait-Check 'single click previews real text' { (Find-Id 'PreviewText').Current.Name -like '*原生版本验证压缩包*' }
Select-File '文档'
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Wait-Check 'Enter navigates into a folder' { $null -ne (Find-Name '快捷键.txt' ([System.Windows.Automation.ControlType]::ListItem)) }
Invoke-Element (Find-Id 'UpButton')
Wait-Check 'up navigation restores the root' { $null -ne (Find-Name 'README.md' ([System.Windows.Automation.ControlType]::ListItem)) }
Select-File '资源'
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Wait-Check 'resource folder opens' { $null -ne (Find-Name 'app-icon.png' ([System.Windows.Automation.ControlType]::ListItem)) }
Select-File 'app-icon.png'
Wait-Check 'image preview renders' { $image=Find-Id 'PreviewImage'; $image -and !$image.Current.IsOffscreen }
Invoke-Element (Find-Id 'UpButton')
Wait-Check 'root is available after image preview' { $null -ne (Find-Name 'README.md' ([System.Windows.Automation.ControlType]::ListItem)) }
Select-File 'README.md'
Invoke-Element (Find-Id 'ExtractCommand')
Wait-Check 'native extraction dialog opens' { $null -ne (Find-Id 'ExtractionDestination') }
$destination = Find-Id 'ExtractionDestination'
$value = [System.Windows.Automation.ValuePattern]$destination.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$sampleDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $SampleArchive).Path
Wait-Check 'default destination is the archive directory' { $value.Current.Value -eq $sampleDirectory }
$all = Find-Name '所有文件' ([System.Windows.Automation.ControlType]::RadioButton)
([System.Windows.Automation.SelectionItemPattern]$all.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Wait-Check 'smart extraction adds the archive folder' { (Find-Id 'ResolvedDestination').Current.Name -eq (Join-Path $sampleDirectory 'AuraZip-sample') }
$outputBase = Join-Path (Split-Path -Parent $sampleDirectory) ('UiExtraction-' + [Guid]::NewGuid().ToString('N'))
$value.SetValue($outputBase)
Invoke-Element (Find-Name '解压' ([System.Windows.Automation.ControlType]::Button))
Wait-Check 'real extraction finishes in the native progress dialog' { $null -ne (Find-Name '解压完成' $null) }
$output = Join-Path $outputBase 'AuraZip-sample'
if (!(Test-Path -LiteralPath (Join-Path $output 'README.md'))) { throw 'The UI operation did not produce the expected output file.' }
if ((Get-ChildItem -LiteralPath $output -File -Recurse).Count -ne 9) { throw 'The extracted file count is incorrect.' }
Write-Output "PASS UI extraction produced 9 files: $output"
Invoke-Element (Find-Name '打开目标文件夹' ([System.Windows.Automation.ControlType]::Button))
$shell = New-Object -ComObject Shell.Application
$opened = $null
Wait-Check 'folder button opens the real destination in Explorer' {
    foreach ($candidate in $shell.Windows()) {
        try {
            if ($candidate.LocationURL -and ([Uri]$candidate.LocationURL).LocalPath.TrimEnd('\') -eq $output.TrimEnd('\')) { $script:opened=$candidate; return $true }
        } catch { }
    }
    return $false
}
if ($opened) { $opened.Quit() }
Invoke-Element (Find-Name '关闭' ([System.Windows.Automation.ControlType]::Button))
Wait-Check 'closing progress returns to archive contents' { $null -ne (Find-Name 'README.md' ([System.Windows.Automation.ControlType]::ListItem)) }
Select-File 'README.md'
Write-Output 'Native UI smoke checks passed.'
