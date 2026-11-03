param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$folder = Join-Path $root 'artifacts/Samples'
[void](New-Item -ItemType Directory -Path $folder -Force)
$path = Join-Path $folder 'AuraZip-sample.zip'
$temporary = Join-Path $folder ('sample-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $texts = [ordered]@{
            'README.md' = "# AuraZip`n原生版本验证压缩包。用于测试导航、预览和解压。"
            '文档/快捷键.txt' = "Ctrl+O 打开`nCtrl+N 创建`nCtrl+C 复制"
            '文档/说明.txt' = '测试样本不包含用户数据。'
            '示例/配置.json' = '{"name":"AuraZip","sample":true}'
            '示例/数据.csv' = "name,value`nAuraZip,1"
            'src/hello.cs' = 'Console.WriteLine("AuraZip");'
            'LICENSE.txt' = 'Generated test data for AuraZip UI verification.'
            '.editorconfig' = "root = true`n[*]`ncharset = utf-8"
        }
        foreach ($item in $texts.GetEnumerator()) {
            $entry = $zip.CreateEntry($item.Key)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($item.Value) } finally { $writer.Dispose() }
        }
        [void]$zip.CreateEntry('空目录/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'AuraZip/Assets/AppIcon.png'), '资源/app-icon.png')
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $path -Force
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Write-Output $path
