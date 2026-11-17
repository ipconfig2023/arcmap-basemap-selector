param([int]$Port=17869)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskRoot 'verification'
$taskResults=@()
foreach($taskProvider in @('esri_imagery','esri_streets','esri_topo','osm','carto_light','carto_dark','nasa_night','custom')) {
    $taskUrl='http://127.0.0.1:'+$Port+'/wms/'+$taskProvider+'/1?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=basemap&STYLES=&SRS=EPSG:3857&BBOX=-20037508.342789244,-20037508.342789244,20037508.342789244,20037508.342789244&WIDTH=256&HEIGHT=256&FORMAT=image/png'
    $taskResponse=Invoke-WebRequest -Uri $taskUrl -TimeoutSec 60
    $taskContentType=$taskResponse.Headers['Content-Type'] -join ''
    if(-not $taskContentType.StartsWith('image/png')) { throw ($taskProvider+': '+[Text.Encoding]::UTF8.GetString($taskResponse.Content)) }
    [IO.File]::WriteAllBytes((Join-Path $taskOutput ($taskProvider+'.png')),$taskResponse.Content)
    $taskResults += [pscustomobject]@{source=$taskProvider;status='PASS';bytes=$taskResponse.RawContentLength}
}
$taskInvalidUrl='http://127.0.0.1:'+$Port+'/wms/tianditu/1?REQUEST=GetCapabilities'
$taskResponse=Invoke-WebRequest -Uri $taskInvalidUrl -TimeoutSec 10
$taskText=if($taskResponse.Content -is [byte[]]){[Text.Encoding]::UTF8.GetString($taskResponse.Content)}else{$taskResponse.Content}
if(-not $taskText.Contains('ServiceException')) { throw '未配置的天地图应明确报错' }
$taskResults += [pscustomobject]@{source='tianditu_without_key';status='PASS (explicit error)';bytes=0}
$taskResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'network-results.json') -Encoding utf8
$taskResults | Format-Table -AutoSize
