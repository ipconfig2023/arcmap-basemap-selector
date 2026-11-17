$ErrorActionPreference = 'Stop'
$taskPackage = Join-Path $PSScriptRoot 'dist\SamplingBasemaps.esriAddIn'
if (-not (Test-Path -LiteralPath $taskPackage)) { throw '请先运行 build.ps1 生成安装包。' }
$taskFolder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ArcGIS\AddIns\Desktop10.8\{53D85543-9B9B-43D4-82D4-0A33B6055D01}'
New-Item -ItemType Directory -Path $taskFolder -Force | Out-Null
Copy-Item -LiteralPath $taskPackage -Destination (Join-Path $taskFolder 'SamplingBasemaps.esriAddIn') -Force
# Refresh only this user's known plugin gateway, without closing or editing ArcMap documents.
$taskRuntime = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SamplingBasemaps\runtime'
$taskKnownExecutables = @(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ESRI\Desktop10.8\AssemblyCache\{53D85543-9B9B-43D4-82D4-0A33B6055D01}\SamplingBasemapGateway.exe'),
    (Join-Path $taskRuntime 'SamplingBasemapGateway.exe'),
    (Join-Path $PSScriptRoot 'build\SamplingBasemapGateway.exe')
)
$taskSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
$taskRunning = @(Get-Process -Name SamplingBasemapGateway -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $taskSession -and $_.Path -in $taskKnownExecutables })
if ($taskRunning.Count -gt 0) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $taskRuntime -Force | Out-Null
    $taskArchive = [IO.Compression.ZipFile]::OpenRead($taskPackage)
    try {
        $taskEntry = $taskArchive.GetEntry('Install/SamplingBasemapGateway.exe')
        if ($null -eq $taskEntry) { throw '安装包缺少地图服务组件。' }
        foreach ($taskProcess in $taskRunning) { Stop-Process -Id $taskProcess.Id; $taskProcess.WaitForExit() }
        $taskRuntimeExe = Join-Path $taskRuntime 'SamplingBasemapGateway.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry,$taskRuntimeExe,$true)
    } finally { $taskArchive.Dispose() }
    $taskSettings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SamplingBasemaps\settings.json'
    Start-Process -FilePath $taskRuntimeExe -ArgumentList ('--settings "' + $taskSettings + '"') -WindowStyle Hidden
    Write-Output '已更新正在运行的本机地图服务，原有磁盘缓存保留。'
}
Write-Output ('已安装到：' + $taskFolder)
Write-Output '请保存当前工作并重新打开 ArcMap；在“自定义 → 工具条”中勾选“Sampling 地图源”。'
