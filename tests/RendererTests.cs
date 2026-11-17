using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Xml;
using SamplingBasemaps;

static class RendererTests
{
    static int count;
    static void Assert(bool value,string message) { if(!value) throw new Exception(message); count++; Console.WriteLine("PASS "+message); }
    static byte[] Solid(Color color) { using(Bitmap b=new Bitmap(256,256)) { using(Graphics g=Graphics.FromImage(b)) g.Clear(color); using(MemoryStream s=new MemoryStream()) { b.Save(s,ImageFormat.Png); return s.ToArray(); } } }
    static Color Pixel(byte[] bytes,int x,int y) { using(MemoryStream s=new MemoryStream(bytes)) using(Bitmap b=new Bitmap(s)) return b.GetPixel(x,y); }
    static void Throws(Action action,string message) { bool threw=false; try{action();}catch{threw=true;} Assert(threw,message); }
    static int Main()
    {
        try {
            Settings s=new Settings(); Source source=new Source{Id="fixture",Name="Fixture & test",Url="https://fixture/{z}/{x}/{y}",LabelUrl="https://labels/{z}/{x}/{y}",MaxZoom=4,Attribution="A & B"};
            double h=TileRenderer.HalfWorld;
            TileRenderer.DownloadOverride=url=>Solid(url.StartsWith("https://labels/")?Color.FromArgb(128,0,0,255):Color.Red);
            byte[] plain=TileRenderer.Render(source,s,false,new[]{-h,-h,h,h},256,256);
            Assert(Pixel(plain,128,128).R>250&&Pixel(plain,128,128).B<5,"World tile is projected and drawn");
            Color blend=Pixel(TileRenderer.Render(source,s,true,new[]{-h,-h,h,h},256,256),128,128);
            Assert(blend.R>115&&blend.R<140&&blend.B>115&&blend.B<140,"Transparent labels blend over imagery");
            TileRenderer.DownloadOverride=url=>Solid(url.Contains("/1/0/")?Color.Red:Color.Green);
            byte[] quadrants=TileRenderer.Render(source,s,false,new[]{-h,-h,h,h},512,512);
            Assert(Pixel(quadrants,50,50).R>250&&Pixel(quadrants,450,50).G>120,"XYZ columns align across adjacent tiles");
            byte[] wrap=TileRenderer.Render(source,s,false,new[]{h,0,h*2,h},256,256);
            Assert(Pixel(wrap,128,128).R>250,"Antimeridian wraps columns");
            TileRenderer.DownloadOverride=url=>Solid(Color.Red);
            Color polar=Pixel(TileRenderer.Render(source,s,false,new[]{-h,h,h,h*2},256,128),128,64);
            Assert(polar.A==0,"Outside valid Mercator latitude stays transparent");
            Throws(()=>TileRenderer.Render(source,s,false,new[]{-h,-h,h,h},5000,256),"Oversized map requests are rejected");
            Throws(()=>TileRenderer.Render(source,s,false,new[]{0.0,0.0,Double.NaN,1.0},256,256),"Non-finite bounds are rejected");
            TileRenderer.DownloadOverride=url=>{throw new IOException("fixture");};
            Throws(()=>TileRenderer.Render(source,s,false,new[]{-h,-h,h,h},256,256),"Failed upstream tiles produce an explicit failure");
            Throws(()=>Source.Find(s,"tianditu"),"TianDiTu requires a real key");
            Throws(()=>Source.Find(s,"custom"),"Empty custom sources are rejected");
            Throws(()=>Source.ValidateTemplate("file:///C:/tiles/{z}/{x}/{y}"),"Custom sources accept only HTTP/S");
            s.SetKey("fixture-key-123"); Assert(s.GetKey()=="fixture-key-123"&&!s.ProtectedTianDiTuKey.Contains("fixture"),"Keys roundtrip through Windows user encryption");
            string path=Path.Combine(Path.GetTempPath(),"SamplingBasemaps-tests-"+Guid.NewGuid().ToString("N"),"settings.json");
            s.Save(path); s.SelectedSource="carto_light";s.Save(path); Assert(Settings.Load(path).SelectedSource=="carto_light","Settings replace atomically and roundtrip");
            XmlDocument doc=new XmlDocument();doc.LoadXml(Gateway.Capabilities(source,s,true));
            Assert(doc.DocumentElement.GetAttribute("version")=="1.1.1"&&doc.SelectSingleNode("//Layer/Layer/Name").InnerText=="basemap","Capabilities expose an ArcMap-compatible named layer");
            Assert(doc.SelectSingleNode("//Attribution/Title").InnerText=="A & B","Capabilities preserve attribution and XML escapes");
            Assert(Source.Catalog(s).Count==9&&!Source.Catalog(s).Any(x=>x.Name.Contains("2024")),"Catalog includes eight real providers and a custom source without false historical years");
            Console.WriteLine("TOTAL "+count+" passed");return 0;
        } catch(Exception error) {Console.WriteLine(error);return 1;}
    }
}
