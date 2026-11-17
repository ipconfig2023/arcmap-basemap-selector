$ErrorActionPreference = 'Stop'
$taskPackage = Join-Path $PSScriptRoot 'dist\SamplingBasemaps.esriAddIn'
if (-not (Test-Path -LiteralPath $taskPackage)) { throw '请先运行 build.ps1 生成安装包。' }
$taskFolder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ArcGIS\AddIns\Desktop10.8\{53D85543-9B9B-43D4-82D4-0A33B6055D01}'
New-Item -ItemType Directory -Path $taskFolder -Force | Out-Null
Copy-Item -LiteralPath $taskPackage -Destination (Join-Path $taskFolder 'SamplingBasemaps.esriAddIn') -Force
Write-Output ('已安装到：' + $taskFolder)
Write-Output '请保存当前工作并重新打开 ArcMap；在“自定义 → 工具条”中勾选“Sampling 地图源”。'
