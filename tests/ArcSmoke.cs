using System;
using System.IO;
using System.Windows.Forms;
using ESRI.ArcGIS;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.Geometry;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Output;
using SamplingBasemaps;
using Path = System.IO.Path;
using System.Drawing;

static class ArcSmoke
{
    [STAThread] static int Main(string[] args)
    {
        try {
            if(!RuntimeManager.Bind(ProductCode.Desktop)) throw new Exception("Cannot bind ArcGIS Desktop");
            return Run(args);
        } catch(Exception error) {Console.WriteLine(error);return 1;}
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        IAoInitialize ao=new AoInitializeClass();
        esriLicenseStatus license=ao.Initialize(esriLicenseProductCode.esriLicenseProductCodeAdvanced);
        Console.WriteLine("License="+license);
        if(license!=esriLicenseStatus.esriLicenseCheckedOut&&license!=esriLicenseStatus.esriLicenseAlreadyInitialized) throw new Exception("License unavailable");
        Settings settings=Settings.Load(args[0]); string output=args[1]; Directory.CreateDirectory(output);
        Source source=Source.Find(settings,"esri_imagery");
        ILayer layer=ArcLayers.Create(settings,source);
        Console.WriteLine("WMS connected, valid="+layer.Valid+" visible="+layer.Visible+" sublayers="+((ICompositeLayer)layer).Count+" childVisible="+((ICompositeLayer)layer).get_Layer(0).Visible);
        if(!layer.Valid||!ArcLayers.Managed(layer)) throw new Exception("Layer invalid / unmarked");
        ILayerFile file=new LayerFileClass();string lyr=Path.Combine(output,"EsriSatellite.lyr");file.New(lyr);file.ReplaceContents(layer);file.Save();file.Close();
        file=new LayerFileClass();file.Open(lyr);if(!ArcLayers.Managed(file.Layer))throw new Exception("Saved layer lost ownership metadata");file.Close();Console.WriteLine("Layer file save/reopen passed");
        IMap map=new MapClass(); map.SpatialReference=new SpatialReferenceEnvironmentClass().CreateGeographicCoordinateSystem(4326);
        IGroupLayer business=new GroupLayerClass();((ILayer)business).Name="Existing business layers";map.AddLayer((ILayer)business);
        IActiveView view=(IActiveView)map;
        using(Form form=new Form()) {
            form.Width=900;form.Height=600;view.Activate(form.Handle.ToInt32());
            IEnvelope extent=new EnvelopeClass();extent.SpatialReference=map.SpatialReference;extent.PutCoords(114.7,32.3,116.7,34.3);view.Extent=extent;
            ArcLayers.AddToMap(map,settings,layer);
            if(map.LayerCount!=2||map.get_Layer(0).Name!="Existing business layers"||map.SpatialReference.FactoryCode!=4326) throw new Exception("Business layer / projection not retained");
            ILayer replacement=ArcLayers.Create(settings,source);ArcLayers.AddToMap(map,settings,replacement);
            if(map.LayerCount!=2||!ArcLayers.Managed(map.get_Layer(1))) throw new Exception("Replacement did not keep exactly one managed basemap");
            Console.WriteLine("Switch retained business layers, order and WGS84 projection");
            IExport export=new ExportPNGClass();export.ExportFileName=Path.Combine(output,"ArcMapExport.png");export.Resolution=96;
            IEnvelope pixels=new EnvelopeClass();pixels.PutCoords(0,0,900,600);export.PixelBounds=pixels;
            tagRECT rect=new tagRECT{left=0,top=0,right=900,bottom=600};
            int hdc=export.StartExporting();view.Output(hdc,96,ref rect,view.Extent,null);export.FinishExporting();export.Cleanup();
            Console.WriteLine("Exported PNG");
            using(Bitmap bitmap=new Bitmap(export.ExportFileName)) {
                int nonwhite=0;for(int y=30;y<bitmap.Height;y+=30)for(int x=30;x<bitmap.Width;x+=30){Color pixel=bitmap.GetPixel(x,y);if(pixel.R<245||pixel.G<245||pixel.B<245)nonwhite++;}
                if(nonwhite<20)throw new Exception("Export is blank: "+nonwhite+" colored samples"); Console.WriteLine("Export contains imagery: "+nonwhite+" colored samples");
            }
            view.Deactivate();
        }
        Console.WriteLine("ARCOBJECTS SMOKE PASSED");return 0;
    }
}
