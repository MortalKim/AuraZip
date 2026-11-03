param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ProductivityUiNative { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }'
. "$PSScriptRoot/UiActivation.ps1"
$fixture=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/ProductivityUi-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture)
$unique='AuraZip-edit-'+[Guid]::NewGuid().ToString('N')+'.txt'
$archivePath=Join-Path $fixture 'productivity.zip'
function ZipText($archive,$name,$text){$entry=$archive.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false));$writer.Write($text);$writer.Dispose()}
$doc=[IO.MemoryStream]::new();$z=[IO.Compression.ZipArchive]::new($doc,[IO.Compression.ZipArchiveMode]::Create,$true)
ZipText $z 'word/document.xml' '<document><p><t>AuraZip Office preview verified</t></p></document>';$z.Dispose()
$pdf='%PDF-1.4' + "`n";$offsets=@(0)
$objects=@('<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>')
foreach($n in 1..2){$content="BT /F1 20 Tf 40 300 Td (Page $n) Tj ET";$objects+=('<< /Length '+$content.Length+" >>`nstream`n"+$content+"`nendstream")}
$objects+='<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
for($i=0;$i -lt $objects.Count;$i++){$offsets+=$pdf.Length;$pdf+=($i+1).ToString()+" 0 obj`n"+$objects[$i]+"`nendobj`n"}
$xref=$pdf.Length;$pdf+="xref`n0 8`n0000000000 65535 f `n";foreach($offset in $offsets[1..7]){$pdf+=$offset.ToString('0000000000')+" 00000 n `n"};$pdf+="trailer`n<< /Size 8 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n"
$wav=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($wav)
$writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write([int]16036);$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write([int]16);$writer.Write([int16]1);$writer.Write([int16]1);$writer.Write([int]8000);$writer.Write([int]16000);$writer.Write([int16]2);$writer.Write([int16]16);$writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write([int]16000);$writer.Write((New-Object byte[] 16000));$writer.Flush()
$z=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
ZipText $z $unique 'original external edit'; ZipText $z 'hash.txt' 'abc'
foreach($pair in @(@('document.docx',$doc.ToArray()),@('pages.pdf',[Text.Encoding]::ASCII.GetBytes($pdf)),@('audio.wav',$wav.ToArray()))){$entry=$z.CreateEntry($pair[0]);$s=$entry.Open();$s.Write($pair[1],0,$pair[1].Length);$s.Dispose()};$z.Dispose();$doc.Dispose();$writer.Dispose();$wav.Dispose()
$app=Start-AuraZipTestProcess ('"'+$archivePath+'"')
$window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
[void][ProductivityUiNative]::SetForegroundWindow($app.MainWindowHandle)
function Find([string]$value,[switch]$Id,$Type){$prop=if($Id){[System.Windows.Automation.AutomationElement]::AutomationIdProperty}else{[System.Windows.Automation.AutomationElement]::NameProperty};$c=[System.Windows.Automation.PropertyCondition]::new($prop,$value);if($Type){$c=[System.Windows.Automation.AndCondition]::new($c,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))};$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)}
function Invoke($item){if(!$item){throw 'UI command missing'};([System.Windows.Automation.InvokePattern]$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();Start-Sleep -Milliseconds 200}
function Check($label,[scriptblock]$test){for($i=0;$i -lt 150;$i++){if(&$test){Write-Output ('PASS '+$label);return};Start-Sleep -Milliseconds 100};throw ('UI check failed: '+$label)}
function SelectFile($name){$item=Find $name -Type ([System.Windows.Automation.ControlType]::ListItem);if(!$item){throw ('Missing file '+$name)};([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select();$item.SetFocus();Start-Sleep -Milliseconds 150}
function SetValue($item,$value){([System.Windows.Automation.ValuePattern]$item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)}
function Tool($name){[void][ProductivityUiNative]::SetForegroundWindow($app.MainWindowHandle); Invoke (Find '工具' -Type ([System.Windows.Automation.ControlType]::MenuItem)); for($attempt=0;$attempt -lt 40;$attempt++){if(Find $name -Type ([System.Windows.Automation.ControlType]::MenuItem)){break};Start-Sleep -Milliseconds 100};Invoke (Find $name -Type ([System.Windows.Automation.ControlType]::MenuItem))}
SelectFile 'document.docx'
Check 'Office document text appears in the preview pane' { (Find 'PreviewText' -Id).Current.Name -like '*AuraZip Office preview verified*' }
SelectFile 'pages.pdf'
Check 'PDF renders its first page' { (Find 'PdfPageNumber' -Id).Current.Name -eq '1 / 2' -and !(Find 'PreviewImage' -Id).Current.IsOffscreen }
Invoke (Find 'PdfNext' -Id)
Check 'PDF next page renders' { (Find 'PdfPageNumber' -Id).Current.Name -eq '2 / 2' }
SelectFile 'audio.wav'
Check 'audio has native playback controls without automatic playback' { $play=Find 'PlayPauseButton' -Id; $play -and !$play.Current.IsOffscreen -and $play.Current.Name -eq '播放' }
SelectFile 'hash.txt'
Tool '文件哈希'
Check 'hash dialog reports real SHA-256 and MD5' { $null -ne (Find '比对哈希（可选）') }
$field=Find '比对哈希（可选）';SetValue $field '900150983cd24fb0d6963f7d28e17f72'
Check 'hash comparison matches case-insensitively' { $null -ne (Find '匹配：hash.txt') }
SetValue $field '123'
Check 'hash comparison reports a mismatch' { $null -ne (Find '哈希不匹配') }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
Tool '压缩包注释'
Check 'ZIP comment editor opens' { $null -ne (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button)) }
$edit=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'')))
SetValue $edit 'AuraZip 中文注释';Invoke (Find '保存' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'ZIP comment is saved' { (Find 'StatusText' -Id).Current.Name -eq '注释已保存' }
SelectFile $unique
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
$script:editedPath=$null
Check 'default application receives a real extracted file' { $script:editedPath=Get-ChildItem (Join-Path $env:TEMP 'AuraZip') -Filter $unique -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName; $null -ne $script:editedPath }
Start-Sleep -Milliseconds 800
[IO.File]::WriteAllText($script:editedPath,'changed by external editor',[Text.UTF8Encoding]::new($false))
[void][ProductivityUiNative]::SetForegroundWindow($app.MainWindowHandle)
Check 'external change prompts before writing to the archive' { [void][ProductivityUiNative]::SetForegroundWindow($app.MainWindowHandle); $null -ne (Find '写回压缩包' -Type ([System.Windows.Automation.ControlType]::Button)) }
Invoke (Find '暂不写回' -Type ([System.Windows.Automation.ControlType]::Button))
Tool '检查外部修改'
Check 'deferred edits can be saved later from the tools menu' { $null -ne (Find '写回压缩包' -Type ([System.Windows.Automation.ControlType]::Button)) }
Invoke (Find '写回压缩包' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'confirmed external change completes an archive update' { $null -ne (Find '更新完成') }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'external edit is saved into the original ZIP' { $z=[IO.Compression.ZipFile]::OpenRead($archivePath);try{$reader=[IO.StreamReader]::new($z.GetEntry($unique).Open());try{$reader.ReadToEnd() -eq 'changed by external editor'}finally{$reader.Dispose()}}finally{$z.Dispose()} }
[IO.File]::WriteAllText($script:editedPath,'second external edit',[Text.UTF8Encoding]::new($false))
Check 'a later external save is detected' { $null -ne (Find '写回压缩包' -Type ([System.Windows.Automation.ControlType]::Button)) }
$z=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Update);ZipText $z 'outside.txt' 'independent change';$z.Dispose()
Invoke (Find '写回压缩包' -Type ([System.Windows.Automation.ControlType]::Button))
$conflict="原压缩包已发生变化，为避免覆盖其他修改，本次未写回。修改后的文件保留在：`n"+$script:editedPath
Check 'concurrent archive changes prevent overwriting the original' { $null -ne (Find $conflict) }
Invoke (Find '关闭' -Type ([System.Windows.Automation.ControlType]::Button))
Check 'conflicting external file remains available' { [IO.File]::ReadAllText($script:editedPath) -eq 'second external edit' }
# The editor's own document remains open; do not close unrelated tabs or applications.
[void]$app.CloseMainWindow();if(!$app.WaitForExit(5000)){throw 'Test app did not close'}
Write-Output ('Productivity UI checks passed: '+$fixture)
