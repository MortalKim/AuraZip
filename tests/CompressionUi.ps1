param([ValidateSet('Debug','Release')][string]$Configuration='Release', [switch]$UseRegisteredPackage, [switch]$IncludeTar)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$projectRoot=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $projectRoot "AuraZip/bin/x64/$Configuration/net8.0-windows10.0.19041.0/win-x64/AuraZip.exe"
$fixture=Join-Path $projectRoot ('artifacts/CompressionUi-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$source=Join-Path $fixture 'source.txt';[IO.File]::WriteAllText($source,'native compression contents')
function Find([string]$value,[switch]$Id,$Type){
    $property=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty}
    $condition=[System.Windows.Automation.PropertyCondition]::new($property,$value)
    if($Type){$condition=[System.Windows.Automation.AndCondition]::new($condition,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))}
    $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke($item){if(!$item){throw 'UI command not found.'};([System.Windows.Automation.InvokePattern]$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}
function Check([string]$label,[scriptblock]$test){for($i=0;$i -lt 100;$i++){if(&$test){Write-Output "PASS $label";return};Start-Sleep -Milliseconds 100};throw "UI check failed: $label"}
. "$PSScriptRoot/UiActivation.ps1"
$package=Get-AppxPackage -Name '600875F506911.AuraZipPreview'
if($UseRegisteredPackage){$exe=Join-Path $package.InstallLocation 'AuraZip.exe'}
if(!$package -or $package.InstallLocation.TrimEnd('\') -ne (Split-Path -Parent $exe).TrimEnd('\')){throw 'Register the requested build with scripts/Run.ps1 before testing.'}
function Launch([string]$arguments){
    $script:process=Start-AuraZipTestProcess $arguments
    $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)
}
function CloseInstance{[void]$script:process.CloseMainWindow();if(!$script:process.WaitForExit(5000)){throw 'The UI test application did not close.'}}
$formats=@('ZIP','7Z');if($IncludeTar){$formats+=@('TAR','TAR.GZ','TAR.BZ2','TAR.XZ')}
foreach($format in $formats){
    Launch ('--shell compress -- "'+$source+'"')
    Check 'shell compression opens the native create dialog' { $null -ne (Find 'CompressionDestination' -Id) }
    if($format -ne 'ZIP'){
        $combo=Find 'CompressionFormat' -Id
        ([System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        Check ($format+' format is available') { $null -ne (Find $format -Type ([System.Windows.Automation.ControlType]::ListItem)) }
        $option=Find $format -Type ([System.Windows.Automation.ControlType]::ListItem)
        ([System.Windows.Automation.SelectionItemPattern]$option.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        Start-Sleep -Milliseconds 400
    }
    $output=Join-Path $fixture ('result.'+$format.ToLowerInvariant())
    Check 'create dialog remains available after selecting format' { $null -ne (Find 'CompressionDestination' -Id) }
    $field=Find 'CompressionDestination' -Id
    ([System.Windows.Automation.ValuePattern]$field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($output)
    Invoke (Find '压缩' -Type ([System.Windows.Automation.ControlType]::Button))
    Check "$format compression finishes" { $null -ne (Find '压缩完成') }
    Check "$format completion has a visible success banner" { $banner=Find 'OperationCompleted' -Id; $banner -and !$banner.Current.IsOffscreen }
    Check "$format completion shows final file totals" { (Find 'OperationFileCount' -Id).Current.Name -like '1 / 1 *' }
    Check "$format real output exists" { Test-Path -LiteralPath $output }
    Check 'completed compression reports original size, packed size and ratio' { @($script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like '*压缩率：*' }).Count -gt 0}
    Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
    Check "$format output opens as archive contents" { $null -ne (Find 'source.txt' -Type ([System.Windows.Automation.ControlType]::ListItem)) }
    CloseInstance
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
foreach($name in @('first','second')){
    $path=Join-Path $fixture ($name+'.zip');$zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    $entry=$zip.CreateEntry($name+'.txt');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write($name);$writer.Dispose();$zip.Dispose()
}
$bad=Join-Path $fixture 'bad.zip';[IO.File]::WriteAllText($bad,'invalid archive')
Launch ('--shell smart -- "'+$bad+'" "'+(Join-Path $fixture 'first.zip')+'" "'+(Join-Path $fixture 'second.zip')+'"')
Check 'batch extraction reaches one final result dialog' { $null -ne (Find '批量处理结果') }
Check 'batch continues after an invalid archive' { (Test-Path -LiteralPath (Join-Path $fixture 'first.txt')) -and (Test-Path -LiteralPath (Join-Path $fixture 'second.txt')) }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
CloseInstance
Launch ('--shell test -- "'+(Join-Path $fixture 'first.zip')+'" "'+(Join-Path $fixture 'second.zip')+'"')
Check 'batch integrity tests finish without individual completion prompts' { $null -ne (Find '批量处理结果') }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
CloseInstance
Write-Output "Native compression and batch checks passed: $fixture"
