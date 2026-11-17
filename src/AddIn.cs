using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using ESRI.ArcGIS.ArcMapUI;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.Framework;
using ESRI.ArcGIS.Geometry;
using ESRI.ArcGIS.GISClient;
using ESRI.ArcGIS.esriSystem;
using Button = System.Windows.Forms.Button;

namespace SamplingBasemaps
{
    public class SelectBasemapButton : ESRI.ArcGIS.Desktop.AddIns.Button
    {
        protected override void OnClick()
        {
            try {
                IApplication application=Hook as IApplication;
                using(SourceForm form=new SourceForm(application)) form.ShowDialog(new ArcOwner(application.hWnd));
            } catch(Exception e) { MessageBox.Show(e.Message,"地图源",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        }
        protected override void OnUpdate() { Enabled=Hook is IApplication; }
        sealed class ArcOwner:IWin32Window { readonly IntPtr handle; public ArcOwner(int hwnd){handle=new IntPtr(hwnd);} public IntPtr Handle { get{return handle;} } }
    }
    public class GatewayExtension : ESRI.ArcGIS.Desktop.AddIns.Extension
    {
        protected override void OnStartup()
        {
            Task.Run(()=> { try { Settings s=Settings.Load(Settings.FileName); if(!File.Exists(Settings.FileName)) s.Save(Settings.FileName); GatewayClient.Ensure(s); } catch { } });
        }
    }
    public static class ArcLayers
    {
        public const string Marker="SamplingBasemaps.Managed/v1";
        public static ILayer Create(Settings settings,Source source)
        {
            IPropertySet properties=new PropertySetClass();
            properties.SetProperty("URL",GatewayClient.BaseUrl(settings)+"/wms/"+source.Id+"/"+(settings.Labels?"1":"0"));
            properties.SetProperty("VERSION","1.1.1");
            IWMSConnectionName connection=new WMSConnectionNameClass(); connection.ConnectionProperties=properties;
            IWMSGroupLayer group=new WMSMapLayerClass();
            if(!((IDataLayer)group).Connect((IName)connection)) throw new InvalidOperationException("ArcMap 未能连接地图服务。");
            IWMSServiceDescription description=group.WMSServiceDescription;
            group.Clear();
            for(int i=0;i<description.LayerDescriptionCount;i++) AddDescription(group,description.get_LayerDescription(i));
            if(group.Count==0) throw new InvalidOperationException("服务未返回可添加的底图图层。");
            ILayer layer=(ILayer)group; layer.Name="[Sampling 底图] "+source.Name; layer.Visible=true;
            ((ILayerGeneralProperties)layer).LayerDescription=Marker+"\n"+source.Attribution+"\n"+source.Notes;
            ((IWMSMapLayer)layer).TransparentBackground=true;
            return layer;
        }
        static void AddDescription(IWMSGroupLayer group,IWMSLayerDescription description)
        {
            if(description.Name=="basemap") { ILayer child=(ILayer)group.CreateWMSLayer(description); child.Visible=true; group.Add(child); return; }
            for(int i=0;i<description.LayerDescriptionCount;i++) AddDescription(group,description.get_LayerDescription(i));
        }
        public static bool Managed(ILayer layer)
        {
            ILayerGeneralProperties info=layer as ILayerGeneralProperties;
            return info!=null && (info.LayerDescription??"").StartsWith(Marker,StringComparison.Ordinal);
        }
        public static void AddToMap(IMap map,Settings settings,ILayer layer)
        {
            IActiveView view=(IActiveView)map;
            bool empty=map.LayerCount==0; IEnvelope oldExtent=empty?null:(IEnvelope)((IClone)view.Extent).Clone();
            map.AddLayer(layer); map.MoveLayer(layer,map.LayerCount-1);
            if(settings.Replace) for(int i=map.LayerCount-1;i>=0;i--) { ILayer other=map.get_Layer(i); if(!Object.ReferenceEquals(other,layer)&&Managed(other)) map.DeleteLayer(other); }
            if(!empty) view.Extent=oldExtent;
            else {
                IEnvelope extent=new EnvelopeClass(); extent.PutCoords(73,18,135,54);
                extent.SpatialReference=new SpatialReferenceEnvironmentClass().CreateGeographicCoordinateSystem(4326);
                extent.Project(map.SpatialReference); view.Extent=extent;
            }
        }
    }

    public sealed class SourceForm:Form
    {
        readonly IApplication application;
        readonly ComboBox sources=new ComboBox();
        readonly Label notes=new Label(),credits=new Label(),status=new Label();
        readonly TextBox key=new TextBox(),customName=new TextBox(),customUrl=new TextBox(),customCredits=new TextBox();
        readonly NumericUpDown customZoom=new NumericUpDown();
        readonly NumericUpDown cacheLimit=new NumericUpDown();
        readonly CheckBox labels=new CheckBox(),replace=new CheckBox();
        readonly Button add=new Button(),test=new Button(),save=new Button();
        Settings settings;
        public SourceForm(IApplication app)
        {
            application=app; settings=Settings.Load(Settings.FileName);
            Text="Sampling 地图源 · ArcMap 10.8 · v1.0.1"; Font=new Font("Microsoft YaHei UI",9F); BackColor=Color.White;
            StartPosition=FormStartPosition.CenterParent; Size=new Size(760,760); MinimumSize=new Size(700,720); AutoScaleMode=AutoScaleMode.Dpi;
            TableLayoutPanel root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=10,Padding=new Padding(22,16,22,16)};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,40)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,38)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,63)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,46)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,40)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,185)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,59)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            Controls.Add(root);
            root.Controls.Add(new Label{Text="选择地图来源",Font=new Font(Font.FontFamily,16,FontStyle.Bold),Dock=DockStyle.Fill,ForeColor=Color.FromArgb(31,58,72)},0,0);
            sources.DropDownStyle=ComboBoxStyle.DropDownList; sources.Dock=DockStyle.Fill;
            foreach(Source source in Source.Catalog(settings)) sources.Items.Add(source);
            root.Controls.Add(Row("地图来源",sources),0,1);
            notes.Dock=DockStyle.Fill; notes.Padding=new Padding(0,7,0,0); notes.ForeColor=Color.FromArgb(78,89,97); root.Controls.Add(notes,0,2);
            FlowLayoutPanel options=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
            labels.Text="叠加地名 / 海岸线注记"; labels.AutoSize=true; labels.Checked=settings.Labels;
            replace.Text="替换此前由此插件添加的底图"; replace.AutoSize=true; replace.Checked=settings.Replace;
            options.Controls.Add(labels); options.Controls.Add(replace); root.Controls.Add(options,0,3);
            key.UseSystemPasswordChar=true; key.Dock=DockStyle.Fill; key.Text=settings.GetKey(); root.Controls.Add(Row("天地图 Key",key),0,4);
            FlowLayoutPanel cache=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
            cacheLimit.Minimum=256; cacheLimit.Maximum=16384; cacheLimit.Increment=256; cacheLimit.Value=settings.CacheLimitMb; cacheLimit.Width=82;
            cache.Controls.Add(cacheLimit); cache.Controls.Add(new Label{Text="MB · 已浏览区域自动缓存",AutoSize=true,Padding=new Padding(0,5,8,0)});
            Button cacheFolder=new Button{Text="打开缓存",AutoSize=true,Height=28};
            cacheFolder.Click+=(o,e)=> { try { string folder=System.IO.Path.Combine(Settings.Root,"cache"); Directory.CreateDirectory(folder); System.Diagnostics.Process.Start("explorer.exe",folder); } catch(Exception error) { ShowError(error); } };
            cache.Controls.Add(cacheFolder); root.Controls.Add(Row("缓存上限",cache),0,5);
            GroupBox custom=new GroupBox{Text="自定义地图 / 真实历史影像",Dock=DockStyle.Fill,Padding=new Padding(12,20,12,8)};
            TableLayoutPanel customRows=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};
            for(int i=0;i<4;i++) customRows.RowStyles.Add(new RowStyle(SizeType.Percent,25));
            customName.Text=settings.CustomName; customUrl.Text=settings.CustomUrl; customCredits.Text=settings.CustomAttribution; customZoom.Minimum=0; customZoom.Maximum=22; customZoom.Value=settings.CustomMaxZoom;
            customRows.Controls.Add(Row("显示名称",customName),0,0); customRows.Controls.Add(Row("XYZ 地址",customUrl),0,1); customRows.Controls.Add(Row("来源署名",customCredits),0,2); customRows.Controls.Add(Row("最大级别",customZoom),0,3); custom.Controls.Add(customRows); root.Controls.Add(custom,0,6);
            credits.Dock=DockStyle.Fill; credits.Padding=new Padding(0,10,0,0); credits.ForeColor=Color.FromArgb(95,103,111); root.Controls.Add(credits,0,7);
            status.Dock=DockStyle.Fill; status.Padding=new Padding(10,8,10,6); status.BackColor=Color.FromArgb(242,246,249); status.Text="已浏览瓦片自动缓存，拖回原区域会复用。新区域、新缩放级别和过期内容需要联网加载。"; root.Controls.Add(status,0,8);
            FlowLayoutPanel buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
            Button close=new Button{Text="关闭",Width=76,Height=32}; close.Click+=(o,e)=>Close(); CancelButton=close;
            add.Text=application==null?"预览模式":"添加 / 切换"; add.Width=118; add.Height=32; add.Enabled=application!=null;
            test.Text="测试连接"; test.Width=96; test.Height=32; save.Text="保存设置"; save.Width=96; save.Height=32;
            buttons.Controls.Add(close); buttons.Controls.Add(add); buttons.Controls.Add(test); buttons.Controls.Add(save); root.Controls.Add(buttons,0,9);
            sources.SelectedIndexChanged+=(o,e)=>{Source source=(Source)sources.SelectedItem; notes.Text=source.Notes; credits.Text="地图来源："+source.Attribution+"\n出图时请保留来源署名。自定义地址需使用 EPSG:3857 XYZ，含 {z}、{x}、{y}。"; key.Enabled=source.Id=="tianditu"; custom.Enabled=source.Id=="custom"; labels.Enabled=!String.IsNullOrEmpty(source.LabelUrl);};
            sources.SelectedIndex=Math.Max(0,sources.Items.Cast<Source>().ToList().FindIndex(x=>x.Id==settings.SelectedSource));
            save.Click+=(o,e)=>{try { ReadSettings(false); status.Text="设置已保存。"; } catch(Exception error) { ShowError(error); }};
            test.Click+=async(o,e)=>{try { Source source=ReadSettings(true); Busy(true,"正在测试地图源，请稍候……"); await Task.Run(()=>Probe(source)); status.Text="连接成功："+source.Name+"。"; } catch(Exception error) { ShowError(error); } finally { Busy(false,null); }};
            add.Click+=async(o,e)=>{
                try {
                    Source source=ReadSettings(true); Busy(true,"正在连接并添加底图，请稍候……");
                    await Task.Run(()=> { GatewayClient.Ensure(settings); Probe(source); });
                    ILayer layer=ArcLayers.Create(settings,source);
                    IMxDocument document=(IMxDocument)application.Document; ArcLayers.AddToMap(document.FocusMap,settings,layer);
                    document.UpdateContents(); document.ActiveView.Refresh(); status.Text="已添加："+source.Name+"。可继续选择其他来源切换。";
                } catch(Exception error) { ShowError(error); } finally { Busy(false,null); }
            };
        }
        static Control Row(string caption,Control control)
        {
            TableLayoutPanel row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,2,0,2)};
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,94)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            row.Controls.Add(new Label{Text=caption,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0); control.Dock=DockStyle.Fill; row.Controls.Add(control,1,0); return row;
        }
        Source ReadSettings(bool validate)
        {
            Source selected=(Source)sources.SelectedItem;
            settings.SelectedSource=selected.Id; settings.Labels=labels.Checked; settings.Replace=replace.Checked; settings.SetKey(key.Text);
            settings.CustomName=customName.Text.Trim(); settings.CustomUrl=customUrl.Text.Trim(); settings.CustomAttribution=customCredits.Text.Trim(); settings.CustomMaxZoom=(int)customZoom.Value;
            settings.CacheLimitMb=(int)cacheLimit.Value;
            Source actual=validate?Source.Find(settings,selected.Id):Source.Catalog(settings).First(x=>x.Id==selected.Id);
            settings.Save(Settings.FileName); return actual;
        }
        void Probe(Source source)
        {
            GatewayClient.Ensure(settings);
            string url=GatewayClient.BaseUrl(settings)+"/wms/"+source.Id+"/"+(settings.Labels?"1":"0")+"?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=basemap&STYLES=&SRS=EPSG:3857&BBOX=0,0,20037508.342789244,20037508.342789244&WIDTH=256&HEIGHT=256&FORMAT=image/png";
            HttpWebRequest request=(HttpWebRequest)WebRequest.Create(url); request.Proxy=null; request.Timeout=60000; request.ReadWriteTimeout=60000;
            try { using(WebResponse response=request.GetResponse()) using(Stream input=response.GetResponseStream()) using(Image image=Image.FromStream(input)) { if(image.Width<1) throw new InvalidDataException(); } }
            catch { throw new IOException("连接测试失败。请检查网络、地图地址或 Key；服务也可能暂时不可用。"); }
        }
        void Busy(bool busy,string message) { sources.Enabled=!busy; test.Enabled=!busy; save.Enabled=!busy; add.Enabled=!busy&&application!=null; key.Enabled=!busy&&((Source)sources.SelectedItem).Id=="tianditu"; customName.Enabled=customUrl.Enabled=customCredits.Enabled=customZoom.Enabled=!busy; UseWaitCursor=busy; if(message!=null) status.Text=message; }
        void ShowError(Exception error) { status.Text=error.Message; MessageBox.Show(this,error.Message,"地图源",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
    }
}
