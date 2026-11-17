using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace SamplingBasemaps
{
    public static class TileRenderer
    {
        public const double HalfWorld=20037508.342789244;
        static readonly object[] Locks=Enumerable.Range(0,64).Select(x=>new object()).ToArray();
        public static Func<string,byte[]> DownloadOverride;
        static string Hash(string input) { using(SHA256 sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-",""); }
        static byte[] Download(string url)
        {
            if(DownloadOverride!=null) return DownloadOverride(url);
            string hash=Hash(url), folder=Path.Combine(Settings.Root,"cache",hash.Substring(0,2));
            string file=Path.Combine(folder,hash+".tile"), expires=file+".expires";
            object gate=Locks[(hash.GetHashCode()&Int32.MaxValue)%Locks.Length];
            lock(gate) {
                long until;
                if(File.Exists(file) && File.Exists(expires) && Int64.TryParse(File.ReadAllText(expires),out until) && DateTime.UtcNow.Ticks<until) return File.ReadAllBytes(file);
                HttpWebRequest request=(HttpWebRequest)WebRequest.Create(url);
                request.UserAgent="SamplingBasemaps/1.0 (ArcMap desktop add-in; interactive viewing)";
                request.Timeout=12000; request.ReadWriteTimeout=12000; request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
                using(HttpWebResponse response=(HttpWebResponse)request.GetResponse()) {
                    using(MemoryStream stream=new MemoryStream()) {
                        using(Stream input=response.GetResponseStream()) input.CopyTo(stream);
                        byte[] bytes=stream.ToArray();
                        using(MemoryStream check=new MemoryStream(bytes)) using(Image image=Image.FromStream(check)) { if(image.Width>1024||image.Height>1024) throw new InvalidDataException("瓦片尺寸异常。"); }
                        DateTime expiry=DateTime.UtcNow.AddDays(7); string cc=response.Headers[HttpResponseHeader.CacheControl]??"";
                        foreach(string directive in cc.Split(',')) { string d=directive.Trim(); int age; if(d.StartsWith("max-age=",StringComparison.OrdinalIgnoreCase)&&Int32.TryParse(d.Substring(8),out age)) expiry=DateTime.UtcNow.AddSeconds(Math.Max(0,age)); }
                        DateTime serverExpiry;
                        if(!cc.Contains("max-age=") && DateTime.TryParse(response.Headers[HttpResponseHeader.Expires],CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal,out serverExpiry)) expiry=serverExpiry.ToUniversalTime();
                        if(!cc.Contains("no-store")&&!cc.Contains("no-cache")) {
                            try { Directory.CreateDirectory(folder); File.WriteAllBytes(file,bytes); File.WriteAllText(expires,expiry.Ticks.ToString(CultureInfo.InvariantCulture)); } catch(IOException) { }
                        }
                        return bytes;
                    }
                }
            }
        }
        public static void PruneCache()
        {
            try {
                string root=Path.Combine(Settings.Root,"cache"); if(!Directory.Exists(root)) return;
                FileInfo[] files=new DirectoryInfo(root).GetFiles("*.tile",SearchOption.AllDirectories); long size=files.Sum(x=>x.Length);
                if(size<512L*1024*1024) return;
                foreach(FileInfo file in files.OrderBy(x=>x.LastWriteTimeUtc)) { if(size<384L*1024*1024) break; size-=file.Length; file.Delete(); if(File.Exists(file.FullName+".expires")) File.Delete(file.FullName+".expires"); }
            } catch { }
        }
        sealed class Tile { public int X,Y; public byte[] Bytes; }
        public static byte[] Render(Source source, Settings settings, bool labels, double[] bbox, int width, int height)
        {
            if(width<1||height<1||width>4096||height>4096) throw new ArgumentException("图像宽高必须在 1—4096 像素之间。");
            if(bbox.Length!=4||bbox.Any(x=>Double.IsNaN(x)||Double.IsInfinity(x))||bbox[2]<=bbox[0]||bbox[3]<=bbox[1]||bbox.Any(x=>Math.Abs(x)>HalfWorld*8)) throw new ArgumentException("地图范围无效。");
            double resolution=Math.Max((bbox[2]-bbox[0])/width,(bbox[3]-bbox[1])/height);
            int z=Math.Max(source.MinZoom,Math.Min(source.MaxZoom,(int)Math.Ceiling(Math.Log((HalfWorld*2)/(256*resolution),2))));
            int n=1<<z;
            double left=(bbox[0]+HalfWorld)/(HalfWorld*2)*n*256, right=(bbox[2]+HalfWorld)/(HalfWorld*2)*n*256;
            double top=(HalfWorld-bbox[3])/(HalfWorld*2)*n*256, bottom=(HalfWorld-bbox[1])/(HalfWorld*2)*n*256;
            int x0=(int)Math.Floor(left/256),x1=(int)Math.Ceiling(right/256)-1,y0=Math.Max(0,(int)Math.Floor(top/256)),y1=Math.Min(n-1,(int)Math.Ceiling(bottom/256)-1);
            if((long)(x1-x0+1)*Math.Max(0,y1-y0+1)>512) throw new ArgumentException("地图请求过大，请缩小导出尺寸或范围。");
            using(Bitmap canvas=new Bitmap(width,height,PixelFormat.Format32bppArgb)) {
                using(Graphics g=Graphics.FromImage(canvas)) {
                    g.Clear(Color.Transparent); g.InterpolationMode=InterpolationMode.HighQualityBilinear; g.PixelOffsetMode=PixelOffsetMode.Half;
                    int total=0,failed=0;
                    string[] templates=labels&&!String.IsNullOrEmpty(source.LabelUrl)?new[]{source.Url,source.LabelUrl}:new[]{source.Url};
                    foreach(string template in templates) {
                        List<Tile> tiles=new List<Tile>(); for(int y=y0;y<=y1;y++) for(int x=x0;x<=x1;x++) tiles.Add(new Tile{X=x,Y=y});
                        Parallel.ForEach(tiles,new ParallelOptions{MaxDegreeOfParallelism=source.Connections},tile=>{
                            try { tile.Bytes=Download(source.TileUrl(template,settings,z,((tile.X%n)+n)%n,tile.Y)); } catch { Interlocked.Increment(ref failed); }
                        });
                        foreach(Tile tile in tiles) {
                            total++;
                            if(tile.Bytes==null) continue;
                            using(MemoryStream input=new MemoryStream(tile.Bytes)) using(Image image=Image.FromStream(input)) {
                                float dx=(float)((tile.X*256-left)/(right-left)*width), dy=(float)((tile.Y*256-top)/(bottom-top)*height);
                                float dw=(float)(256/(right-left)*width), dh=(float)(256/(bottom-top)*height);
                                using(ImageAttributes attributes=new ImageAttributes()) { attributes.SetWrapMode(WrapMode.TileFlipXY); g.DrawImage(image,new[]{new PointF(dx,dy),new PointF(dx+dw,dy),new PointF(dx,dy+dh)},new RectangleF(0,0,image.Width,image.Height),GraphicsUnit.Pixel,attributes); }
                            }
                        }
                    }
                    if(failed>0) throw new IOException("地图源有 "+failed+" / "+total+" 张瓦片未能获取。请检查网络、来源地址或天地图 Key 后重试。");
                }
                using(MemoryStream output=new MemoryStream()) { canvas.Save(output,ImageFormat.Png); return output.ToArray(); }
            }
        }
    }

    public static class Gateway
    {
        static string settingsPath;
        static readonly SemaphoreSlim Slots=new SemaphoreSlim(12);
        static readonly object LogLock=new object();
        public static void Log(string text) { try { lock(LogLock) { Directory.CreateDirectory(Settings.Root); string p=Path.Combine(Settings.Root,"gateway.log"); if(File.Exists(p)&&new FileInfo(p).Length>1024*1024) File.WriteAllText(p,""); File.AppendAllText(p,DateTime.Now.ToString("s")+" "+text+Environment.NewLine); } } catch { } }
        public static int Main(string[] args)
        {
            settingsPath=Settings.FileName;
            for(int i=0;i<args.Length-1;i++) if(args[i]=="--settings") settingsPath=args[++i];
            try {
                ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit=16;
                Settings settings=Settings.Load(settingsPath);
                using(Mutex mutex=new Mutex(false,"Local\\SamplingBasemapGateway_"+settings.Port)) {
                    if(!mutex.WaitOne(0)) return 0;
                    TcpListener listener=new TcpListener(IPAddress.Loopback,settings.Port); listener.Start();
                    Task.Run((Action)TileRenderer.PruneCache); Log("Started version="+GatewayClient.Signature+" port="+settings.Port);
                    while(true) {
                        TcpClient client=listener.AcceptTcpClient();
                        if(!Slots.Wait(0)) { client.Close(); continue; }
                        Task.Run(()=> { try { Handle(client); } catch(Exception error) { Log("Request failed: "+error.GetType().Name); } finally { client.Close(); Slots.Release(); } });
                    }
                }
            } catch(Exception error) { Log("Startup failed: "+error.Message); return 1; }
        }
        static string Xml(string value) { return SecurityElement.Escape(value??""); }
        public static string Capabilities(Source source,Settings settings,bool labels)
        {
            string url=Xml(GatewayClient.BaseUrl(settings)+"/wms/"+source.Id+"/"+(labels?"1":"0")+"?");
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><WMT_MS_Capabilities version=\"1.1.1\"><Service><Name>OGC:WMS</Name><Title>"+Xml(source.Name)+"</Title><Abstract>"+Xml(source.Notes+" 来源："+source.Attribution)+"</Abstract><OnlineResource xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:type=\"simple\" xlink:href=\""+url+"\"/><Fees>Source terms apply</Fees><AccessConstraints>"+Xml(source.Attribution)+"</AccessConstraints></Service><Capability><Request><GetCapabilities><Format>application/vnd.ogc.wms_xml</Format><DCPType><HTTP><Get><OnlineResource xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:type=\"simple\" xlink:href=\""+url+"\"/></Get></HTTP></DCPType></GetCapabilities><GetMap><Format>image/png</Format><DCPType><HTTP><Get><OnlineResource xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:type=\"simple\" xlink:href=\""+url+"\"/></Get></HTTP></DCPType></GetMap></Request><Exception><Format>application/vnd.ogc.se_xml</Format></Exception><Layer queryable=\"0\"><Title>"+Xml(source.Name)+"</Title><SRS>EPSG:3857</SRS><LatLonBoundingBox minx=\"-180\" miny=\"-85.0511287798\" maxx=\"180\" maxy=\"85.0511287798\"/><BoundingBox SRS=\"EPSG:3857\" minx=\"-20037508.342789244\" miny=\"-20037508.342789244\" maxx=\"20037508.342789244\" maxy=\"20037508.342789244\"/><Layer queryable=\"0\" opaque=\"0\"><Name>basemap</Name><Title>"+Xml(source.Name)+"</Title><Attribution><Title>"+Xml(source.Attribution)+"</Title></Attribution><Style><Name>default</Name><Title>Default</Title></Style></Layer></Layer></Capability></WMT_MS_Capabilities>";
        }
        static void Handle(TcpClient client)
        {
            client.ReceiveTimeout=5000; client.SendTimeout=15000;
            using(NetworkStream stream=client.GetStream()) {
                // Read only a bounded HTTP header. ArcMap's WMS client uses GET.
                MemoryStream header=new MemoryStream(); int b,match=0;
                while(header.Length<16384&&(b=stream.ReadByte())>=0) { header.WriteByte((byte)b); match=(match==0&&b==13)?1:(match==1&&b==10)?2:(match==2&&b==13)?3:(match==3&&b==10)?4:0; if(match==4) break; }
                string[] first=Encoding.ASCII.GetString(header.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None)[0].Split(' ');
                if(first.Length<2||first[0]!="GET"||match!=4) { Send(stream,400,"text/plain",Encoding.UTF8.GetBytes("Bad request")); return; }
                Uri uri=new Uri("http://localhost"+first[1]);
                if(uri.AbsolutePath=="/health") { Send(stream,200,"text/plain",Encoding.UTF8.GetBytes(GatewayClient.Signature)); return; }
                try {
                    string[] parts=uri.AbsolutePath.Trim('/').Split('/');
                    if(parts.Length!=3||parts[0]!="wms"||(parts[2]!="0"&&parts[2]!="1")) throw new ArgumentException("未知服务地址。");
                    Settings settings=Settings.Load(settingsPath); Source source=Source.Find(settings,parts[1]); bool labels=parts[2]=="1";
                    var query=HttpUtility.ParseQueryString(uri.Query); string operation=query["REQUEST"]??"GetCapabilities";
                    if(operation.Equals("GetCapabilities",StringComparison.OrdinalIgnoreCase)) Send(stream,200,"application/vnd.ogc.wms_xml",Encoding.UTF8.GetBytes(Capabilities(source,settings,labels)));
                    else if(operation.Equals("GetMap",StringComparison.OrdinalIgnoreCase)) {
                        string srs=query["SRS"]??query["CRS"];
                        if(!String.Equals(srs,"EPSG:3857",StringComparison.OrdinalIgnoreCase)&&!String.Equals(srs,"EPSG:102100",StringComparison.OrdinalIgnoreCase)&&!String.Equals(srs,"EPSG:900913",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("仅支持 EPSG:3857；请让 ArcMap 自动投影该图层。");
                        if(query["LAYERS"]!="basemap"||!String.Equals(query["FORMAT"],"image/png",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("仅支持 basemap 图层和 image/png 格式。");
                        double[] bbox=(query["BBOX"]??"").Split(',').Select(x=>Double.Parse(x,CultureInfo.InvariantCulture)).ToArray();
                        int width=Int32.Parse(query["WIDTH"],CultureInfo.InvariantCulture),height=Int32.Parse(query["HEIGHT"],CultureInfo.InvariantCulture);
                        Send(stream,200,"image/png",TileRenderer.Render(source,settings,labels,bbox,width,height));
                    } else throw new ArgumentException("不支持该 WMS 操作。");
                } catch(Exception error) {
                    // Log only the error type: upstream URLs may contain API keys.
                    Log("WMS failed: "+error.GetType().Name);
                    string message=error is ArgumentException||error is InvalidOperationException||error is IOException?error.Message:"地图请求失败，请检查地图源配置与网络。";
                    Send(stream,200,"application/vnd.ogc.se_xml",Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ServiceExceptionReport version=\"1.1.1\"><ServiceException>"+Xml(message)+"</ServiceException></ServiceExceptionReport>"));
                }
            }
        }
        static void Send(Stream stream,int status,string type,byte[] bytes)
        {
            byte[] head=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+(status==200?" OK":" Bad Request")+"\r\nContent-Type: "+type+"; charset=utf-8\r\nContent-Length: "+bytes.Length+"\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head,0,head.Length); stream.Write(bytes,0,bytes.Length);
        }
    }
}
