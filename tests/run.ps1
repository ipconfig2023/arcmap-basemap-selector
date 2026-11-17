$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskCsc=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$taskBin=Join-Path $taskRoot 'build'
& $taskCsc /nologo /platform:x86 /reference:System.Drawing.dll /reference:System.Xml.dll ('/reference:'+(Join-Path $taskBin 'SamplingBasemapGateway.exe')) ('/out:'+(Join-Path $taskBin 'RendererTests.exe')) (Join-Path $PSScriptRoot 'RendererTests.cs')
if($LASTEXITCODE -ne 0){throw '测试编译失败'}
& (Join-Path $taskBin 'RendererTests.exe')
if($LASTEXITCODE -ne 0){throw '渲染测试失败'}
$taskRefs=@('ESRI.ArcGIS.Version','ESRI.ArcGIS.System','ESRI.ArcGIS.Carto','ESRI.ArcGIS.Geometry','ESRI.ArcGIS.Display','ESRI.ArcGIS.Geodatabase','ESRI.ArcGIS.Output') | ForEach-Object {
    '/reference:'+(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR ('Microsoft.NET\assembly\GAC_MSIL\'+$_)) -Recurse -Filter ($_.ToString()+'.dll') | Select-Object -First 1).FullName
}
& $taskCsc /nologo /platform:x86 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @taskRefs ('/reference:'+(Join-Path $taskBin 'SamplingBasemaps.dll')) ('/out:'+(Join-Path $taskBin 'ArcSmoke.exe')) (Join-Path $PSScriptRoot 'ArcSmoke.cs')
if($LASTEXITCODE -ne 0){throw 'ArcObjects 测试编译失败'}
Write-Output '本地测试通过；ArcSmoke.exe 已生成。'
