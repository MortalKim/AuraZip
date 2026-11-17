param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
. "$PSScriptRoot/UiActivation.ps1"
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('artifacts/OperationClose-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
function Find([string]$id){$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Named([string]$name){$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Check([string]$name,[scriptblock]$test){for($i=0;$i -lt 150;$i++){if(&$test){Write-Output ('PASS '+$name);return};Start-Sleep -Milliseconds 100};throw ('Operation close check failed: '+$name)}
function Invoke($element){if(!$element){throw 'Operation UI command missing.'};$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Launch([string]$arguments){$script:app=Start-AuraZipTestProcess $arguments;$script:window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)}
function PauseAndClose{
    Check 'operation can be paused' {$button=Find 'SecondaryButton';$button -and $button.Current.Name -in @('暂停','Pause')}
    Invoke (Find 'SecondaryButton')
    Check 'operation is paused' {(Find 'SecondaryButton').Current.Name -in @('继续','Resume')}
    [void]$app.CloseMainWindow()
    Check 'window close asks whether to continue the unfinished task' {$null -ne (Named '任务尚未完成') -or $null -ne (Named 'Operation in progress')}
    Invoke (Find 'PrimaryButton')
    Check 'continue preserves the paused operation' { !$app.HasExited -and (Find 'SecondaryButton').Current.Name -in @('继续','Resume') }
    [void]$app.CloseMainWindow()
    Check 'a second close offers cancellation' {(Find 'SecondaryButton').Current.Name -in @('取消任务并关闭','Cancel operation and close')}
    Invoke (Find 'SecondaryButton')
    if(!$app.WaitForExit(15000)){throw 'Cancelled operation did not clean up and close.'}
    Write-Output 'PASS cancellation cleans up and closes the window'
}
$ownedRoot=[IO.Path]::GetFullPath((Join-Path $root 'AuraZip/bin'))+[IO.Path]::DirectorySeparatorChar
foreach($old in (Get-Process AuraZip -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($ownedRoot,[StringComparison]::OrdinalIgnoreCase) -and $_.MainWindowHandle -ne 0})){
    [void]$old.CloseMainWindow();if(!$old.WaitForExit(5000)){throw 'Close the idle development window before running the test.'}
}
$source=Join-Path $fixture 'large.bin'
$bytes=New-Object byte[] (64*1024*1024);$random=[Security.Cryptography.RandomNumberGenerator]::Create()
try{$random.GetBytes($bytes);[IO.File]::WriteAllBytes($source,$bytes)}finally{$random.Dispose()}
$bytes=$null
Launch ('--shell compress -- "'+$source+'"')
Check 'compression dialog opens' {$null -ne (Find 'CompressionDestination')}
(Find 'CompressionFormat').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Check '7Z option appears' {$null -ne (Named '7Z')}
(Named '7Z').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 250
$output=Join-Path $fixture 'cancelled.7z'
(Find 'CompressionDestination').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($output)
Invoke (Find 'PrimaryButton')
PauseAndClose
Check 'cancelled compression never publishes a partial archive' {!(Test-Path -LiteralPath $output)}
$tiny=Join-Path $fixture 'tiny.txt';[IO.File]::WriteAllText($tiny,'finished operation close')
Launch ('--shell compress -- "'+$tiny+'"')
Check 'second compression dialog opens' {$null -ne (Find 'CompressionDestination')}
Invoke (Find 'PrimaryButton')
Check 'compression completes with its modal still open' {$null -ne (Find 'OperationCompleted')}
[void]$app.CloseMainWindow()
if(!$app.WaitForExit(15000)){throw 'Completed operation required dismissing its modal before closing.'}
Write-Output 'PASS completed compression closes directly without dismissing the result dialog'
Launch ('"'+(Join-Path $fixture 'tiny.zip')+'"')
Check 'archive is ready for extraction' {$button=Find 'ExtractCommand';$button -and $button.Current.IsEnabled}
Invoke (Find 'ExtractCommand')
Check 'extraction destination dialog opens' {$null -ne (Find 'ExtractionDestination')}
(Find 'ExtractionDestination').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $fixture 'extracted'))
Invoke (Find 'PrimaryButton')
Check 'extraction completes with its modal still open' {$null -ne (Find 'OperationCompleted')}
[void]$app.CloseMainWindow()
if(!$app.WaitForExit(15000)){throw 'Completed extraction did not close directly.'}
Write-Output 'PASS completed extraction closes directly'
foreach($kind in @('compression','extraction')){
    if($kind -eq 'compression'){
        Launch ('--shell compress -- "'+$tiny+'"')
        Check 'exit test create dialog opens' {$null -ne (Find 'CompressionDestination')}
        (Find 'CompressionDestination').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $fixture 'exit.zip'))
    }else{
        Launch ('"'+(Join-Path $fixture 'tiny.zip')+'"')
        Check 'exit test archive opens' {$button=Find 'ExtractCommand';$button -and $button.Current.IsEnabled}
        Invoke (Find 'ExtractCommand')
        Check 'exit test extraction dialog opens' {$null -ne (Find 'ExtractionDestination')}
        (Find 'ExtractionDestination').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $fixture 'exit-extracted'))
    }
    Invoke (Find 'PrimaryButton')
    Check ($kind+' completion exposes an Exit button') {$null -ne (Find 'OperationCompleted') -and (Find 'SecondaryButton').Current.Name -in @('退出','Exit')}
    Check ($kind+' completion retains the ordinary Close button') {(Find 'CloseButton').Current.Name -in @('关闭','Close')}
    Invoke (Find 'SecondaryButton')
    if(!$app.WaitForExit(15000)){throw ($kind+' completion Exit button did not close the application.')}
    Write-Output ('PASS '+$kind+' completion Exit closes the application')
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$many=Join-Path $fixture 'many.zip';$zip=[IO.Compression.ZipFile]::Open($many,[IO.Compression.ZipArchiveMode]::Create)
try{
    $data=New-Object byte[] 4096
    for($i=0;$i -lt 4000;$i++){$entry=$zip.CreateEntry(('file-'+$i+'.bin'),[IO.Compression.CompressionLevel]::NoCompression);$stream=$entry.Open();try{$stream.Write($data,0,$data.Length)}finally{$stream.Dispose()}}
}finally{$zip.Dispose()}
Launch ('"'+$many+'"')
Check 'many-file archive opens' {$button=Find 'ExtractCommand';$button -and $button.Current.IsEnabled}
Invoke (Find 'ExtractCommand')
Check 'many-file extraction dialog opens' {$null -ne (Find 'ExtractionDestination')}
(Find 'ExtractionDestination').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $fixture 'cancelled-extraction'))
Invoke (Find 'PrimaryButton')
PauseAndClose
Check 'cancelling extraction preserves the source archive' {Test-Path -LiteralPath $many}
Write-Output ('Operation close UI checks passed: '+$fixture)
