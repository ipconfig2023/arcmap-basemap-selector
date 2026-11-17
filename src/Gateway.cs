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
        public static string CacheDirectory=Path.Combine(Settings.Root,"cache");
        sealed class CachedImage { public byte[] Bytes; public long Until; }
        sealed class MemoryImages
        {
            sealed class Entry { public string Key; public CachedImage Image; }
            readonly Dictionary<string,LinkedListNode<Entry>> index=new Dictionary<string,LinkedListNode<Entry>>();
            readonly LinkedList<Entry> recent=new LinkedList<Entry>();
            readonly long limit; long size;
            public MemoryImages(long bytes) { limit=bytes; }
            void Remove(LinkedListNode<Entry> node) { size-=node.Value.Image.Bytes.Length; index.Remove(node.Value.Key); recent.Remove(node); }
            public CachedImage Get(string key) {
                lock(index) {
                    LinkedListNode<Entry> node; if(!index.TryGetValue(key,out node)) return null;
                    if(node.Value.Image.Until<=DateTime.UtcNow.Ticks) { Remove(node); return null; }
                    recent.Remove(node); recent.AddFirst(node); return node.Value.Image;
                }
            }
            public void Put(string key,CachedImage image) {
                if(image.Until<=DateTime.UtcNow.Ticks||image.Bytes.Length>limit) return;
                lock(index) {
                    LinkedListNode<Entry> old; if(index.TryGetValue(key,out old)) Remove(old);
                    while(size+image.Bytes.Length>limit&&recent.Last!=null) Remove(recent.Last);
                    index[key]=recent.AddFirst(new Entry{Key=key,Image=image}); size+=image.Bytes.Length;
                }
            }
        }
        static readonly MemoryImages TileMemory=new MemoryImages(32L*1024*1024),ViewMemory=new MemoryImages(16L*1024*1024);
        static string Hash(string input) { using(SHA256 sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-",""); }
        static void ValidateImage(byte[] bytes) {
            using(MemoryStream check=new MemoryStream(bytes)) using(Image image=Image.FromStream(check)) { if(image.Width>1024||image.Height>1024) throw new InvalidDataException("瓦片尺寸异常。"); }
        }
        static void WriteAtomic(string path,byte[] bytes) {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { File.WriteAllBytes(temp,bytes); if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        static CachedImage Download(string url)
        {
            if(DownloadOverride!=null) return new CachedImage{Bytes=DownloadOverride(url),Until=0};
            string hash=Hash(url), folder=Path.Combine(CacheDirectory,hash.Substring(0,2));
            CachedImage cached=TileMemory.Get(hash); if(cached!=null) return cached;
            string file=Path.Combine(folder,hash+".tile"), expires=file+".expires";
            object gate=Locks[(hash.GetHashCode()&Int32.MaxValue)%Locks.Length];
            lock(gate) {
                cached=TileMemory.Get(hash); if(cached!=null) return cached;
                try {
                    long until;
                    if(File.Exists(file)&&File.Exists(expires)&&Int64.TryParse(File.ReadAllText(expires),out until)&&DateTime.UtcNow.Ticks<until) {
                        byte[] bytes=File.ReadAllBytes(file); ValidateImage(bytes);
                        cached=new CachedImage{Bytes=bytes,Until=until}; TileMemory.Put(hash,cached);
                        if(File.GetLastWriteTimeUtc(file)<DateTime.UtcNow.AddMinutes(-5)) File.SetLastWriteTimeUtc(file,DateTime.UtcNow);
                        return cached;
                    }
                } catch(IOException) { } catch(UnauthorizedAccessException) { } catch(ArgumentException) { }
                HttpWebRequest request=(HttpWebRequest)WebRequest.Create(url);
                request.UserAgent="SamplingBasemaps/1.0.1 (ArcMap desktop add-in; interactive viewing)";
                request.Timeout=12000; request.ReadWriteTimeout=12000; request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
                using(HttpWebResponse response=(HttpWebResponse)request.GetResponse()) {
                    using(MemoryStream stream=new MemoryStream()) {
                        using(Stream input=response.GetResponseStream()) input.CopyTo(stream);
                        byte[] bytes=stream.ToArray();
                        ValidateImage(bytes);
                        DateTime expiry=DateTime.UtcNow.AddDays(7); string cc=response.Headers[HttpResponseHeader.CacheControl]??"";
                        bool allowed=true,hasMaxAge=false; long responseAge;
                        if(!Int64.TryParse(response.Headers["Age"],out responseAge)) responseAge=0;
                        foreach(string directive in cc.Split(',')) {
                            string d=directive.Trim(); int age;
                            if(d.Equals("no-store",StringComparison.OrdinalIgnoreCase)||d.Split('=')[0].Equals("no-cache",StringComparison.OrdinalIgnoreCase)) allowed=false;
                            if(d.StartsWith("max-age=",StringComparison.OrdinalIgnoreCase)&&Int32.TryParse(d.Substring(8).Trim('"'),out age)) { expiry=DateTime.UtcNow.AddSeconds(Math.Max(0,(long)age-Math.Max(0,responseAge))); hasMaxAge=true; }
                        }
                        DateTime serverExpiry;
                        if(!hasMaxAge&&DateTime.TryParse(response.Headers[HttpResponseHeader.Expires],CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal,out serverExpiry)) expiry=serverExpiry.ToUniversalTime();
                        cached=new CachedImage{Bytes=bytes,Until=allowed?expiry.Ticks:0};
                        if(allowed&&expiry>DateTime.UtcNow) {
                            TileMemory.Put(hash,cached);
                            try { Directory.CreateDirectory(folder); WriteAtomic(file,bytes); WriteAtomic(expires,Encoding.ASCII.GetBytes(expiry.Ticks.ToString(CultureInfo.InvariantCulture))); } catch(IOException) { } catch(UnauthorizedAccessException) { }
                        }
                        return cached;
                    }
                }
            }
        }
        public static void PruneCache(int limitMb)
        {
            try {
                string root=CacheDirectory; if(!Directory.Exists(root)) return;
                FileInfo[] files=new DirectoryInfo(root).GetFiles("*.tile",SearchOption.AllDirectories); long size=files.Sum(x=>x.Length);
                long limit=(long)Math.Max(256,Math.Min(16384,limitMb))*1024*1024;
                if(size<=limit) return;
                foreach(FileInfo file in files.OrderBy(x=>x.LastWriteTimeUtc)) { if(size<limit*3/4) break; long length=file.Length; file.Delete(); size-=length; if(File.Exists(file.FullName+".expires")) File.Delete(file.FullName+".expires"); }
            } catch { }
        }
        sealed class Tile { public int X,Y,Layer; public CachedImage Image; }
        public static byte[] Render(Source source, Settings settings, bool labels, double[] bbox, int width, int height)
        {
            if(width<1||height<1||width>4096||height>4096) throw new ArgumentException("图像宽高必须在 1—4096 像素之间。");
            if(bbox.Length!=4||bbox.Any(x=>Double.IsNaN(x)||Double.IsInfinity(x))||bbox[2]<=bbox[0]||bbox[3]<=bbox[1]||bbox.Any(x=>Math.Abs(x)>HalfWorld*8)) throw new ArgumentException("地图范围无效。");
            string[] templates=(labels&&!String.IsNullOrEmpty(source.LabelUrl)?new[]{source.Url,source.LabelUrl}:new[]{source.Url})
                .Select(t=>t.Contains("{key}")?t.Replace("{key}",Uri.EscapeDataString(settings.GetKey())):t).ToArray();
            string viewKey=Hash(String.Join("\n",templates)+"\n"+source.MinZoom+":"+source.MaxZoom+":"+width+":"+height+":"+String.Join(",",bbox.Select(x=>x.ToString("R",CultureInfo.InvariantCulture))));
            if(DownloadOverride==null) { CachedImage view=ViewMemory.Get(viewKey); if(view!=null) return view.Bytes; }
            double resolution=Math.Max((bbox[2]-bbox[0])/width,(bbox[3]-bbox[1])/height);
            int z=Math.Max(source.MinZoom,Math.Min(source.MaxZoom,(int)Math.Ceiling(Math.Log((HalfWorld*2)/(256*resolution),2))));
            int n=1<<z;
            double left=(bbox[0]+HalfWorld)/(HalfWorld*2)*n*256, right=(bbox[2]+HalfWorld)/(HalfWorld*2)*n*256;
            double top=(HalfWorld-bbox[3])/(HalfWorld*2)*n*256, bottom=(HalfWorld-bbox[1])/(HalfWorld*2)*n*256;
            int x0=(int)Math.Floor(left/256),x1=(int)Math.Ceiling(right/256)-1,y0=Math.Max(0,(int)Math.Floor(top/256)),y1=Math.Min(n-1,(int)Math.Ceiling(bottom/256)-1);
            if((long)(x1-x0+1)*Math.Max(0,y1-y0+1)>512) throw new ArgumentException("地图请求过大，请缩小导出尺寸或范围。");
            List<Tile> tiles=new List<Tile>();
            for(int layer=0;layer<templates.Length;layer++) for(int y=y0;y<=y1;y++) for(int x=x0;x<=x1;x++) tiles.Add(new Tile{X=x,Y=y,Layer=layer});
            int failed=0;
            // One bounded queue lets imagery and labels load together without doubling provider concurrency.
            Parallel.ForEach(tiles.OrderBy(t=>t.Y).ThenBy(t=>t.X).ThenBy(t=>t.Layer),new ParallelOptions{MaxDegreeOfParallelism=source.Connections},tile=>{
                try { tile.Image=Download(source.TileUrl(templates[tile.Layer],settings,z,((tile.X%n)+n)%n,tile.Y)); } catch { Interlocked.Increment(ref failed); }
            });
            if(failed>0) throw new IOException("地图源有 "+failed+" / "+tiles.Count+" 张瓦片未能获取。请检查网络、来源地址或天地图 Key 后重试。");
            long viewUntil=tiles.Count==0?DateTime.UtcNow.AddDays(7).Ticks:tiles.Min(t=>t.Image.Until);
            using(Bitmap canvas=new Bitmap(width,height,PixelFormat.Format32bppArgb)) {
                using(Graphics g=Graphics.FromImage(canvas)) {
                    g.Clear(Color.Transparent); g.InterpolationMode=InterpolationMode.HighQualityBilinear; g.PixelOffsetMode=PixelOffsetMode.Half;
                        foreach(Tile tile in tiles) {
                            using(MemoryStream input=new MemoryStream(tile.Image.Bytes)) using(Image image=Image.FromStream(input)) {
                                float dx=(float)((tile.X*256-left)/(right-left)*width), dy=(float)((tile.Y*256-top)/(bottom-top)*height);
                                float dw=(float)(256/(right-left)*width), dh=(float)(256/(bottom-top)*height);
                                using(ImageAttributes attributes=new ImageAttributes()) { attributes.SetWrapMode(WrapMode.TileFlipXY); g.DrawImage(image,new[]{new PointF(dx,dy),new PointF(dx+dw,dy),new PointF(dx,dy+dh)},new RectangleF(0,0,image.Width,image.Height),GraphicsUnit.Pixel,attributes); }
                            }
                        }
                }
                using(MemoryStream output=new MemoryStream()) {
                    canvas.Save(output,ImageFormat.Png); byte[] bytes=output.ToArray();
                    if(DownloadOverride==null) ViewMemory.Put(viewKey,new CachedImage{Bytes=bytes,Until=viewUntil});
                    return bytes;
                }
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
            for(int i=0;i<args.Length-1;i++) {
                if(args[i]=="--settings") settingsPath=args[++i];
                else if(args[i]=="--cache-root") TileRenderer.CacheDirectory=Path.GetFullPath(args[++i]);
            }
            try {
                ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit=16;
                Settings settings=Settings.Load(settingsPath);
                using(Mutex mutex=new Mutex(false,"Local\\SamplingBasemapGateway_"+settings.Port)) {
                    if(!mutex.WaitOne(0)) return 0;
                    TcpListener listener=new TcpListener(IPAddress.Loopback,settings.Port); listener.Start();
                    Task.Run(()=>TileRenderer.PruneCache(settings.CacheLimitMb));
                    using(Timer maintenance=new Timer(o=> { try { TileRenderer.PruneCache(Settings.Load(settingsPath).CacheLimitMb); } catch { } },null,TimeSpan.FromMinutes(5),TimeSpan.FromMinutes(5))) {
                    Log("Started version=1.0.1 protocol="+GatewayClient.Signature+" port="+settings.Port);
                    while(true) {
                        TcpClient client=listener.AcceptTcpClient();
                        if(!Slots.Wait(0)) { client.Close(); continue; }
                        Task.Run(()=> { try { Handle(client); } catch(Exception error) { Log("Request failed: "+error.GetType().Name); } finally { client.Close(); Slots.Release(); } });
                    }
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
