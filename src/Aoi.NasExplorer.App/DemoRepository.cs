using System.Runtime.CompilerServices;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

// Names come from the recovered 2026-10-27..11-03 catalog. Counts, coordinates and images are practice fixtures.
internal sealed class DemoRepository : IExplorerRepository
{
    private readonly Dictionary<BrowseLevel,List<ResultRow>> _data=Enum.GetValues<BrowseLevel>().ToDictionary(x=>x,_=>new List<ResultRow>());
    private const string Db="예제_DB";
    private const string Model="p2411-004d101";
    internal const string FirstLot="agsd";
    public DemoRepository()
    {
        string[] lots={FirstLot,"ASBA","ASBADF","DB_TEST_260511","T5SG","T5SG2","TEARS","TEARSABV","TEARSABVl2","TESAAGB","TESAAGBB","TESTAB","dsag","testafdb","ATMAET202TEST"};
        foreach(var name in new[]{Model,"p2412-022d601"})_data[BrowseLevel.Models].Add(new(){Database=Db,Values=new(){["model_name"]=name}});
        int layerId=0,inspectionId=0,defectId=0;
        for(int k=0;k<lots.Length;k++)
        {
            string model=k==14?"p2412-022d601":Model,lot=lots[k];
            _data[BrowseLevel.Lots].Add(new(){Database=Db,Values=new(){["lot_id"]=k+1,["model_name"]=model,["lot_id_name"]=lot,["lot_time"]=new DateTime(2026,11,3,8,0,0),["lot_status"]="ACTIVE"}});
            string[] layers=k==14?new[]{"L1-T-SHORT3.1-0200","L2-B-SHORT3.1-0200"}:new[]{k is 2 or 8 or 12 or 13?"L2-B-SHORT3.1-0200":"L1-T-SHORT3.1-0200"};
            foreach(string layer in layers)
            {
                layerId++;
                _data[BrowseLevel.Layers].Add(new(){Database=Db,Values=new(){["layer"]=layerId,["lot_id"]=k+1,["model_name"]=model,["lot_id_name"]=lot,["layer_name"]=layer,["vms_status"]="VMS_COMPLETED",["retest_count"]=0}});
                for(int panel=1;panel<=6;panel++)
                {
                    inspectionId++;var time=new DateTime(2026,11,3,9,15,0).AddSeconds(inspectionId);
                    var values=new Dictionary<string,object?>{["inspection"]=inspectionId,["lot_id"]=k+1,["layer_id"]=layerId,["model_name"]=model,["lot_id_name"]=lot,["layer_name"]=layer,["machine_id"]="EQ_09",["pc_no"]=0,["panel_no"]=panel,["inspection_time"]=time,["inspection_status"]="VMS_COMPLETED",["handler_tact_sec"]=18.4,["tact_ms"]=13500,["fov_count"]=12,["total_defects"]=12,["stored_defect_count"]=12};
                    _data[BrowseLevel.Inspections].Add(new(){Database=Db,Values=values});
                    for(int j=1;j<=12;j++)
                    {
                        defectId++;var d=new Dictionary<string,object?>(values){["defects"]=defectId,["unit_idx"]=j,["unit_x"]=j%8+1,["unit_y"]=j%12+1,["defect_type"]=j%3+1,["vms_state"]=j%3,["vms_code"]=j%4,["ai_code"]=0,["cause_code"]=j%2==0?4001:null,["cause_key"]=j%2==0?"PINHOLE_MIN_SIZE":null,["defect_size"]=j%2==0?2.35+j/10.0:null,["defect_extra"]=0.52,["image_size"]=16384,["image_width"]=128,["image_height"]=128,["image_channels"]=1,["has_image"]=true,["raw_x"]=10.25+j,["raw_y"]=20.5+j,["cad_x"]=80+j,["cad_y"]=160+j,["d_attr"]=new double[]{2.4,0.5,13},["source_metadata_version"]=1,["source_block_idx"]=1,["source_axis_x"]=j%8+1,["source_axis_y"]=j%12+1,["source_result_idx"]=1,["source_file_name"]=$"{lot}_{panel}_{layer}_{j}_1_{j%8+1}_{j%12+1}_1"};
                        _data[BrowseLevel.Defects].Add(new(){Database=Db,Values=d});
                    }
                }
            }
        }
    }
    public Task<IReadOnlyList<DatabaseInfo>> DiscoverAsync(ConnectionProfile p,CancellationToken ct)=>Task.FromResult<IReadOnlyList<DatabaseInfo>>(new[]{new DatabaseInfo(Db,"회수 명칭 · 연습용 데이터",true)});
    private IEnumerable<ResultRow> Filter(BrowseLevel level,SearchFilter f)
    {
        IEnumerable<ResultRow> rows=_data[level];
        bool Match(ResultRow row)
        {
            if(f.Text.Length>0&&!(f.Target switch{SearchTarget.Lot=>row.Text("lot_id_name"),SearchTarget.Model=>row.Text("model_name"),_=>row.Text("model_name")+" "+row.Text("lot_id_name")}).Contains(f.Text,StringComparison.OrdinalIgnoreCase))return false;
            if(f.Model.Length>0&&row.Text("model_name")!=f.Model||f.Lot.Length>0&&row.Text("lot_id_name")!=f.Lot||f.Layer.Length>0&&row.Text("layer_name")!=f.Layer)return false;
            if(f.LotId.Length>0&&row.Text("lot_id")!=f.LotId||f.LayerId.Length>0&&row.Text(level==BrowseLevel.Layers?"layer":"layer_id")!=f.LayerId||f.InspectionId.Length>0&&row.Text("inspection")!=f.InspectionId)return false;
            if(f.Panel.HasValue&&row.Long("panel_no")!=f.Panel||f.Pc.HasValue&&row.Long("pc_no")!=f.Pc)return false;
            if(f.Machine.Length>0&&!(f.MachineExact?row.Text("machine_id")==f.Machine:row.Text("machine_id").Contains(f.Machine,StringComparison.OrdinalIgnoreCase)))return false;
            if(f.From.HasValue&&(row["inspection_time"] is not DateTime start||start<f.From)||f.UntilExclusive.HasValue&&(row["inspection_time"] is not DateTime end||end>=f.UntilExclusive))return false;
            if(level==BrowseLevel.Defects)
            {
                if(f.DefectCode.HasValue&&row.Long("defect_type")!=f.DefectCode||f.VmsCode.HasValue&&row.Long("vms_code")!=f.VmsCode||f.VmsState.HasValue&&row.Long("vms_state")!=f.VmsState||f.CauseCode.HasValue&&row.Long("cause_code")!=f.CauseCode||f.AiCode.HasValue&&row.Long("ai_code")!=f.AiCode)return false;
                if(f.CauseText.Length>0&&!row.Text("cause_key").Contains(f.CauseText,StringComparison.OrdinalIgnoreCase)||f.Images==ImageFilter.Missing)return false;
                if(f.MinimumSize.HasValue&&!(row.Number("defect_size")>=f.MinimumSize)||f.MaximumSize.HasValue&&!(row.Number("defect_size")<=f.MaximumSize))return false;
            }
            return true;
        }
        // Higher folders with defect predicates must represent matching descendants.
        bool hasDefect=f.DefectCode.HasValue||f.VmsCode.HasValue||f.VmsState.HasValue||f.CauseCode.HasValue||f.AiCode.HasValue||f.CauseText.Length>0||f.MinimumSize.HasValue||f.MaximumSize.HasValue||f.Images!=ImageFilter.All;
        if(level<BrowseLevel.Defects&&(hasDefect||f.InspectionId.Length>0||level<BrowseLevel.Inspections&&(f.Panel.HasValue||f.Machine.Length>0||f.From.HasValue||f.UntilExclusive.HasValue)||level<BrowseLevel.Layers&&(f.Layer.Length>0||f.LayerId.Length>0)||level==BrowseLevel.Models&&(f.Lot.Length>0||f.LotId.Length>0)))
        {
            var matching=Filter(BrowseLevel.Defects,f).ToArray();
            string key=level switch{BrowseLevel.Models=>"model_name",BrowseLevel.Lots=>"lot_id",BrowseLevel.Layers=>"layer_id",_=>"inspection"};
            var keys=matching.Select(x=>x.Text(key)).ToHashSet();return rows.Where(x=>keys.Contains(x.Text(level==BrowseLevel.Layers?"layer":key)));
        }
        return rows.Where(Match);
    }
    public Task<QueryPage> QueryAsync(ConnectionProfile p,string db,BrowseLevel level,SearchFilter f,int size,string? cursor,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();f.Validate();string key=level switch{BrowseLevel.Models=>"model_name",BrowseLevel.Lots=>"lot_id",BrowseLevel.Layers=>"layer",BrowseLevel.Inspections=>"inspection",_=>"defects"};
        var rows=Filter(level,f);rows=level==BrowseLevel.Models?rows.OrderBy(x=>x.Text(key),StringComparer.Ordinal):rows.OrderByDescending(x=>x.Long(key));
        if(cursor is not null)rows=rows.Where(x=>level==BrowseLevel.Models?string.CompareOrdinal(x.Text(key),cursor)>0:x.Long(key)<long.Parse(cursor));
        var page=rows.Take(size+1).Select(x=>new ResultRow{Database=db,Values=new(x.Values)}).ToList();bool more=page.Count>size;if(more)page.RemoveAt(page.Count-1);
        return Task.FromResult(new QueryPage(page,more,more?page[^1].Text(key):null));
    }
    public Task<ResultRow> DetailAsync(ConnectionProfile p,RowKey key,CancellationToken ct)
    {
        var level=key.Table switch{"lot_id"=>BrowseLevel.Lots,"layer"=>BrowseLevel.Layers,"inspection"=>BrowseLevel.Inspections,_=>BrowseLevel.Defects};
        var row=_data[level].Single(x=>x.Text(key.Table)==key.Id);return Task.FromResult(new ResultRow{Database=key.Database,Values=new(row.Values)});
    }
    public Task<ImagePayload?> ImageAsync(ConnectionProfile p,RowKey key,CancellationToken ct)
    {
        byte[] bytes=new byte[128*128];for(int y=0;y<128;y++)for(int x=0;x<128;x++)bytes[y*128+x]=(byte)(Math.Abs(x-67)<6&&Math.Abs(y-63)<14?36:(x/16+y/16)%2==0?177:200);
        return Task.FromResult<ImagePayload?>(new(bytes,128,128,1,key.Database,key.Id));
    }
    public Task<ModelGeometry> GeometryAsync(ConnectionProfile p,string model,string layer,CancellationToken ct)=>Task.FromResult(new ModelGeometry(2,0,0,256,384,new double[]{0},new double[]{0},256,384,Enumerable.Range(0,8).Select(x=>(double)x*32).ToArray(),Enumerable.Range(0,12).Select(x=>(double)x*32).ToArray(),32,32,256,384));
    public Task WithSnapshotAsync(ConnectionProfile p,string db,Func<IExplorerRepository,DateTimeOffset,CancellationToken,Task> consume,CancellationToken ct)=>consume(this,DateTimeOffset.Now,ct);
    public Task StreamInspectionAsync(ConnectionProfile p,string db,string id,Func<long,IAsyncEnumerable<ResultRow>,CancellationToken,Task> consume,CancellationToken ct)
    {var rows=Filter(BrowseLevel.Defects,new(){InspectionId=id}).ToArray();return consume(rows.LongLength,Aoi.NasExplorer.Export.ExportService.Enumerate(rows),ct);}
    public async IAsyncEnumerable<ResultRow> StreamAsync(ConnectionProfile p,string db,BrowseLevel level,SearchFilter f,bool images,Action<DateTimeOffset>? captured,[EnumeratorCancellation]CancellationToken ct,IReadOnlySet<string>? selectedIds=null)
    {
        captured?.Invoke(DateTimeOffset.Now);string? cursor=null;bool more;
        do{var page=await QueryAsync(p,db,level,f,500,cursor,ct);foreach(var row in page.Rows){ct.ThrowIfCancellationRequested();if(selectedIds is not null&&!selectedIds.Contains(row.Key(p.Identity,level).Id))continue;if(images)row.SnapshotImage=await ImageAsync(p,row.Key(p.Identity,level),ct);yield return row;}cursor=page.Cursor;more=page.HasMore;}while(more);
    }
    public Task<ScopeSummary> SummaryAsync(ConnectionProfile p,string db,SearchFilter f,CancellationToken ct)
    {
        var ds=Filter(BrowseLevel.Defects,f).ToArray();var ins=Filter(BrowseLevel.Inspections,f).ToArray();var ls=Filter(BrowseLevel.Lots,f).ToArray();
        var panels=ins.GroupBy(x=>(Lot:x.Text("lot_id"),Layer:x.Text("layer_id"),Panel:x.Text("panel_no"),Machine:x.Text("machine_id"))).Select(g=>
        {var ids=g.Select(x=>x.Text("inspection")).ToHashSet();var dd=ds.Where(x=>ids.Contains(x.Text("inspection"))).ToArray();return new ResultRow{Database=db,Values=new(g.First().Values){["inspection_count"]=g.LongCount(),["defect_count"]=dd.LongLength,["image_count"]=dd.LongLength,["vms_ng_count"]=dd.LongCount(x=>x.Long("vms_state")==1),["vms_pass_count"]=dd.LongCount(x=>x.Long("vms_state")==2)}};}).ToArray();
        return Task.FromResult(new ScopeSummary(ls.LongLength,ins.LongLength,ds.LongLength,panels.LongLength,panels){LotCounts=ls.Select(x=>new ResultRow{Database=db,Values=new(x.Values){["inspection_count"]=ins.LongCount(i=>i.Text("lot_id")==x.Text("lot_id")),["defect_count"]=ds.LongCount(d=>d.Text("lot_id")==x.Text("lot_id"))}}).ToArray()});
    }
}
