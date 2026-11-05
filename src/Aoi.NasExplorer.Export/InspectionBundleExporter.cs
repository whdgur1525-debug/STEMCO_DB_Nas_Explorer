using System.Text;
using System.Text.Json;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.Export;

public sealed record BundleOptions(bool Ert,bool Images,bool Counts,bool DefectCsv,bool ModelLayerLot=false,bool HandlerFiles=false,bool IncludeHistory=false);
public sealed record BundleScope(string Database,SearchFilter Filter,BrowseLevel Level=BrowseLevel.Inspections,bool ExpandQuery=false);
public sealed record BundleResult(string Status,long Panels,long Defects,long ErtFiles,long Images,long MissingImages,long Failures,long SidecarFiles=0,long Unrecoverable=0)
{ public string DiagnosticFolder {get;init;}=""; }
public sealed record BundleProgress(string Phase,long PlannedPanels,long ProcessedPanels,long Defects,long Images,long CurrentPanelImages=0,long CurrentPanelExpected=0);
public sealed record BundlePlanSummary(long PlannedPanels,string DiagnosticFolder)
{ public IReadOnlyList<string> Folders {get;init;}=Array.Empty<string>();public long FolderCount {get;init;}public long UnknownIdentityPanels {get;init;} }

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
    public static string? ErtMasterFolder(ResultRow inspection,ResultRow? layerDetail,bool modelLayerLot)
    {
        string? recordedRoot=layerDetail?.Text("vms_aoi_ert_path").Trim().TrimEnd('\\','/');
        if(string.IsNullOrEmpty(recordedRoot)||string.IsNullOrWhiteSpace(inspection.Text("machine_id")))return null;
        string[] keys=modelLayerLot?["machine_id","model_name","layer_name","lot_id_name"]:["machine_id","model_name","lot_id_name","layer_name"];
        return recordedRoot+"\\"+string.Join("\\",keys.Select(inspection.Text));
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

    private sealed record PhysicalPanel(string Machine,string Model,string Lot,string Layer,long Panel);
    private sealed record PanelReference(string Database,string Inspection,PhysicalPanel? Physical=null,DateTime? Time=null,bool Explicit=false);
    private static PanelReference PlanReference(ResultRow row,bool explicitInspection)
    {
        string machine=row.Text("machine_id"),model=row.Text("model_name"),lot=row.Text("lot_id_name"),layer=row.Text("layer_name");
        var physical=machine.Length>0&&model.Length>0&&lot.Length>0&&layer.Length>0&&row.Long("panel_no") is >=0
            ?new PhysicalPanel(machine,model,lot,layer,row.Long("panel_no")!.Value):null;
        DateTime? time=row["inspection_time"] switch{DateTime date=>date,DateTimeOffset date=>date.UtcDateTime,_=>null};
        return new(row.Database,row.Text("inspection"),physical,time,explicitInspection);
    }
    private static bool IsLater(PanelReference candidate,PanelReference current)
    {
        int time=DateTime.Compare(candidate.Time!.Value,current.Time!.Value);if(time!=0)return time>0;
        // Equal timestamps use the source inspection index, as Data Transfer does. Database name is only a deterministic final tie-break.
        if(long.TryParse(candidate.Inspection,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out long next)&&
           long.TryParse(current.Inspection,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out long previous)&&next!=previous)return next>previous;
        int id=string.CompareOrdinal(candidate.Inspection,current.Inspection);return id!=0?id>0:string.CompareOrdinal(candidate.Database,current.Database)>0;
    }
    private sealed record StoredRow(string Database,Dictionary<string,object?> Values,Dictionary<string,string> Types);
    private sealed record StoredImage(long Ordinal,string Defect,string Status,string? Payload,int Width,int Height,int Channels,long Bytes,string? Error);
    private sealed record StagedPanel(ResultRow Inspection,ResultRow Detail,ResultRow? LayerDetail,DateTimeOffset CapturedAt,DateTimeOffset SnapshotEndedAt,
        string Folder,string RowsFile,string? ImagesFile,long Expected,Dictionary<string,long> BarCounts,int[] RecordedPunchUnits,bool UnknownPunchUnit);
    private static readonly JsonSerializerOptions SpoolJson=new(){NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};
    private static string StoredType(object? value)=>value switch
    {
        null or DBNull=>"null",short=>"i16",int=>"i32",long=>"i64",float=>"f32",double=>"f64",decimal=>"decimal",bool=>"bool",string=>"string",Guid=>"guid",DateTime=>"date",DateTimeOffset=>"offset",
        byte[]=>"bytes",short[]=>"i16[]",int[]=>"i32[]",long[]=>"i64[]",float[]=>"f32[]",double[]=>"f64[]",decimal[]=>"decimal[]",bool[]=>"bool[]",string[]=>"string[]",_=>"json"
    };
    private static object? RestoreValue(object? value,string type)
    {
        if(value is null||type=="null")return null;
        if(value is not JsonElement data)return value;
        return type switch
        {
            "i16"=>data.Deserialize<short>(SpoolJson),"i32"=>data.Deserialize<int>(SpoolJson),"i64"=>data.Deserialize<long>(SpoolJson),"f32"=>data.Deserialize<float>(SpoolJson),"f64"=>data.Deserialize<double>(SpoolJson),"decimal"=>data.Deserialize<decimal>(SpoolJson),
            "bool"=>data.GetBoolean(),"string"=>data.GetString(),"guid"=>data.GetGuid(),"date"=>data.GetDateTime(),"offset"=>data.GetDateTimeOffset(),"bytes"=>data.GetBytesFromBase64(),
            "i16[]"=>data.Deserialize<short[]>(SpoolJson),"i32[]"=>data.Deserialize<int[]>(SpoolJson),"i64[]"=>data.Deserialize<long[]>(SpoolJson),"f32[]"=>data.Deserialize<float[]>(SpoolJson),"f64[]"=>data.Deserialize<double[]>(SpoolJson),"decimal[]"=>data.Deserialize<decimal[]>(SpoolJson),"bool[]"=>data.Deserialize<bool[]>(SpoolJson),"string[]"=>data.Deserialize<string[]>(SpoolJson),_=>data.Clone()
        };
    }
    private static async Task WriteRowAsync(StreamWriter writer,ResultRow row,CancellationToken ct)
    {
        var stored=new StoredRow(row.Database,row.Values,row.Values.ToDictionary(x=>x.Key,x=>StoredType(x.Value),StringComparer.Ordinal));
        await writer.WriteLineAsync(JsonSerializer.Serialize(stored,SpoolJson).AsMemory(),ct);
    }
    private static async IAsyncEnumerable<ResultRow> ReadSpoolRowsAsync(string path,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
    {
        using var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true),Encoding.UTF8,true,65536);
        while(await reader.ReadLineAsync(ct) is string line)
        {
            ct.ThrowIfCancellationRequested();var stored=JsonSerializer.Deserialize<StoredRow>(line,SpoolJson)??throw new InvalidDataException("임시 metadata 행이 비어 있습니다.");
            yield return new(){Database=stored.Database,Values=stored.Values.ToDictionary(x=>x.Key,x=>RestoreValue(x.Value,stored.Types.GetValueOrDefault(x.Key,"json")),StringComparer.Ordinal)};
        }
    }
    private static async Task<StagedPanel> StagePanelAsync(IExplorerRepository repository,ConnectionProfile profile,PanelReference plan,string stageFolder,bool images,
        Action<string>? progress,CancellationToken ct)
    {
        Directory.CreateDirectory(stageFolder);StagedPanel? staged=null;
        await repository.WithSnapshotAsync(profile,plan.Database,async(snapshot,captured,token)=>
        {
            var inspection=(await snapshot.QueryAsync(profile,plan.Database,BrowseLevel.Inspections,new(){InspectionId=plan.Inspection},1,null,token)).Rows.SingleOrDefault()
                ??throw new KeyNotFoundException($"시작 목록의 검사 {plan.Database}/{plan.Inspection}가 삭제되었거나 조회할 수 없습니다.");
            var detail=await snapshot.DetailAsync(profile,inspection.Key(profile.Identity,BrowseLevel.Inspections),token);
            ResultRow? layerDetail=null;string layerId=inspection.Text("layer_id");
            if(layerId.Length>0)layerDetail=await snapshot.DetailAsync(profile,new(profile.Identity,plan.Database,"layer",layerId),token);
            string rowsFile=Path.Combine(stageFolder,"rows.jsonl"),rowsPending=rowsFile+".partial";
            string? imagesFile=images?Path.Combine(stageFolder,"images.jsonl"):null;
            await using var imageWriter=imagesFile is null?null:new StreamWriter(new FileStream(imagesFile+".partial",FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true),new UTF8Encoding(false));
            long expected=0,actual=0;var barCounts=new Dictionary<string,long>(StringComparer.Ordinal);var punchUnits=new HashSet<int>();bool unknownPunch=false;
            await using(var rowsWriter=new StreamWriter(new FileStream(rowsPending,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true),new UTF8Encoding(false)))
            {
                async Task Consume(long count,IAsyncEnumerable<ResultRow> source,CancellationToken readToken)
                {
                    expected=count;
                    await foreach(var row in source.WithCancellation(readToken))
                    {
                        actual++;await WriteRowAsync(rowsWriter,row,readToken);
                        if(ExportService.IsRecordedPunch(row))
                        {if(row.Long("unit_idx") is long unit&&unit>=0&&unit<=int.MaxValue)punchUnits.Add((int)unit);else unknownPunch=true;}
                        else
                        {string bar=row.Long("source_metadata_version")>=1&&row.Long("source_block_idx")>0?row.Text("source_block_idx"):"미기록";barCounts[bar]=barCounts.GetValueOrDefault(bar)+1;}
                        if(imageWriter is not null)
                        {
                            StoredImage record;
                            try
                            {
                                if(row.SnapshotImageError is string imageError)throw new InvalidDataException(imageError);
                                var payload=row.SnapshotImage;
                                if(payload is null||payload.Bytes.Length==0)record=new(actual,row.Text("defects"),"not_recorded",null,0,0,0,0,"DB 이미지 미기록 또는 0바이트");
                                else
                                {
                                    string name=actual.ToString("D12",System.Globalization.CultureInfo.InvariantCulture)+".bytea",path=Path.Combine(stageFolder,name),pending=path+".partial";
                                    await using(var file=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true))await file.WriteAsync(payload.Bytes,readToken);
                                    readToken.ThrowIfCancellationRequested();File.Move(pending,path,false);
                                    record=new(actual,row.Text("defects"),"staged",name,payload.Width,payload.Height,payload.Channels,payload.Bytes.LongLength,null);
                                }
                            }
                            catch(Exception ex) when(ex is not OperationCanceledException)
                            {record=new(actual,row.Text("defects"),"failed",null,0,0,0,0,ex.Message);}
                            await imageWriter.WriteLineAsync(JsonSerializer.Serialize(record,SpoolJson).AsMemory(),readToken);
                            if(actual%250==0)progress?.Invoke($"검사 {plan.Inspection} · 연속 조회로 이미지 원본 {actual:N0}/{expected:N0}개 읽는 중");
                        }
                    }
                }
                if(images)await snapshot.StreamInspectionPayloadsAsync(profile,plan.Database,plan.Inspection,Consume,token);
                else await snapshot.StreamInspectionAsync(profile,plan.Database,plan.Inspection,Consume,token);
                await rowsWriter.FlushAsync(token);
            }
            if(expected!=actual)throw new InvalidDataException($"패널 metadata 행수 불일치: expected={expected}, actual={actual}. 임시 자료를 보존했습니다.");
            token.ThrowIfCancellationRequested();File.Move(rowsPending,rowsFile,false);
            if(imageWriter is not null&&imagesFile is not null)
            {
                await imageWriter.FlushAsync(token);await imageWriter.DisposeAsync();
                token.ThrowIfCancellationRequested();File.Move(imagesFile+".partial",imagesFile,false);
            }
            staged=new(inspection,detail,layerDetail,captured,DateTimeOffset.MinValue,stageFolder,rowsFile,imagesFile,expected,barCounts,punchUnits.Order().ToArray(),unknownPunch);
        },ct);
        // WithSnapshotAsync has committed and returned its DB connection before any PNG/ERT/CSV rendering.
        return (staged??throw new InvalidDataException("패널 snapshot 결과가 없습니다.")) with{SnapshotEndedAt=DateTimeOffset.Now};
    }
    private static void RemoveCompletedSpool(StagedPanel panel)
    {
        // Only exact files created by this panel staging operation are removed. Failed/partial staging is retained.
        if(panel.ImagesFile is not null)
        {
            foreach(string line in File.ReadLines(panel.ImagesFile))
            {
                var entry=JsonSerializer.Deserialize<StoredImage>(line,SpoolJson);
                if(entry?.Payload is not string name)continue;
                if(Path.GetFileName(name)!=name)throw new InvalidDataException("임시 이미지 파일명이 올바르지 않습니다.");
                string path=Path.Combine(panel.Folder,name);if(File.Exists(path))File.Delete(path);
            }
            File.Delete(panel.ImagesFile);
        }
        File.Delete(panel.RowsFile);
        if(!Directory.EnumerateFileSystemEntries(panel.Folder).Any())Directory.Delete(panel.Folder,false);
    }

    public static async Task<BundleResult> ExportAsync(IExplorerRepository repository,ConnectionProfile profile,string root,
        IReadOnlyList<BundleScope> scopes,BundleOptions options,
        Func<ResultRow,ImagePayload,string,CancellationToken,Task<string>> saveImage,Action<string>? progress,CancellationToken ct,Action<BundleProgress>? detailedProgress=null,
        Func<BundlePlanSummary,CancellationToken,Task<bool>>? confirmPlan=null)
    {
        if(!options.Ert&&!options.Images&&!options.Counts&&!options.DefectCsv&&!options.HandlerFiles)throw new ArgumentException("저장할 항목을 하나 이상 선택하세요.");
        Directory.CreateDirectory(root);DateTimeOffset started=DateTimeOffset.Now;
        string jobId=started.ToString("yyyyMMdd_HHmmss",System.Globalization.CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N");
        // Operator result folders contain only requested artifacts. All provenance and diagnostics stay on this PC.
        string diagnosticFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synapse","NasExplorer","ExportHistory",jobId);
        Directory.CreateDirectory(diagnosticFolder);
        const string consistency="시작 시 내보낼 검사 Index 목록을 고정. 각 패널의 metadata·판정·이미지 원본은 동일한 패널별 snapshot. 패널 간 기준시각은 다를 수 있음. DB snapshot 해제 뒤 PNG/ERT/CSV 생성.";
        await ExportService.WriteNewAsync(Path.Combine(diagnosticFolder,"내보내기조건.json"),JsonSerializer.Serialize(new{outputDirectory=Path.GetFullPath(root),options,scopes,startedAt=started,consistency,scope="선택한 모델/LOT/레이어의 패널별 최신 검사. 명시 선택한 검사와 식별·시각 미기록 검사는 보존. 과거 검사 포함은 별도 옵션. 불량 검색조건과 PC 제한을 적용하지 않음."},new JsonSerializerOptions{WriteIndented=true}),ct);
        long panels=0,defects=0,erts=0,images=0,missing=0,failures=0,planned=0,snapshotCount=0,sidecarFiles=0,unrecoverable=0;string status="completed";bool selectionComplete=false;
        var sidecarContributions=new List<HandlerSidecars.LayerContribution>();
        var destinations=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var snapshots=new List<object>();var selectionSnapshots=new List<object>();
        var geometry=new Dictionary<string,ModelGeometry>(StringComparer.Ordinal);var geometryErrors=new Dictionary<string,string>(StringComparer.Ordinal);var geometryReadAt=new Dictionary<string,DateTimeOffset>(StringComparer.Ordinal);
        string planFile=Path.Combine(diagnosticFolder,"패널목록.jsonl"),snapshotFile=Path.Combine(diagnosticFolder,"패널조회시점.jsonl"),spoolRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synapse","NasExplorer","ExportSpool",jobId);
        await using var log=new StreamWriter(Path.Combine(diagnosticFolder,"처리내역.jsonl"),false,new UTF8Encoding(true)){AutoFlush=true};
        await using var snapshotLog=new StreamWriter(snapshotFile,false,new UTF8Encoding(true)){AutoFlush=true};
        StreamWriter? counts=null,bars=null;
        async Task Failure(ResultRow? row,string artifact,Exception error)
        {
            failures++;progress?.Invoke($"{artifact} 실패: {error.Message}");
            await log.WriteLineAsync(JsonSerializer.Serialize(new{database=row?.Database,inspection=row?.Text("inspection"),artifact,status="failed",error=error.Message}));
        }
        async Task RecordSidecarsAsync(HandlerSidecars.Outcome outcome,ResultRow? row)
        {
            sidecarFiles+=outcome.SavedFiles.Count;unrecoverable+=outcome.Unrecoverable.Count;
            await log.WriteLineAsync(JsonSerializer.Serialize(new{database=row?.Database,inspection=row?.Text("inspection"),artifact="handler_sidecars",savedFiles=outcome.SavedFiles,notRequired=outcome.NotRequired,unrecoverable=outcome.Unrecoverable,failures=outcome.Failures,sourceNotes=outcome.SourceNotes},SpoolJson));
            foreach(var reason in outcome.Failures)await Failure(row,reason.Artifact,new IOException(reason.Message));
        }
        try
        {
            detailedProgress?.Invoke(new("planning",0,0,0,0));
            // Resolve only the source IDs first. Later inserts cannot silently expand this export job.
            var seen=new HashSet<(string Database,string Inspection)>();
            var latest=new Dictionary<PhysicalPanel,PanelReference>();
            var explicitIds=new HashSet<(string Database,string Inspection)>();
            string candidatesFile=Path.Combine(diagnosticFolder,"선택후보목록.jsonl");
            long candidates=0;
            await using(var planWriter=new StreamWriter(new FileStream(candidatesFile,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true),new UTF8Encoding(true)))
            {
                foreach(var dbGroup in scopes.GroupBy(x=>x.Database))
                {
                    await repository.WithSnapshotAsync(profile,dbGroup.Key,async(snapshot,captured,token)=>
                    {
                        foreach(var scope in dbGroup)
                        {
                            await foreach(var expanded in ExpandAsync(snapshot,profile,scope,token))
                            {
                                string? cursor=null;bool more;
                                do
                                {
                                    var page=await snapshot.QueryAsync(profile,scope.Database,BrowseLevel.Inspections,WholePanel(expanded),500,cursor,token);
                                    foreach(var row in page.Rows)
                                    {
                                        token.ThrowIfCancellationRequested();string id=row.Text("inspection");
                                        if(id.Length==0)throw new InvalidDataException("내보낼 검사의 원본 Index가 없습니다.");
                                        var reference=PlanReference(row,!scope.ExpandQuery&&scope.Filter.InspectionId.Length>0);
                                        if(reference.Explicit)explicitIds.Add((row.Database,id));
                                        if(!seen.Add((row.Database,id)))continue;
                                        if(reference.Physical is not null&&reference.Time is not null&&(!latest.TryGetValue(reference.Physical,out var previous)||IsLater(reference,previous)))latest[reference.Physical]=reference;
                                        await planWriter.WriteLineAsync(JsonSerializer.Serialize(reference).AsMemory(),token);candidates++;
                                    }
                                    cursor=page.Cursor;more=page.HasMore;
                                }while(more);
                            }
                        }
                        await planWriter.FlushAsync(token);selectionSnapshots.Add(new{database=dbGroup.Key,capturedAt=captured,scope="검사 Index 선택 목록"});
                    },ct);
                    progress?.Invoke($"내보낼 패널 목록 확인 중 · 검사 후보 {candidates:N0}개");
                    detailedProgress?.Invoke(new("planning",candidates,0,0,0));
                }
            }
            // Resolve across all source databases, using machine/model/LOT/layer/panel, never DB-local LOT/Layer indices.
            // Latest panels are written first, so explicit older selections/history cannot claim the primary NNN.ERT path.
            var primaryIds=latest.Values.Select(x=>(x.Database,x.Inspection)).ToHashSet();
            var targetFolders=new HashSet<string>(StringComparer.Ordinal);
            long unknownIdentityPanels=0;
            void RememberFolder(PanelReference reference)
            {if(reference.Physical is PhysicalPanel key)targetFolders.Add($"{key.Machine} / Model({key.Model}) / LotID({key.Lot}) / Layer({key.Layer})");else unknownIdentityPanels++;}
            await using(var planWriter=new StreamWriter(new FileStream(planFile,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true),new UTF8Encoding(true)))
            {
                foreach(var reference in latest.Values)
                {ct.ThrowIfCancellationRequested();await planWriter.WriteLineAsync(JsonSerializer.Serialize(reference).AsMemory(),ct);RememberFolder(reference);planned++;}
                using var candidatesReader=new StreamReader(new FileStream(candidatesFile,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true),Encoding.UTF8,true,65536);
                while(await candidatesReader.ReadLineAsync(ct) is string candidateLine)
                {
                    ct.ThrowIfCancellationRequested();var reference=JsonSerializer.Deserialize<PanelReference>(candidateLine)??throw new InvalidDataException("검사 선택 후보가 손상되었습니다.");
                    if(primaryIds.Contains((reference.Database,reference.Inspection)))continue;
                    if(options.IncludeHistory||reference.Physical is null||reference.Time is null||explicitIds.Contains((reference.Database,reference.Inspection)))
                    {await planWriter.WriteLineAsync(candidateLine.AsMemory(),ct);RememberFolder(reference);planned++;}
                }
                await planWriter.FlushAsync(ct);
            }
            await log.WriteLineAsync(JsonSerializer.Serialize(new{artifact="selection",candidates,plannedPanels=planned,options.IncludeHistory,latestPanels=latest.Count,
                note="식별 필드나 시각 미기록 검사는 최신 여부를 추정하지 않고 보존. 명시 선택 검사도 그대로 보존."}));
            seen.Clear();seen.TrimExcess();latest.Clear();explicitIds.Clear();primaryIds.Clear();selectionComplete=true;
            if(confirmPlan is not null&&!await confirmPlan(new(planned,diagnosticFolder){Folders=targetFolders.Order(StringComparer.Ordinal).Take(1000).ToArray(),FolderCount=targetFolders.Count,UnknownIdentityPanels=unknownIdentityPanels},ct))
                throw new OperationCanceledException("내보내기 대상 확인에서 취소했습니다.",ct);
            ct.ThrowIfCancellationRequested();
            counts=options.Counts?new StreamWriter(Path.Combine(root,"패널별불량수.csv"),false,new UTF8Encoding(true)):null;
            bars=options.Counts?new StreamWriter(Path.Combine(root,"바별검출수.csv"),false,new UTF8Encoding(true)):null;
            if(counts is not null)await counts.WriteLineAsync("\"출처 DB\",\"검사 Index\",\"Model\",\"LotID\",\"Layer\",\"패널 번호\",\"검사 시각\",\"검사 상태\",\"DB 불량 전체 건수\",\"DB 기록 total_defects\",\"이미지 저장\",\"이미지 없음\",\"ERT 저장\"");
            if(bars is not null)await bars.WriteLineAsync("\"출처 DB\",\"검사 Index\",\"Model\",\"LotID\",\"Layer\",\"패널 번호\",\"저장된 바 번호\",\"검출 수(펀치 증거행 제외)\"");
            detailedProgress?.Invoke(new("reading",planned,0,0,0));
            using var planReader=new StreamReader(new FileStream(planFile,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true),Encoding.UTF8,true,65536);
            while(await planReader.ReadLineAsync(ct) is string line)
            {
                ct.ThrowIfCancellationRequested();var plan=JsonSerializer.Deserialize<PanelReference>(line)??throw new InvalidDataException("내보낼 패널 목록이 손상되었습니다.");
                var reference=new ResultRow{Database=plan.Database,Values=new(){["inspection"]=plan.Inspection}};
                string stageFolder=Path.Combine(spoolRoot,Guid.NewGuid().ToString("N"));StagedPanel staged;
                try
                {
                    detailedProgress?.Invoke(new("reading",planned,panels,defects,images));
                    staged=await StagePanelAsync(repository,profile,plan,stageFolder,options.Images,progress,ct);
                    var stamp=new{database=plan.Database,inspection=plan.Inspection,capturedAt=staged.CapturedAt,snapshotEndedAt=staged.SnapshotEndedAt,scope="패널 metadata·판정·이미지 원본",rawRows=staged.Expected};
                    snapshotCount++;if(snapshots.Count<1000)snapshots.Add(stamp);
                    await snapshotLog.WriteLineAsync(JsonSerializer.Serialize(stamp));
                }
                catch(OperationCanceledException)
                {
                    await log.WriteLineAsync(JsonSerializer.Serialize(new{database=plan.Database,inspection=plan.Inspection,stageFolder,status="cancelled_spool_retained",phase="패널 snapshot/이미지 원본 임시 저장"}));throw;
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    await Failure(reference,"패널 snapshot/임시 저장",ex);
                    await log.WriteLineAsync(JsonSerializer.Serialize(new{database=plan.Database,inspection=plan.Inspection,stageFolder,status="spool_retained"}));continue;
                }
                var inspection=staged.Inspection;var detail=staged.Detail;var layerDetail=staged.LayerDetail;
                string id=plan.Inspection,number=inspection.Long("panel_no") is long n?n.ToString("D3"):"패널미기록";
                string panelName=inspection.Long("panel_no") is long panel?panel.ToString(System.Globalization.CultureInfo.InvariantCulture):"패널미기록";
                string folder=LayerFolder(root,inspection,options.ModelLayerLot),primary=Path.Combine(folder,panelName);
                if(!destinations.Add(primary))folder=Path.Combine(folder,"_검사이력",Component(inspection.Database),"검사_"+Component(id));
                string panelFolder=Path.Combine(folder,panelName);long panelImages=0,panelMissing=0;bool ertSaved=false,completeSpoolUse=true;
                progress?.Invoke($"검사 {id} · DB snapshot 해제 완료 / 파일 생성 중");
                try
                {
                    Directory.CreateDirectory(folder);
                    string panelDiagnosticFolder=Path.Combine(diagnosticFolder,"패널",Component(inspection.Database),"검사_"+Component(id));
                    await ExportService.WriteNewAsync(Path.Combine(panelDiagnosticFolder,number+".inspection.json"),ExportService.Json(new(){Database=inspection.Database,Values=new(inspection.Values)
                    { ["snapshot_at"]=staged.CapturedAt,["snapshot_ended_at"]=staged.SnapshotEndedAt,["inspection_fields"]=detail.Values,["current_layer_fields"]=layerDetail?.Values,["layer_metadata_scope"]="패널 조회 시점의 레이어 현재값. 검사 당시 재검 회차를 보장하지 않음.",["consistency"]=consistency}}),ct);
                    ModelGeometry? model=null;string modelKey=inspection.Text("model_name")+"_Layer"+inspection.Text("layer_name");
                    if(options.Ert||options.HandlerFiles&&detail.Long("disposed_bar_mask")>0)
                    {
                        if(!geometry.TryGetValue(modelKey,out model)&&!geometryErrors.ContainsKey(modelKey))
                        {
                            try{model=await repository.GeometryAsync(profile,inspection.Text("model_name"),inspection.Text("layer_name"),ct);geometry[modelKey]=model;geometryReadAt[modelKey]=DateTimeOffset.Now;}
                            catch(Exception ex) when(ex is not OperationCanceledException){geometryErrors[modelKey]=ex.Message;}
                        }
                    }
                    if(options.Ert)
                    {
                        if(model is null){completeSpoolUse=false;await Failure(inspection,"ERT",new InvalidDataException("모델 DB 자료가 필요합니다: "+geometryErrors[modelKey]));}
                        else
                        {
                            try
                            {
                                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);string ertPath=Path.Combine(folder,number+".ERT");
                                var document=await ErtExporter.WriteStreamingAsync(ertPath,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),inspection.Long("panel_no") is long panelNumber?checked((int)panelNumber):throw new InvalidDataException("패널 번호 미기록"),inspection["inspection_time"] is DateTime time?time:throw new InvalidDataException("검사 시각 미기록"),inspection.Text("inspection_status"),(int?)detail.Long("fov_count"),model,staged.Expected,ReadSpoolRowsAsync(staged.RowsFile,ct),new UTF8Encoding(false),ct,ErtMasterFolder(inspection,layerDetail,options.ModelLayerLot));
                                await ExportService.WriteNewAsync(Path.Combine(panelDiagnosticFolder,number+".ERT.source.json"),JsonSerializer.Serialize(new{inspection.Database,inspection=id,capturedAt=staged.CapturedAt,snapshotEndedAt=staged.SnapshotEndedAt,modelKey,modelDatabase=profile.ModelDatabase,modelReadCompletedAt=geometryReadAt.GetValueOrDefault(modelKey),modelConsistency="패널 snapshot 해제 후 조회한 모델 DB 별도 스냅샷 / 검사 당시 모델 보장 아님",document.Rows,document.Notes},new JsonSerializerOptions{WriteIndented=true}),ct);
                                ertSaved=true;erts++;
                            }
                            catch(Exception ex) when(ex is not OperationCanceledException){completeSpoolUse=false;await Failure(inspection,"ERT",ex);}
                        }
                    }
                    if(options.DefectCsv)await ExportService.CsvAsync(Path.Combine(panelFolder,"불량목록.csv"),ReadSpoolRowsAsync(staged.RowsFile,ct),ct);
                    if(options.HandlerFiles)
                    {
                        var outcome=await HandlerSidecars.PreparePanelAsync(folder,panelFolder,inspection,detail,layerDetail,staged.RecordedPunchUnits,model,ct);
                        if(outcome.Contribution is not null)sidecarContributions.Add(outcome.Contribution);
                        await RecordSidecarsAsync(outcome,inspection);
                        if(staged.UnknownPunchUnit)
                        {unrecoverable++;await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,artifact="punch.json",status="unrecoverable",reason="펀치 증거가 있으나 원본 unit_idx가 미기록 또는 지원 정수 범위 밖입니다."}));}
                    }
                    if(options.Images&&staged.ImagesFile is not null)
                    {
                        using var imageReader=new StreamReader(new FileStream(staged.ImagesFile,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true),Encoding.UTF8,true,65536);long ordinal=0;
                        await foreach(var row in ReadSpoolRowsAsync(staged.RowsFile,ct))
                        {
                            ct.ThrowIfCancellationRequested();ordinal++;
                            string imageLine=await imageReader.ReadLineAsync(ct)??throw new InvalidDataException("이미지 임시 목록이 metadata보다 짧습니다.");
                            var image=JsonSerializer.Deserialize<StoredImage>(imageLine,SpoolJson)??throw new InvalidDataException("이미지 임시 목록이 비어 있습니다.");
                            if(image.Ordinal!=ordinal||image.Defect!=row.Text("defects"))throw new InvalidDataException("이미지 임시 목록과 metadata 원본 Index가 일치하지 않습니다.");
                            if(image.Status=="not_recorded")
                            {
                                missing++;panelMissing++;await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,defect=image.Defect,artifact="image",status="not_recorded",reason=image.Error}));continue;
                            }
                            if(image.Status=="failed"){completeSpoolUse=false;await Failure(row,"이미지 원본 읽기/임시 저장",new IOException(image.Error));continue;}
                            try
                            {
                                if(image.Payload is null||Path.GetFileName(image.Payload)!=image.Payload||image.Bytes is <0 or >int.MaxValue)throw new InvalidDataException("이미지 임시 파일 정보가 올바르지 않습니다.");
                                string path=Path.Combine(staged.Folder,image.Payload);if(new FileInfo(path).Length!=image.Bytes)throw new InvalidDataException("이미지 임시 파일 바이트 수가 다릅니다.");
                                var payload=new ImagePayload(await File.ReadAllBytesAsync(path,ct),image.Width,image.Height,image.Channels,inspection.Database,image.Defect);
                                string saved=await saveImage(row,payload,panelFolder,ct);images++;panelImages++;
                                await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,defect=image.Defect,artifact="image",status="saved",path=saved}));
                            }
                            catch(Exception ex) when(ex is not OperationCanceledException){completeSpoolUse=false;await Failure(row,"이미지 파일 생성",ex);}
                            if(ordinal%25==0)
                            {
                                progress?.Invoke($"패널 {number} · 이미지 {panelImages:N0}개 저장 / 없음 {panelMissing:N0}");
                                detailedProgress?.Invoke(new("writing",planned,panels,defects,images,ordinal,staged.Expected));
                            }
                        }
                        if(await imageReader.ReadLineAsync(ct) is not null)throw new InvalidDataException("이미지 임시 목록이 metadata보다 깁니다.");
                    }
                    if(counts is not null)
                    {
                        object?[] fields={inspection.Database,id,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),inspection["panel_no"],inspection["inspection_time"],inspection["inspection_status"],staged.Expected,detail["total_defects"],panelImages,panelMissing,ertSaved};
                        await counts.WriteLineAsync(string.Join(",",fields.Select(ExportService.CsvCell)));
                        foreach(var bar in staged.BarCounts.OrderBy(x=>x.Key))await bars!.WriteLineAsync(string.Join(",",new object?[]{inspection.Database,id,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),inspection["panel_no"],bar.Key,bar.Value}.Select(ExportService.CsvCell)));
                        await counts.FlushAsync(ct);await bars!.FlushAsync(ct);
                    }
                    panels++;defects+=staged.Expected;
                    await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,panel=number,folder,defects=staged.Expected,capturedAt=staged.CapturedAt,snapshotEndedAt=staged.SnapshotEndedAt,ertSaved,images=panelImages,missing=panelMissing,status="processed"}));
                    progress?.Invoke($"패널 {panels:N0}/{planned:N0}개 · 불량 {defects:N0}건 · ERT {erts:N0}개 · 이미지 {images:N0}개 / 미기록 {missing:N0}개 · 실패 {failures:N0}건");
                    detailedProgress?.Invoke(new("writing",planned,panels,defects,images));
                    if(completeSpoolUse)
                    {
                        try{RemoveCompletedSpool(staged);}
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,artifact="spool_cleanup",status="retained",stageFolder=staged.Folder,error=ex.Message}));}
                    }
                    else await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,status="spool_retained",stageFolder=staged.Folder}));
                }
                catch(OperationCanceledException)
                {await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,status="cancelled_spool_retained",stageFolder=staged.Folder,phase="DB snapshot 해제 후 파일 생성"}));throw;}
                catch(Exception ex) when(ex is not OperationCanceledException)
                {await Failure(inspection,"패널 파일 생성",ex);await log.WriteLineAsync(JsonSerializer.Serialize(new{database=inspection.Database,inspection=id,status="spool_retained",stageFolder=staged.Folder}));}
            }
            foreach(var group in sidecarContributions.GroupBy(x=>x.LayerFolder,StringComparer.OrdinalIgnoreCase))
                await RecordSidecarsAsync(await HandlerSidecars.FinalizeLayerAsync(group.ToArray(),ct,Path.Combine(diagnosticFolder,"레이어참고",ExportService.SafeComponent(group.Key))),null);
            if(panels==0&&failures==0)status="empty";
            if(Directory.Exists(spoolRoot)&&!Directory.EnumerateFileSystemEntries(spoolRoot).Any())Directory.Delete(spoolRoot,false);
        }
        catch(OperationCanceledException ex){status="cancelled";ex.Data["DiagnosticFolder"]=diagnosticFolder;throw;}
        catch(Exception ex){status="failed";ex.Data["DiagnosticFolder"]=diagnosticFolder;throw;}
        finally
        {
            foreach(var writer in new[]{counts,bars})
            {
                if(writer is null)continue;
                try{await writer.DisposeAsync();}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException)
                {await Failure(null,"불량 수 CSV 마무리",error);}
            }
            if(status=="completed")status=failures>0?"partial_failure":unrecoverable>0?"completed_with_limitations":"completed";
            await File.WriteAllTextAsync(Path.Combine(diagnosticFolder,"내보내기결과.json"),JsonSerializer.Serialize(new{status,outputDirectory=Path.GetFullPath(root),selectionComplete,plannedPanels=planned,panels,defects,ertFiles=erts,images,missingImages=missing,failures,sidecarFiles,unrecoverable,consistency,selectionSnapshots,snapshots,snapshotCount,snapshotSamplesTruncated=snapshotCount>snapshots.Count,snapshotManifest=Path.GetFileName(snapshotFile),selectionManifest=Path.GetFileName(planFile),retainedSpoolDirectory=Directory.Exists(spoolRoot)?spoolRoot:null,endedAt=DateTimeOffset.Now,modelConsistency="패널 snapshot 해제 후 조회한 모델 DB 별도 스냅샷. 검사 당시 모델을 보장하지 않음.",history="동일 장비/모델/LOT/Layer/패널의 추가 검사 기록은 _검사이력 하위에 보존"},new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
            detailedProgress?.Invoke(new(status=="completed"||status=="completed_with_limitations"?"completed":status,planned,panels,defects,images));
        }
        return new(status,panels,defects,erts,images,missing,failures,sidecarFiles,unrecoverable){DiagnosticFolder=diagnosticFolder};
    }
}
