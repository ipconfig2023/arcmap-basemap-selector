# Sampling 地图源插件（ArcMap 10.8）

在 ArcMap 内选择地图来源，然后点击 **添加 / 切换**。插件把底图放在图层列表底部，保留采样点、专题图层、数据框坐标系及当前视图范围。

## 安装与使用

从本仓库的 **Releases** 下载 `SamplingBasemaps.esriAddIn`，双击后按 ArcGIS 提示安装。也可使用仓库 `dist` 目录内的同名安装包。

1. 保存工作，重新打开 ArcMap。
2. 在 **自定义（Customize）→ 工具条（Toolbars）** 勾选 **Sampling 地图源**。新安装时工具条通常会自动出现。
3. 点击地球图标 **地图源选择**。
4. 选来源，点击 **添加 / 切换**。可先点击 **测试连接**。

需要 Windows、ArcGIS Desktop 10.8 / 10.8.1 和 .NET Framework 4.5 及以上版本。已在 ArcGIS Desktop 10.8.1 验证。该安装包用于 ArcMap，不适用于 ArcGIS Pro。

## 地图来源

| 来源 | 说明 |
|---|---|
| Esri 卫星影像 | 可选地名注记；服务持续更新，并非固定年份 |
| Esri 街道地图 | 道路、居民地、地名 |
| Esri 地形图 | 地形与地理背景 |
| OpenStreetMap | 标准街道地图；仅交互浏览，按需请求与缓存 |
| CARTO 浅色地图 | 浅色专题图底图 |
| CARTO 深色地图 | 深色展示底图 |
| 天地图影像 + 中文注记 | 需填写有效天地图服务 Key；最多 18 级 |
| NASA 夜间灯光 | VIIRS Black Marble **2016 年合成影像**，最多 8 级，并非实时夜间灯光 |
| 自定义地图 / 历史影像 | 一个可编辑的 Web Mercator XYZ 来源槽位 |

天地图 Key 在当前 Windows 用户下加密保存，不写进地图服务地址或 MXD。没有真实 Key 时该来源不能添加，不会替换成其他地图。

自定义来源需填写完整 HTTP / HTTPS 地址，并含 `{z}`、`{x}`、`{y}`。支持 `{s}`（取 `a`）和 `{r}`（取空字符）。设置名称、来源署名和最大级别后保存。使用 EPSG:3857、标准 XYZ 编号；TMS 反向行号和 GCJ-02 / BD-09 坐标不适用。自定义来源是一个共享设置槽位，修改它也会改变此前保存地图里该槽位的显示来源。

如需历史影像，可在自定义来源中填写有授权且明确标注年份的真实历史影像地址。普通 Esri 卫星影像不代表指定年份。

## 切换、保存与出图

- 勾选 **替换此前由此插件添加的底图**：添加新底图后，替换此前由插件添加并带专有标记的顶层底图；其他图层保留。取消勾选可保留多个来源。
- 注记开关只对支持独立注记的影像来源生效。街道图自身已有文字，无法通过此开关移除。
- 可以正常保存 MXD、保存图层文件，以及通过 ArcMap 打印或导出。下次启动 ArcMap 时插件会启动本机地图服务，恢复连接。
- 在其他电脑打开 MXD / 图层文件时，也需安装此插件，并配置相同的来源、端口和所需 Key；服务地址指向本机，并非可公开分享的服务器。
- 出图时保留相应来源署名。来源署名显示在选择窗口和 WMS 图层服务说明内，版面中请添加对应的数据来源文字。
- 底图需联网。下载失败会报错，不会偷偷换用其他来源。首次显示或大范围导出可能需要等待。
- 单次 WMS 图像最大 4096 × 4096 像素、最多 512 张基础瓦片；超过限制时缩小输出尺寸、调整 DPI 或分幅导出。ArcMap 的动态投影需要额外处理时间。

## 本地文件与维护

- 安装包：`dist\SamplingBasemaps.esriAddIn`
- 源码：`src\`；编译：`build.ps1`；安装：`install.ps1`
- 用户设置：`%LOCALAPPDATA%\SamplingBasemaps\settings.json`
- 运行日志：`%LOCALAPPDATA%\SamplingBasemaps\gateway.log`
- 按需瓦片缓存：`%LOCALAPPDATA%\SamplingBasemaps\cache`。尊重服务的有效期；服务未提供缓存期限时默认 7 天。启动时超出 512 MB 的旧缓存会整理至约 384 MB。

本机地图服务仅监听 `127.0.0.1`，默认端口 `17868`；后台进程名为 `SamplingBasemapGateway.exe`，由插件启动，可持续运行至 Windows 会话结束，不创建开机启动项。若修改设置文件端口，需同步修复既有地图的 WMS 连接。卸载可在 ArcMap Add-In 管理器移除本插件并重新启动 ArcMap。

## 从源码构建与验证

在已安装 ArcGIS Desktop 10.8 / 10.8.1 及其 .NET ArcObjects 程序集的 Windows 电脑上，使用 Windows PowerShell 运行：

```powershell
.\build.ps1
.\tests\run.ps1
# 可选：安装至当前 Windows 用户
.\install.ps1
```

构建脚本使用系统 .NET Framework 编译器和本机 ArcGIS 程序集，输出 `dist\SamplingBasemaps.esriAddIn`。仓库与安装包不包含 Esri 专有程序集；需要自行安装并授权 ArcGIS Desktop。

`tests\run.ps1` 检查瓦片拼接、透明注记、日期线环绕、范围验证、请求失败、Key 加密、设置保存和服务描述。`ArcSmoke.exe` 在本机 ArcGIS 中验证 WMS 连接、图层保存重开、底图替换、业务图层保留、WGS84 动态投影与实际非空图片导出。验证产物仅用于测试，位于 `verification\`。

实现依据：Esri ArcObjects 的 WMS 图层接口、NASA GIBS WMTS 服务能力文档及 OpenStreetMap 瓦片使用政策。NASA 影像地址已修正为官方能力文档声明的 PNG 地址。

## 地图服务使用

各地图服务及数据的版权归相应提供方所有，使用时遵守其授权、署名、访问频率和用途要求。天地图 Key 由使用者自行申请；仓库不包含服务密钥。公开源码不授予第三方地图数据的使用权。
