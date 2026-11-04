using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

internal sealed class ProfileStore
{
    private static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synapse","NasExplorer");
    private sealed record Saved(ConnectionProfile Profile,string? Secret);
    public static ConnectionProfile Load()
    {
        string path=Path.Combine(Folder,"connection.json");
        if(!File.Exists(path))return new();
        var saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))??throw new InvalidDataException("연결 설정이 비어 있습니다.");
        string password="";
        if(saved.Secret is not null)
            password=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.Secret),null,DataProtectionScope.CurrentUser));
        return saved.Profile with{Password=password};
    }
    public static void Save(ConnectionProfile profile,bool remember)
    {
        Directory.CreateDirectory(Folder);
        string? secret=remember?Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(profile.Password),null,DataProtectionScope.CurrentUser)):null;
        File.WriteAllText(Path.Combine(Folder,"connection.json"),JsonSerializer.Serialize(new Saved(profile with{Password=""},secret),new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void AppendLog(string text)
    {
        Directory.CreateDirectory(Folder);
        string path=Path.Combine(Folder,"explorer-"+DateTime.Now.ToString("yyyyMMdd")+".log");
        File.AppendAllText(path,text+Environment.NewLine);
    }
    public static ViewPreferences LoadView()
    {
        string path=Path.Combine(Folder,"view.json");
        return File.Exists(path)?JsonSerializer.Deserialize<ViewPreferences>(File.ReadAllText(path))??new():new();
    }
    public static void SaveView(ViewPreferences view)
    {Directory.CreateDirectory(Folder);File.WriteAllText(Path.Combine(Folder,"view.json"),JsonSerializer.Serialize(view,new JsonSerializerOptions{WriteIndented=true}));}
}

internal sealed record SavedFavorite(string Source,string Label,SearchFilter Filter,BrowseLevel Level,string[] Databases);
internal sealed record ViewPreferences
{
    public double Width {get;set;}=1500;
    public double Height {get;set;}=940;
    public Dictionary<string,double> ColumnWidths {get;set;}=new();
    public List<SavedFavorite> Favorites {get;set;}=[];
}

internal static class BitmapDecoderService
{
    public static BitmapSource Decode(ImagePayload data)
    {
        byte[] b=ImageBytes.Normalize(data.Bytes,data.Width,data.Height,data.Channels);
        long rawLength=(long)data.Width*data.Height*data.Channels;
        bool raw=data.Width>0&&data.Height>0&&rawLength==b.Length;
        if(!raw&&ImageBytes.Extension(b)!=".raw")
        {
            using var memory=new MemoryStream(b,false);
            var decoder=BitmapDecoder.Create(memory,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var frame=decoder.Frames[0];
            if((long)frame.PixelWidth*frame.PixelHeight>64_000_000) throw new InvalidDataException("이미지 크기가 6,400만 픽셀을 초과합니다.");
            frame.Freeze();return frame;
        }
        if(data.Width<=0||data.Height<=0) throw new InvalidDataException("raw 이미지 가로·세로가 미기록입니다. 원본 바이트와 메타데이터를 저장할 수 있습니다.");
        if(rawLength!=b.Length) throw new InvalidDataException("이미지 크기·채널과 실제 바이트 수가 다릅니다.");
        PixelFormat format=data.Channels switch{1=>PixelFormats.Gray8,3=>PixelFormats.Rgb24,_=>throw new InvalidDataException("지원하지 않는 raw 이미지 채널입니다. 원본 바이트를 저장할 수 있습니다.")};
        var source=BitmapSource.Create(data.Width,data.Height,96,96,format,null,b,checked(data.Width*data.Channels));
        source.Freeze();return source;
    }
}

internal sealed class DemoRepository : IExplorerRepository
{
    public Task<IReadOnlyList<DatabaseInfo>> DiscoverAsync(ConnectionProfile p,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DatabaseInfo>>(new[]{new DatabaseInfo("예제_DB","예제",true)});
    public Task<QueryPage> QueryAsync(ConnectionProfile p,string db,BrowseLevel level,SearchFilter f,int size,string? cursor,CancellationToken ct)
    {
        var rows=new List<ResultRow>();
        int count=level switch{BrowseLevel.Models=>3,BrowseLevel.Lots=>12,BrowseLevel.Layers=>2,BrowseLevel.Inspections=>18,_=>72};
        for(int i=1;i<=count;i++)
        {
            ct.ThrowIfCancellationRequested();
            var row=Row(db,i); var v=row.Values;
            if(level==BrowseLevel.Models) v=new(){["model_name"]=$"PI-DEMO-{i:D2}"};
            if(level==BrowseLevel.Lots) v=new(){["lot_id"]=i,["model_name"]=f.Model.Length>0?f.Model:"PI-DEMO-01",["lot_id_name"]=$"LOT-20261104-{i:D3}",["lot_time"]=new DateTime(2026,11,4,8,0,0).AddMinutes(i),["lot_status"]=i%3==0?"ACTIVE":"VMS_COMPLETED"};
            if(level==BrowseLevel.Layers) v=new(){["layer"]=i,["model_name"]="PI-DEMO-01",["lot_id_name"]="LOT-20261104-001",["layer_name"]=$"L{i}",["vms_status"]="VMS_COMPLETED",["retest_count"]=0};
            if(level==BrowseLevel.Inspections) v=new(){["inspection"]=i,["model_name"]="PI-DEMO-01",["lot_id_name"]="LOT-20261104-001",["layer_name"]="L1",["machine_id"]="EQ09",["pc_no"]=(i-1)%3+1,["panel_no"]=(i-1)/3+1,["inspection_time"]=new DateTime(2026,11,4,9,15,0).AddSeconds(i),["inspection_status"]="VMS_COMPLETED",["handler_tact_sec"]=18.4+i/10.0,["tact_ms"]=13500};
            if(f.Model.Length>0)v["model_name"]=f.Model;if(f.Lot.Length>0)v["lot_id_name"]=f.Lot;if(f.Layer.Length>0)v["layer_name"]=f.Layer;
            if(f.Text.Length>0&&!string.Join(" ",v.Values).Contains(f.Text,StringComparison.OrdinalIgnoreCase))continue;
            if(f.Panel.HasValue&&Convert.ToInt32(v.GetValueOrDefault("panel_no",-1))!=f.Panel)continue;
            rows.Add(new(){Database=db,Values=v});
        }
        return Task.FromResult(new QueryPage(rows,false,null));
    }
    private static ResultRow Row(string db,int i)=>new(){Database=db,Values=new()
    {
        ["defects"]=i,["inspection"]=(i-1)/12+1,["model_name"]="PI-DEMO-01",["lot_id_name"]="LOT-20261104-001",["layer_name"]="L1",["machine_id"]="EQ09",["pc_no"]=(i-1)%3+1,["panel_no"]=(i-1)/12+1,
        ["inspection_time"]=new DateTime(2026,11,4,9,15,0).AddSeconds(i),["inspection_status"]="VMS_COMPLETED",["unit_idx"]=i,["unit_x"]=i%8+1,["unit_y"]=i%12+1,["defect_type"]=i%3+1,["vms_state"]=i%3,["vms_code"]=i%4,["ai_code"]=0,
        ["cause_code"]=i%2==0?4001:null,["cause_key"]=i%2==0?"PINHOLE_MIN_SIZE":null,["defect_size"]=i%2==0?2.35+i/10.0:null,["defect_extra"]=0.52,["image_size"]=16384,["image_width"]=128,["image_height"]=128,["image_channels"]=1,["has_image"]=true,
        ["raw_x"]=10.25+i,["raw_y"]=20.5+i,["cad_x"]=80+i,["cad_y"]=160+i,["d_attr"]=new double[]{2.4,0.5,13.0},["source_metadata_version"]=1,["source_block_idx"]=1,["source_axis_x"]=i%8+1,["source_axis_y"]=i%12+1
    }};
    public Task<ResultRow> DetailAsync(ConnectionProfile p,RowKey key,CancellationToken ct)
    {
        var row=Row(key.Database,int.TryParse(key.Id,out var n)?n:1);
        if(key.Table=="inspection") row.Values["fov_count"]=12;
        return Task.FromResult(row);
    }
    public Task<ImagePayload?> ImageAsync(ConnectionProfile p,RowKey key,CancellationToken ct)
    {
        byte[] bytes=new byte[128*128];
        for(int y=0;y<128;y++) for(int x=0;x<128;x++)
        { double dx=x-67,dy=y-63; bytes[y*128+x]=(byte)(Math.Abs(dx)<6&&Math.Abs(dy)<14?36:((x/16+y/16)%2==0?177:200)); }
        return Task.FromResult<ImagePayload?>(new(bytes,128,128,1,key.Database,key.Id));
    }
    public Task<ModelGeometry> GeometryAsync(ConnectionProfile p,string model,CancellationToken ct)=>Task.FromResult(new ModelGeometry(2.2,0,0,256,384,new double[]{0},new double[]{0},256,384,Enumerable.Range(0,8).Select(x=>(double)x*32).ToArray(),Enumerable.Range(0,12).Select(x=>(double)x*32).ToArray(),32,32,256,384));
    public Task StreamInspectionAsync(ConnectionProfile p,string database,string inspectionId,Func<long,IAsyncEnumerable<ResultRow>,CancellationToken,Task> consume,CancellationToken ct)
    {var rows=Enumerable.Range(1,12).Select(i=>Row(database,i)).ToArray();return consume(rows.Length,Aoi.NasExplorer.Export.ExportService.Enumerate(rows),ct);}
}
