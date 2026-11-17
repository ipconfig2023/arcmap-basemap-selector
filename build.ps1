param([switch]$Install)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskBin = Join-Path $taskRoot 'build'
$taskPackage = Join-Path $taskRoot 'package'
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$taskArc = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WOW6432Node\ESRI\Desktop10.8').InstallDir
$taskRefs = @('ESRI.ArcGIS.System','ESRI.ArcGIS.Carto','ESRI.ArcGIS.GISClient','ESRI.ArcGIS.ArcMapUI','ESRI.ArcGIS.Framework','ESRI.ArcGIS.Geometry','ESRI.ArcGIS.Display','ESRI.ArcGIS.Geodatabase') | ForEach-Object {
    (Get-ChildItem -LiteralPath (Join-Path $env:WINDIR ('Microsoft.NET\assembly\GAC_MSIL\' + $_)) -Recurse -Filter ($_.ToString() + '.dll') | Select-Object -First 1).FullName
}
$taskRefs += Join-Path $taskArc 'bin\ESRI.ArcGIS.Desktop.AddIns.dll'
$taskRefArgs = $taskRefs | ForEach-Object { '/reference:' + $_ }
$taskStandard = @('/nologo','/optimize+','/platform:x86','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Web.dll','/reference:System.Security.dll')
New-Item -ItemType Directory -Path $taskBin,(Join-Path $taskPackage 'Images'),(Join-Path $taskPackage 'Install') -Force | Out-Null
& $taskCsc @taskStandard @taskRefArgs /target:library ('/out:' + (Join-Path $taskBin 'SamplingBasemaps.dll')) (Join-Path $taskRoot 'src\Common.cs') (Join-Path $taskRoot 'src\AddIn.cs')
if ($LASTEXITCODE -ne 0) { throw '插件编译失败' }
& $taskCsc @taskStandard /target:winexe ('/out:' + (Join-Path $taskBin 'SamplingBasemapGateway.exe')) (Join-Path $taskRoot 'src\Common.cs') (Join-Path $taskRoot 'src\Gateway.cs')
if ($LASTEXITCODE -ne 0) { throw '地图服务编译失败' }
& $taskCsc @taskStandard @taskRefArgs ('/reference:' + (Join-Path $taskBin 'SamplingBasemaps.dll')) /target:winexe ('/out:' + (Join-Path $taskBin 'Preview.exe')) (Join-Path $taskRoot 'src\Preview.cs')
if ($LASTEXITCODE -ne 0) { throw '预览程序编译失败' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'Config.xml') -Destination (Join-Path $taskPackage 'Config.xml') -Force
Copy-Item -LiteralPath (Join-Path $taskBin 'SamplingBasemaps.dll'),(Join-Path $taskBin 'SamplingBasemapGateway.exe') -Destination (Join-Path $taskPackage 'Install') -Force
Add-Type -AssemblyName System.Drawing
$taskImage = New-Object Drawing.Bitmap 32,32
$taskG = [Drawing.Graphics]::FromImage($taskImage)
$taskG.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskG.Clear([Drawing.Color]::Transparent)
$taskBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(35,120,155))
$taskPen = New-Object Drawing.Pen ([Drawing.Color]::White),1.7
$taskG.FillEllipse($taskBrush,1,1,29,29)
$taskG.DrawEllipse($taskPen,7,2,17,27)
$taskG.DrawLine($taskPen,2,16,30,16)
$taskG.DrawArc($taskPen,2,7,28,9,0,180)
$taskImage.Save((Join-Path $taskPackage 'Images\Basemap.png'),[Drawing.Imaging.ImageFormat]::Png)
$taskPen.Dispose(); $taskBrush.Dispose(); $taskG.Dispose(); $taskImage.Dispose()
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskDestination = Join-Path $taskRoot 'dist\SamplingBasemaps.esriAddIn'
if (Test-Path -LiteralPath $taskDestination) { Remove-Item -LiteralPath $taskDestination }
[IO.Compression.ZipFile]::CreateFromDirectory($taskPackage,$taskDestination,[IO.Compression.CompressionLevel]::Optimal,$false)
Write-Output ('已生成：' + $taskDestination)
if ($Install) { & (Join-Path $taskRoot 'install.ps1') }
