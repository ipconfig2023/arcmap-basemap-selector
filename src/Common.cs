using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SamplingBasemaps
{
    public sealed class Settings
    {
        public int Port = 17868;
        public string SelectedSource = "esri_imagery";
        public bool Labels = true;
        public bool Replace = true;
        public string ProtectedTianDiTuKey = "";
        public string CustomName = "自定义地图 / 历史影像";
        public string CustomUrl = "";
        public string CustomAttribution = "";
        public int CustomMaxZoom = 19;
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamplingBasemaps"); } }
        public static string FileName { get { return Path.Combine(Root, "settings.json"); } }
        public static Settings Load(string path)
        {
            if (!File.Exists(path)) return new Settings();
            Settings s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8));
            if (s == null || s.Port < 1024 || s.Port > 65535 || s.CustomMaxZoom < 0 || s.CustomMaxZoom > 22)
                throw new InvalidDataException("地图源设置文件无效：" + path);
            return s;
        }
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        public string GetKey()
        {
            if (String.IsNullOrEmpty(ProtectedTianDiTuKey)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedTianDiTuKey), null, DataProtectionScope.CurrentUser));
        }
        public void SetKey(string value)
        {
            ProtectedTianDiTuKey = String.IsNullOrWhiteSpace(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    public sealed class Source
    {
        public string Id, Name, Url, LabelUrl, Attribution, Notes;
        public int MinZoom, MaxZoom = 19, Connections = 4;
        public override string ToString() { return Name; }
        public static List<Source> Catalog(Settings s)
        {
            string esri = "https://server.arcgisonline.com/ArcGIS/rest/services/";
            return new List<Source> {
                new Source { Id="esri_imagery", Name="Esri 卫星影像", Url=esri+"World_Imagery/MapServer/tile/{z}/{y}/{x}", LabelUrl="https://services.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}", Attribution="Esri, Maxar, Earthstar Geographics and the GIS User Community", Notes="全球卫星影像，可叠加地名注记。影像日期随地区变化，不代表固定年份。" },
                new Source { Id="esri_streets", Name="Esri 街道地图", Url=esri+"World_Street_Map/MapServer/tile/{z}/{y}/{x}", Attribution="Esri and the GIS User Community", Notes="道路、居民地与地名，适合采样位置示意图。" },
                new Source { Id="esri_topo", Name="Esri 地形图", Url=esri+"World_Topo_Map/MapServer/tile/{z}/{y}/{x}", Attribution="Esri and the GIS User Community", Notes="地形、道路与地名底图。" },
                new Source { Id="osm", Name="OpenStreetMap 标准地图", Url="https://tile.openstreetmap.org/{z}/{x}/{y}.png", Attribution="© OpenStreetMap contributors", Connections=2, Notes="开放街道地图；按需显示并缓存，禁止批量预下载或用作离线底图库。" },
                new Source { Id="carto_light", Name="CARTO 浅色地图", Url="https://a.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png", Attribution="© OpenStreetMap contributors © CARTO", Notes="浅色底图，适合突出采样点和专题图层。" },
                new Source { Id="carto_dark", Name="CARTO 深色地图", Url="https://a.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png", Attribution="© OpenStreetMap contributors © CARTO", Notes="深色底图，适合高对比度展示。" },
                new Source { Id="tianditu", Name="天地图 影像 + 中文注记", Url="https://t0.tianditu.gov.cn/img_w/wmts?service=wmts&request=GetTile&version=1.0.0&layer=img&style=default&tilematrixset=w&format=tiles&tilematrix={z}&tilerow={y}&tilecol={x}&tk={key}", LabelUrl="https://t0.tianditu.gov.cn/cia_w/wmts?service=wmts&request=GetTile&version=1.0.0&layer=cia&style=default&tilematrixset=w&format=tiles&tilematrix={z}&tilerow={y}&tilecol={x}&tk={key}", Attribution="© 天地图", MinZoom=1, MaxZoom=18, Notes="需要有效的天地图服务 Key；影像与中文注记一起切换。" },
                new Source { Id="nasa_night", Name="NASA 夜间灯光（2016 年合成）", Url="https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/VIIRS_Black_Marble/default/2016-01-01/GoogleMapsCompatible_Level8/{z}/{y}/{x}.png", LabelUrl="https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/Coastlines/default/GoogleMapsCompatible_Level9/{z}/{y}/{x}.png", Attribution="NASA GIBS / VIIRS Black Marble (2016)", MaxZoom=8, Notes="2016 年夜间灯光合成影像，并非实时数据；细节等级低于卫星影像，仅用于可视化。" },
                new Source { Id="custom", Name=String.IsNullOrWhiteSpace(s.CustomName)?"自定义地图 / 历史影像":s.CustomName, Url=s.CustomUrl, Attribution=s.CustomAttribution, MaxZoom=s.CustomMaxZoom, Notes="填写 Web Mercator（EPSG:3857）XYZ 地址，含 {z}、{x}、{y}；可添加有授权的真实历史影像。" }
            };
        }
        public static Source Find(Settings s, string id)
        {
            Source source = Catalog(s).FirstOrDefault(x => x.Id == id);
            if (source == null) throw new ArgumentException("未知地图源。");
            if (id == "tianditu" && String.IsNullOrWhiteSpace(s.GetKey())) throw new InvalidOperationException("请先填写并保存天地图 Key。");
            if (id == "custom") ValidateTemplate(source.Url);
            return source;
        }
        public static void ValidateTemplate(string url)
        {
            if (String.IsNullOrWhiteSpace(url) || !url.Contains("{z}") || !url.Contains("{x}") || !url.Contains("{y}"))
                throw new ArgumentException("自定义地址必须包含 {z}、{x}、{y}。");
            Uri uri;
            if (!Uri.TryCreate(url.Replace("{z}","0").Replace("{x}","0").Replace("{y}","0").Replace("{s}","a").Replace("{r}",""), UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("自定义地址必须是有效的 HTTP 或 HTTPS 地址。");
        }
        public string TileUrl(string template, Settings settings, int z, int x, int y)
        {
            return template.Replace("{z}",z.ToString(CultureInfo.InvariantCulture)).Replace("{x}",x.ToString(CultureInfo.InvariantCulture)).Replace("{y}",y.ToString(CultureInfo.InvariantCulture)).Replace("{s}","a").Replace("{r}","").Replace("{key}",Uri.EscapeDataString(settings.GetKey()));
        }
    }

    public static class GatewayClient
    {
        public const string Signature = "sampling-basemaps-1.0.0";
        public static string BaseUrl(Settings settings) { return "http://127.0.0.1:" + settings.Port.ToString(CultureInfo.InvariantCulture); }
        public static bool IsRunning(Settings settings)
        {
            try {
                HttpWebRequest r=(HttpWebRequest)WebRequest.Create(BaseUrl(settings)+"/health"); r.Proxy=null; r.Timeout=700;
                using(WebResponse response=r.GetResponse()) using(StreamReader reader=new StreamReader(response.GetResponseStream())) return reader.ReadToEnd()==Signature;
            } catch { return false; }
        }
        public static void Ensure(Settings settings)
        {
            if (IsRunning(settings)) return;
            string exe=Path.Combine(Path.GetDirectoryName(typeof(GatewayClient).Assembly.Location),"SamplingBasemapGateway.exe");
            if(!File.Exists(exe)) throw new FileNotFoundException("未找到地图服务组件，请重新安装插件。",exe);
            ProcessStartInfo info=new ProcessStartInfo(exe,"--settings \""+Settings.FileName+"\""); info.UseShellExecute=false; info.CreateNoWindow=true; info.WindowStyle=ProcessWindowStyle.Hidden;
            Process.Start(info);
            for(int i=0;i<40;i++) { Thread.Sleep(150); if(IsRunning(settings)) return; }
            throw new InvalidOperationException("本机地图服务未能启动。端口可能被占用，请查看日志："+Path.Combine(Settings.Root,"gateway.log"));
        }
    }
}
