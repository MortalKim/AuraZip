param([int]$FileCount=10000)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. "$PSScriptRoot/UiActivation.ps1"
$repo=Split-Path -Parent $PSScriptRoot
$root=Join-Path $repo ('artifacts/LargeArchiveUi-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$archive=Join-Path $root 'many-files.zip'
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try {for($i=0;$i -lt $FileCount;$i++){$entry=$zip.CreateEntry(('file-{0:D5}.txt' -f $i));$s=$entry.Open();$s.WriteByte(65);$s.Dispose()}}finally{$zip.Dispose()}
function Find([string]$id){$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Check([string]$name,[scriptblock]$condition){for($attempt=0;$attempt -lt 200;$attempt++){if(&$condition){Write-Output "PASS $name";return};Start-Sleep -Milliseconds 100};throw "UI check failed: $name"}
$clock=[Diagnostics.Stopwatch]::StartNew()
$app=Start-AuraZipTestProcess ('"'+$archive+'"')
$window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
try {
    Check 'large archive opens in the contents view' {(Find 'WindowTitle').Current.Name -like 'many-files.zip*' -and (Find 'ArchiveSummary').Current.Name -match $FileCount.ToString()}
    $openMs=$clock.Elapsed.TotalMilliseconds
    $search=Find 'SearchBox'
    $edit=$search.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))
    $value=$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $clock.Restart();$value.SetValue(('file-{0:D5}.txt' -f ($FileCount-1)))
    Check 'last file can be found among all entries' {(Find 'StatusText').Current.Name -match '^0 .+1 '}
    $searchMs=$clock.Elapsed.TotalMilliseconds
    $clock.Restart();$value.SetValue('')
    Check 'clearing search restores the complete list' {(Find 'StatusText').Current.Name -match $FileCount.ToString()}
    $resetMs=$clock.Elapsed.TotalMilliseconds
    $app.Refresh()
    $result=[ordered]@{Files=$FileCount;OpenMilliseconds=$openMs;SearchMilliseconds=$searchMs;RestoreMilliseconds=$resetMs;WorkingSetBytes=$app.WorkingSet64}
    $result | ConvertTo-Json | Set-Content (Join-Path $root 'results.json')
    $result | ConvertTo-Json
    Write-Output ('Evidence='+$root)
} finally {[void]$app.CloseMainWindow();if(!$app.WaitForExit(5000)){throw 'The large archive test application did not close.'}}
