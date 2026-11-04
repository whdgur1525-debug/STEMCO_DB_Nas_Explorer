using System.Text;
using System.Text.Json;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.Export;

public sealed record BundleOptions(bool Ert,bool Images,bool Counts,bool DefectCsv,bool ModelLayerLot=false);
public sealed record BundleScope(string Database,SearchFilter Filter,BrowseLevel Level=BrowseLevel.Inspections,bool ExpandQuery=false);
public sealed record BundleResult(string Status,long Panels,long Defects,long ErtFiles,long Images,long MissingImages,long Failures);

public static class InspectionBundleExporter
{
    public static string Component(string name)
    {
        if(string.IsNullOrWhiteSpace(name))return "미기록";
        var invalid=Path.GetInvalidFileNameChars();
        bool reserved=System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0],"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return name.Length<=100&&!reserved&&name is not ("." or "..")&&!name.EndsWith('.')&&!name.EndsWith(' ')&&!name.Any(c=>invalid.Contains(c)||char.IsControl(c))?name:ExportService.SafeComponent(name);
    }
    public static string LayerFolder(string root,ResultRow inspection,bool modelLayerLot)
    {
        string[] names=modelLayerLot?new[]{"machine_id","model_name","layer_name","lot_id_name"}:new[]{"machine_id","model_name","lot_id_name","layer_name"};
        return names.Aggregate(root,(path,key)=>Path.Combine(path,Component(inspection.Text(key))));
    }
    public static SearchFilter WholePanel(SearchFilter filter)=>filter with
    {DefectCode=null,VmsCode=null,VmsState=null,AiCode=null,CauseCode=null,CauseText="",MinimumSize=null,MaximumSize=null,Images=ImageFilter.All,Pc=null};
    public static SearchFilter SelectionFilter(SearchFilter filter,BrowseLevel level,ResultRow row)
    {
        if(level==BrowseLevel.Models&&row.Text("model_name").Length==0)throw new InvalidDataException("모델명이 미기록된 항목입니다. LOT 또는 패널을 선택하세요.");
        string identity=level switch{BrowseLevel.Models=>row.Text("model_name"),BrowseLevel.Lots=>row.Text("lot_id"),BrowseLevel.Layers=>row.Text("layer"),_=>row.Text("inspection")};
        if(identity.Length==0)throw new InvalidDataException("선택한 항목의 원본 Index가 없습니다. 다시 조회하세요.");
        var f=new SearchFilter{Target=filter.Target,Model=row.Text("model_name")};
        if(level==BrowseLevel.Models)return f with{Panel=null};
        f=f with{Lot=row.Text("lot_id_name"),LotId=row.Text("lot_id")};
        if(level==BrowseLevel.Lots)return f;
        f=f with{Layer=row.Text("layer_name"),LayerId=row.Text(level==BrowseLevel.Layers?"layer":"layer_id")};
        if(level==BrowseLevel.Layers)return f;
        return f with{InspectionId=row.Text("inspection"),Panel=null,Machine="",MachineExact=false,LatestOnly=false};
    }
    private static async IAsyncEnumerable<SearchFilter> ExpandAsync(IExplorerRepository snapshot,ConnectionProfile profile,BundleScope scope,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
    {
        if(!scope.ExpandQuery){yield return scope.Filter;yield break;}
        BrowseLevel level=scope.Level==BrowseLevel.Defects?BrowseLevel.Inspections:scope.Level;
        string? cursor=null;bool more;
        do
        {
            var page=await snapshot.QueryAsync(profile,scope.Database,level,scope.Filter,500,cursor,ct);
            foreach(var row in page.Rows)yield return SelectionFilter(scope.Filter,level,row);
            cursor=page.Cursor;more=page.HasMore;
        }while(more);
    }

    public static async Task<BundleResult> ExportAsync(IExplorerRepository repository,ConnectionProfile profile,string root,
        IReadOnlyList<BundleScope> scopes,BundleOptions options,
        Func<ResultRow,ImagePayload,string,CancellationToken,Task<string>> saveImage,Action<string>? progress,CancellationToken ct)
    {
        if(!options.Ert&&!options.Images&&!options.Counts&&!options.DefectCsv)throw new ArgumentException("저장할 항목을 하나 이상 선택하세요.");
        Directory.CreateDirectory(root);
        await ExportService.WriteNewAsync(Path.Combine(root,"내보내기조건.json"),JsonSerializer.Serialize(new{options,scopes,startedAt=DateTimeOffset.Now,scope="선택한 모델/LOT/레이어/패널의 전체 검사 불량. 불량 검색조건과 PC 제한을 적용하지 않음. 검사 이력 보존."},new JsonSerializerOptions{WriteIndented=true}),ct);
        long panels=0,defects=0,erts=0,images=0,missing=0,failures=0;string status="completed";
        var seen=new HashSet<(string Database,string Inspection)>();var destinations=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots=new List<object>();var geometry=new Dictionary<string,ModelGeometry>(StringComparer.Ordinal);var geometryErrors=new Dictionary<string,string>(StringComparer.Ordinal);
        await using var log=new StreamWriter(Path.Combine(root,"처리내역.jsonl"),false,new UTF8Encoding(true)){AutoFlush=true};
        await using var counts=options.Counts?new StreamWriter(Path.Combine(root,"패널별불량수.csv"),false,new UTF8Encoding(true)):null;
        await using var bars=options.Counts?new StreamWriter(Path.Combine(root,"바별검출수.csv"),false,new UTF8Encoding(true)):null;
        if(counts is not null)await counts.WriteLineAsync("\"출처 DB\",\"검사 Index\",\"Model\",\"LotID\",\"Layer\",\"패널 번호\",\"검사 시각\",\"검사 상태\",\"DB 불량 전체 건수\",\"DB 기록 total_defects\",\"이미지 저장\",\"이미지 없음\",\"ERT 저장\"");
        if(bars is not null)await bars.WriteLineAsync("\"출처 DB\",\"검사 Index\",\"Model\",\"LotID\",\"Layer\",\"패널 번호\",\"저장된 바 번호\",\"검출 수\"");
        async Task Failure(ResultRow? row,string artifact,Exception error)
        {
            failures++;progress?.Invoke($"{artifact} 실패: {error.Message}");
            await log.WriteLineAsync(JsonSerializer.Serialize(new{database=row?.Database,inspection=row?.Text("inspection"),artifact,status="failed",error=error.Message}));
        }
        try
        {
            foreach(var dbGroup in scopes.GroupBy(x=>x.Database))
            {
                await repository.WithSnapshotAsync(profile,dbGroup.Key,async(snapshot,captured,token)=>
                {
                    snapshots.Add(new{database=dbGroup.Key,capturedAt=captured});
                    foreach(var scope in dbGroup)
                    {
                        await foreach(var expanded in ExpandAsync(snapshot,profile,scope,token))
                        {
                        var filter=WholePanel(expanded);string? cursor=null;bool more;
                        do
                        {
                            var page=await snapshot.QueryAsync(profile,scope.Database,BrowseLevel.Inspections,filter,500,cursor,token);
                            foreach(var inspection in page.Rows)
                            {
                                token.ThrowIfCancellationRequested();string id=inspection.Text("inspection");if(!seen.Add((inspection.Database,id)))continue;
                                string folder=LayerFolder(root,inspection,options.ModelLayerLot);string number=inspection.Long("panel_no") is long n?n.ToString("D3"):"패널미기록";
                                string panelName=inspection.Long("panel_no") is long panel?panel.ToString(System.Globalization.CultureInfo.InvariantCulture):"패널미기록";
                                string primary=Path.Combine(folder,panelName);
                                if(!destinations.Add(primary))folder=Path.Combine(folder,"_검사이력",Component(inspection.Database),"검사_"+Component(id));
                                Directory.CreateDirectory(folder);string panelFolder=Path.Combine(folder,panelName);
                                long expected=0,seenRows=0,panelImages=0,panelMissing=0;bool ertSaved=false;
                                var barCounts=new Dictionary<string,long>(StringComparer.Ordinal);
                                try
                                {
                                    var detail=await snapshot.DetailAsync(profile,inspection.Key(profile.Identity,BrowseLevel.Inspections),token);
                                    await ExportService.WriteNewAsync(Path.Combine(folder,number+".inspection.json"),ExportService.Json(new(){Database=inspection.Database,Values=new(inspection.Values){["snapshot_at"]=captured,["inspection_fields"]=detail.Values}}),token);
                                    ModelGeometry? model=null;
                                    if(options.Ert)
                                    {
                                        string name=inspection.Text("model_name"),layer=inspection.Text("layer_name"),modelKey=name+"_Layer"+layer;
                                        if(!geometry.TryGetValue(modelKey,out model)&&!geometryErrors.ContainsKey(modelKey))
                                        {
                                            try{model=await repository.GeometryAsync(profile,name,layer,token);geometry[modelKey]=model;}
                                            catch(Exception ex) when(ex is not OperationCanceledException){geometryErrors[modelKey]=ex.Message;}
                                        }
                                        if(model is null)await Failure(inspection,"ERT",new InvalidDataException("모델 DB 자료가 필요합니다: "+geometryErrors[modelKey]));
                                    }
                                    async IAsyncEnumerable<ResultRow> CountRows(IAsyncEnumerable<ResultRow> source)
                                    {
                                        await foreach(var row in source.WithCancellation(token))
                                        {
                                            seenRows++;string bar=row["source_block_idx"] is null?"미기록":row.Text("source_block_idx");barCounts[bar]=barCounts.GetValueOrDefault(bar)+1;
                                            yield return row;
                                        }
                                    }
                                    await snapshot.StreamInspectionAsync(profile,inspection.Database,id,async(count,source,readToken)=>
                                    {
                                        expected=count;
                                        if(options.Ert&&model is not null)
                                        {
                                            try
                                            {
                                                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                                                string ertPath=Path.Combine(folder,number+".ERT");
                                                var document=await ErtExporter.WriteStreamingAsync(ertPath,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),inspection.Long("panel_no") is long panelNumber?checked((int)panelNumber):throw new InvalidDataException("패널 번호 미기록"),inspection["inspection_time"] is DateTime time?time:throw new InvalidDataException("검사 시각 미기록"),inspection.Text("inspection_status"),(int?)detail.Long("fov_count"),model,count,CountRows(source),Encoding.GetEncoding(949),readToken);
                                                await ExportService.WriteNewAsync(ertPath+".source.json",JsonSerializer.Serialize(new{inspection.Database,inspection=id,capturedAt=captured,modelKey=inspection.Text("model_name")+"_Layer"+inspection.Text("layer_name"),modelDatabase=profile.ModelDatabase,modelConsistency="모델 DB 별도 스냅샷 / 검사 당시 모델 보장 아님",document.Rows,document.Notes},new JsonSerializerOptions{WriteIndented=true}),readToken);
                                                ertSaved=true;erts++;
                                            }
                                            catch(Exception ex) when(ex is not OperationCanceledException){await Failure(inspection,"ERT",ex);}
                                        }
                                        else if(options.Counts)await foreach(var row in CountRows(source)){}
                                    },token);
                                    if(options.DefectCsv)
                                    {
                                        await snapshot.StreamInspectionAsync(profile,inspection.Database,id,async(_,source,readToken)=>
                                        {await ExportService.CsvAsync(Path.Combine(panelFolder,"불량목록.csv"),source,readToken);},token);
                                    }
                                    if(options.Counts&&seenRows!=expected)
                                    {
                                        // A failed ERT writer may have consumed only part of its stream.
                                        barCounts.Clear();seenRows=0;
                                        await snapshot.StreamInspectionAsync(profile,inspection.Database,id,async(_,source,_)=>{await foreach(var row in CountRows(source)){}},token);
                                    }
                                    if(options.Images)
                                    {
                                        string? defectCursor=null;bool moreDefects;
                                        do
                                        {
                                            var rows=await snapshot.QueryAsync(profile,inspection.Database,BrowseLevel.Defects,new(){InspectionId=id},250,defectCursor,token);
                                            foreach(var row in rows.Rows)
                                            {
                                                token.ThrowIfCancellationRequested();
                                                try
                                                {
                                                    var image=await snapshot.ImageAsync(profile,row.Key(profile.Identity,BrowseLevel.Defects),token);
                                                    if(image is null||image.Bytes.Length==0){missing++;panelMissing++;await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,defect=row.Text("defects"),artifact="image",status="not_recorded",reason="DB 이미지 미기록 또는 0바이트"}));continue;}
                                                    string path=await saveImage(row,image,panelFolder,token);images++;panelImages++;
                                                    await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,defect=row.Text("defects"),artifact="image",status="saved",path}));
                                                }
                                                catch(Exception ex) when(ex is not OperationCanceledException){await Failure(row,"이미지",ex);}
                                                if((panelImages+panelMissing)%25==0)progress?.Invoke($"패널 {number} · 이미지 {panelImages:N0}개 저장 / 없음 {panelMissing:N0}");
                                            }
                                            defectCursor=rows.Cursor;moreDefects=rows.HasMore;
                                        }while(moreDefects);
                                    }
                                    if(counts is not null)
                                    {
                                        object?[] fields={inspection.Database,id,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),inspection["panel_no"],inspection["inspection_time"],inspection["inspection_status"],expected,detail["total_defects"],panelImages,panelMissing,ertSaved};
                                        await counts.WriteLineAsync(string.Join(",",fields.Select(ExportService.CsvCell)));
                                        foreach(var bar in barCounts.OrderBy(x=>x.Key))await bars!.WriteLineAsync(string.Join(",",new object?[]{inspection.Database,id,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),inspection["panel_no"],bar.Key,bar.Value}.Select(ExportService.CsvCell)));
                                        await counts.FlushAsync(token);await bars!.FlushAsync(token);
                                    }
                                    panels++;defects+=expected;
                                    await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,panel=number,folder,defects=expected,ertSaved,images=panelImages,missing=panelMissing,status="processed"}));
                                    progress?.Invoke($"패널 {panels:N0}개 · 불량 {defects:N0}건 · ERT {erts:N0}개 · 이미지 {images:N0}개 · 실패 {failures:N0}건");
                                }
                                catch(Exception ex) when(ex is not OperationCanceledException){await Failure(inspection,"패널",ex);}
                            }
                            cursor=page.Cursor;more=page.HasMore;
                        }while(more);
                        }
                    }
                },ct);
            }
            if(panels==0&&failures==0)status="empty";
        }
        catch(OperationCanceledException){status="cancelled";throw;}
        catch{status="failed";throw;}
        finally
        {
            if(status=="completed"&&failures>0)status="partial_failure";
            await File.WriteAllTextAsync(Path.Combine(root,"내보내기결과.json"),JsonSerializer.Serialize(new{status,panels,defects,ertFiles=erts,images,missingImages=missing,failures,snapshots,endedAt=DateTimeOffset.Now,modelConsistency="모델 DB는 별도 스냅샷. 검사 당시 모델을 보장하지 않음.",history="동일 장비/모델/LOT/Layer/패널의 추가 검사 기록은 _검사이력 하위에 보존"},new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
        }
        return new(status,panels,defects,erts,images,missing,failures);
    }
}
