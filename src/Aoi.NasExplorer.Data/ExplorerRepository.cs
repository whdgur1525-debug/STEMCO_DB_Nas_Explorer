using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Aoi.NasExplorer.Core;
using Npgsql;
using NpgsqlTypes;

namespace Aoi.NasExplorer.Data;

public sealed class ExplorerRepository : IExplorerRepository,IDisposable
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _schemas = new();
    private readonly SemaphoreSlim _sourceGate=new(1,1);
    private readonly Dictionary<string,(NpgsqlDataSource Source,long Used)> _sources=new(StringComparer.Ordinal);
    private long _sourceClock;
    private NpgsqlConnection? _snapshot;
    private string? _snapshotDatabase;
    public static string ConnectionString(ConnectionProfile p, string database)
    {
        p.Validate();
        if (!Enum.TryParse<SslMode>(p.SslMode, true, out var ssl)) throw new ArgumentException("TLS 연결 방식을 확인하세요.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = p.Host, Port = p.Port, Username = p.Username, Password = p.Password,
            Database = database, SslMode = ssl, RootCertificate = string.IsNullOrWhiteSpace(p.RootCertificate) ? null : p.RootCertificate,
            Timeout = Math.Min(p.TimeoutSeconds, 15), CommandTimeout = p.TimeoutSeconds, Pooling = true,
            MaxPoolSize = 3, MinPoolSize = 0, ConnectionIdleLifetime = 20, ConnectionPruningInterval = 10,
            ApplicationName = "AOI-NasExplorer", IncludeErrorDetail = false,
            Options = $"-c default_transaction_read_only=on -c statement_timeout={p.TimeoutSeconds * 1000} -c lock_timeout=2000 -c idle_in_transaction_session_timeout=30000"
        }.ConnectionString;
    }
    private async Task<NpgsqlConnection> OpenAsync(ConnectionProfile p, string db, CancellationToken ct,bool pool=true)
    {
        string connectionString=ConnectionString(p,db);
        if(!pool)
        {
            var settings=new NpgsqlConnectionStringBuilder(connectionString){Pooling=false};var c=new NpgsqlConnection(settings.ConnectionString);
            try{await c.OpenAsync(ct);return c;}catch{await c.DisposeAsync();throw;}
        }
        await _sourceGate.WaitAsync(ct);
        try
        {
            if(!_sources.TryGetValue(connectionString,out var entry))
            {
                if(_sources.Count>=6){var old=_sources.MinBy(x=>x.Value.Used);_sources.Remove(old.Key);old.Value.Source.Dispose();}
                entry=(NpgsqlDataSource.Create(connectionString),++_sourceClock);
            }
            else entry=(entry.Source,++_sourceClock);
            _sources[connectionString]=entry;
            return await entry.Source.OpenConnectionAsync(ct);
        }
        finally{_sourceGate.Release();}
    }
    public void Dispose()
    {
        _sourceGate.Wait();try{foreach(var item in _sources.Values)item.Source.Dispose();_sources.Clear();}finally{_sourceGate.Release();}
    }
    private async Task<IReadOnlyDictionary<string, string>> SchemaAsync(ConnectionProfile p, NpgsqlConnection c, CancellationToken ct)
    {
        string key = $"{p.Identity}/{p.Username}/{c.Database}";
        if (_schemas.TryGetValue(key, out var known)) return known;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT table_name,column_name,udt_name FROM information_schema.columns WHERE table_schema='public' AND table_name IN ('lot_id','layer','inspection','defects')", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) fields[$"{r.GetString(0)}.{r.GetString(1)}"] = r.GetString(2);
        _schemas[key] = fields;
        return fields;
    }
    public void ClearSchemaCache() => _schemas.Clear();
    public async Task<IReadOnlyList<DatabaseInfo>> DiscoverAsync(ConnectionProfile p, CancellationToken ct)
    {
        var result = new List<DatabaseInfo>();
        await using var c = await OpenAsync(p, p.ManagementDatabase, ct,false);
        var names = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY datname COLLATE \"C\" DESC", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        string? statusError = null;
        try
        {
            await using var config = await OpenAsync(p, "aoi_config", ct,false);
            await using var cmd = new NpgsqlCommand("SELECT db_name,status FROM public.db_status", config);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) states[r.GetString(0)] = r.IsDBNull(1) ? "미기록" : r.GetString(1);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException) { statusError = "DB 상태 조회 불가 (aoi_config)"; }
        foreach (string name in names)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = await OpenAsync(p, name, ct,false);
                var schema = await SchemaAsync(p, db, ct);
                bool inspection = new[] {"lot_id.lot_id", "layer.layer", "inspection.inspection", "defects.defects"}.All(schema.ContainsKey);
                if (inspection || name.StartsWith("aoi_", StringComparison.Ordinal) || name == p.ManagementDatabase)
                    result.Add(new(name, states.GetValueOrDefault(name, "미등록"), inspection, statusError));
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            { result.Add(new(name, states.GetValueOrDefault(name, "알 수 없음"), false, SafeError(ex))); }
        }
        return result;
    }
    public static string SafeError(Exception ex) => ex switch
    {
        PostgresException pg => $"DB 오류 {pg.SqlState}: {pg.MessageText}",
        OperationCanceledException => "작업을 취소했습니다.",
        NpgsqlException => "DB 연결 또는 통신에 실패했습니다. 주소·인증·네트워크를 확인하세요.",
        _ => ex.Message
    };
    private static object NativeId(string text, string type) => type switch
    {
        "int8" => long.Parse(text, CultureInfo.InvariantCulture), "int4" => int.Parse(text, CultureInfo.InvariantCulture),
        "uuid" => Guid.Parse(text), _ => text
    };
    private static string Quoted(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static void Require(IReadOnlyDictionary<string,string> schema, string field)
    { if (!schema.ContainsKey(field)) throw new NotSupportedException($"필수 항목 {field}가 없는 DB입니다."); }
    public async Task<QueryPage> QueryAsync(ConnectionProfile p, string database, BrowseLevel level,
        SearchFilter f, int pageSize, string? cursor, CancellationToken ct)
    {
        f.Validate();
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if(_snapshot is not null&&_snapshotDatabase!=database)throw new InvalidOperationException("스냅샷의 DB 범위를 벗어났습니다.");
        var c = _snapshot??await OpenAsync(p, database, ct);
        try
        {
        var s = await SchemaAsync(p, c, ct);
        await using var cmd=BuildQuery(c,s,level,f,pageSize,cursor);
        var rows = new List<ResultRow>();
        await using(var r=await cmd.ExecuteReaderAsync(ct)) while(await r.ReadAsync(ct)) rows.Add(ReadRow(r,database));
        bool more = rows.Count > pageSize;
        if(more) rows.RemoveAt(rows.Count-1);
        string keyName=level switch{BrowseLevel.Models=>"model_name",BrowseLevel.Lots=>"lot_id",BrowseLevel.Layers=>"layer",BrowseLevel.Inspections=>"inspection",_=>"defects"};
        return new(rows,more,more ? rows[^1].Text(keyName) : null);
        }
        finally{if(_snapshot is null)await c.DisposeAsync();}
    }
    private static NpgsqlCommand BuildQuery(NpgsqlConnection c,IReadOnlyDictionary<string,string> s,BrowseLevel level,SearchFilter f,int? pageSize=null,string? cursor=null)
    {
        f.Validate();
        Require(s, "lot_id.lot_id");
        var conditions = new List<string>();
        var cmd = new NpgsqlCommand { Connection = c };
        void Param(string condition, string name, object value) { conditions.Add(condition); cmd.Parameters.AddWithValue(name, value); }
        bool dNeeded = level == BrowseLevel.Defects || f.DefectCode.HasValue || f.VmsCode.HasValue || f.VmsState.HasValue
            || f.AiCode.HasValue || f.CauseCode.HasValue || f.CauseText.Length > 0 || f.MinimumSize.HasValue || f.MaximumSize.HasValue || f.Images != ImageFilter.All;
        bool iNeeded = level >= BrowseLevel.Inspections || dNeeded || f.InspectionId.Length>0 || f.Machine.Length > 0 || f.Panel.HasValue || f.Pc.HasValue || f.From.HasValue || f.UntilExclusive.HasValue || f.LatestOnly;
        bool lNeeded = level >= BrowseLevel.Layers || iNeeded || f.Layer.Length > 0 || f.LayerId.Length>0;
        string from = "public.lot_id lo";
        if (lNeeded) { Require(s,"layer.layer"); from += " JOIN public.layer l ON l.lot_id=lo.lot_id"; }
        if (iNeeded) { Require(s,"inspection.inspection"); from += " JOIN public.inspection i ON i.layer=l.layer"; }
        if (dNeeded) { Require(s,"defects.defects"); from += " JOIN public.defects d ON d.inspection=i.inspection"; }
        if (f.Text.Length > 0) Param(f.Target switch{SearchTarget.Lot=>"lo.lot_id_name ILIKE @text ESCAPE '!'",SearchTarget.Model=>"lo.model_name ILIKE @text ESCAPE '!'",_=>"(lo.model_name ILIKE @text ESCAPE '!' OR lo.lot_id_name ILIKE @text ESCAPE '!')"}, "text", Like(f.Text));
        if (f.Model.Length > 0) Param("lo.model_name=@model", "model", f.Model);
        if (f.Lot.Length > 0) Param("lo.lot_id_name=@lot", "lot", f.Lot);
        if (f.Layer.Length > 0) Param("l.layer_name=@layer", "layer", f.Layer);
        if(f.LotId.Length>0) Param("lo.lot_id=@lotPk","lotPk",NativeId(f.LotId,s["lot_id.lot_id"]));
        if(f.LayerId.Length>0) Param("l.layer=@layerPk","layerPk",NativeId(f.LayerId,s["layer.layer"]));
        if(f.InspectionId.Length>0) Param("i.inspection=@inspectionPk","inspectionPk",NativeId(f.InspectionId,s["inspection.inspection"]));
        if (f.Machine.Length > 0) Param(f.MachineExact?"i.machine_id=@machine":"i.machine_id ILIKE @machine ESCAPE '!'", "machine", f.MachineExact?f.Machine:Like(f.Machine));
        if (f.Panel.HasValue) Param("i.panel_no=@panel", "panel", f.Panel.Value);
        if (f.Pc.HasValue) Param("i.pc_no=@pc", "pc", f.Pc.Value);
        if (f.From.HasValue) Param("i.time>=@from", "from", DateTime.SpecifyKind(f.From.Value, DateTimeKind.Unspecified));
        if (f.UntilExclusive.HasValue) Param("i.time<@until", "until", DateTime.SpecifyKind(f.UntilExclusive.Value, DateTimeKind.Unspecified));
        void OptionalFilter(string field, string op, object? value)
        {
            if (value is null) return;
            if (!s.ContainsKey("defects."+field)) { conditions.Add("FALSE"); return; }
            Param($"d.{Quoted(field)}{op}@{field}", field, value);
        }
        OptionalFilter("defect_type", "=", f.DefectCode); OptionalFilter("vms_code", "=", f.VmsCode);
        OptionalFilter("vms_state", "=", f.VmsState); OptionalFilter("ai_code", "=", f.AiCode);
        OptionalFilter("cause_code", "=", f.CauseCode);
        if(f.CauseText.Length>0)
        {if(s.ContainsKey("defects.cause_key"))Param("d.cause_key ILIKE @causeText ESCAPE '!'","causeText",Like(f.CauseText));else conditions.Add("FALSE");}
        if (f.MinimumSize.HasValue) { if(s.ContainsKey("defects.defect_size")) Param("d.defect_size>=@minimum", "minimum", f.MinimumSize.Value); else conditions.Add("FALSE"); }
        if (f.MaximumSize.HasValue) { if(s.ContainsKey("defects.defect_size")) Param("d.defect_size<=@maximum", "maximum", f.MaximumSize.Value); else conditions.Add("FALSE"); }
        if (f.Images != ImageFilter.All) conditions.Add(f.Images == ImageFilter.Present ? "octet_length(d.image)>0" : "(d.image IS NULL OR octet_length(d.image)=0)");
        if (f.LatestOnly && iNeeded)
            conditions.Add("NOT EXISTS (SELECT 1 FROM public.inspection newer WHERE newer.layer=i.layer AND newer.machine_id=i.machine_id AND newer.panel_no=i.panel_no AND newer.pc_no=i.pc_no AND (newer.time,newer.inspection)>(i.time,i.inspection))");
        string Field(string table,string alias,string name) => s.ContainsKey(table+"."+name) ? $"{alias}.{Quoted(name)}" : $"NULL AS {Quoted(name)}";
        string columns = level switch
        {
            BrowseLevel.Models => "lo.model_name",
            BrowseLevel.Lots => "lo.lot_id,lo.model_name,lo.lot_id_name,lo.time AS lot_time,lo.status AS lot_status",
            BrowseLevel.Layers => "l.layer,lo.model_name,lo.lot_id_name,l.layer_name,l.vms_status," + Field("layer","l","retest_count"),
            BrowseLevel.Inspections => "i.inspection,lo.lot_id,l.layer AS layer_id,lo.model_name,lo.lot_id_name,l.layer_name,i.machine_id,i.pc_no,i.panel_no,i.time AS inspection_time,i.status AS inspection_status," + Field("inspection","i","handler_tact_sec")+","+Field("inspection","i","tact_ms"),
            _ => "d.defects,i.inspection,lo.lot_id,l.layer AS layer_id,lo.model_name,lo.lot_id_name,l.layer_name,i.machine_id,i.pc_no,i.panel_no,i.time AS inspection_time,i.status AS inspection_status,"+
                string.Join(",",DefectFields.Select(x=>Field("defects","d",x)))+",(octet_length(d.image)>0) AS has_image,@missingFields AS missing_fields"
        };
        string key = level switch { BrowseLevel.Models => "lo.model_name COLLATE \"C\"", BrowseLevel.Lots => "lo.lot_id", BrowseLevel.Layers=>"l.layer", BrowseLevel.Inspections=>"i.inspection", _=>"d.defects" };
        string keyName = level switch { BrowseLevel.Models=>"model_name", BrowseLevel.Lots=>"lot_id", BrowseLevel.Layers=>"layer", BrowseLevel.Inspections=>"inspection", _=>"defects" };
        string table = level switch { BrowseLevel.Models or BrowseLevel.Lots=>"lot_id", BrowseLevel.Layers=>"layer", BrowseLevel.Inspections=>"inspection", _=>"defects" };
        if(cursor is not null) Param($"{key}{(level==BrowseLevel.Models?">":"<")}@cursor", "cursor", level==BrowseLevel.Models ? cursor : NativeId(cursor,s[table+"."+keyName]));
        cmd.CommandText = $"SELECT {(level==BrowseLevel.Defects?"":"DISTINCT ")}{columns} FROM {from} WHERE {(conditions.Count==0 ? "TRUE" : string.Join(" AND ",conditions))}";
        if(pageSize.HasValue)cmd.CommandText+=$" ORDER BY {key}{(level==BrowseLevel.Models?"":" DESC")} LIMIT @limit";
        // DISTINCT requires its ORDER BY expression to be selected. Only model names use explicit collation.
        if (level==BrowseLevel.Models) cmd.CommandText=cmd.CommandText.Replace("SELECT DISTINCT lo.model_name FROM", "SELECT DISTINCT lo.model_name COLLATE \"C\" AS model_name FROM");
        if(pageSize.HasValue)cmd.Parameters.AddWithValue("limit", pageSize.Value+1);
        if(level==BrowseLevel.Defects)cmd.Parameters.AddWithValue("missingFields",string.Join(",",DefectFields.Where(x=>!s.ContainsKey("defects."+x))));
        return cmd;
    }
    public static string Like(string value) => "%"+value.Replace("!","!!").Replace("%","!%").Replace("_","!_")+"%";
    private static readonly string[] DefectFields=["unit_idx","unit_x","unit_y","defect_type","cad_x","cad_y","raw_x","raw_y","d_attr","threshold","vms_state","vms_code","ai_code","cause_code","cause_key","defect_size","defect_extra","image_size","image_width","image_height","image_channels","source_metadata_version","source_elementary_num","source_result_idx","source_block_idx","source_axis_x","source_axis_y","source_file_name"];
    private static ResultRow ReadRow(NpgsqlDataReader r,string database)
    {
        var values = new Dictionary<string,object?>(StringComparer.Ordinal);
        for(int j=0;j<r.FieldCount;j++) values[r.GetName(j)] = r.IsDBNull(j) ? null : r.GetValue(j);
        return new(){Database=database,Values=values};
    }
    public async Task<ResultRow> DetailAsync(ConnectionProfile p,RowKey key,CancellationToken ct)
    {
        if(_snapshot is not null&&_snapshotDatabase!=key.Database)throw new InvalidOperationException("스냅샷의 DB 범위를 벗어났습니다.");
        ValidateTable(key.Table);
        var c=_snapshot??await OpenAsync(p,key.Database,ct);
        try{
        var s=await SchemaAsync(p,c,ct); Require(s,key.Table+"."+key.Table);
        var names=s.Keys.Where(x=>x.StartsWith(key.Table+".",StringComparison.Ordinal)).Select(x=>x[(key.Table.Length+1)..]).Where(x=>x!="image").Order(StringComparer.Ordinal);
        await using var cmd=new NpgsqlCommand($"SELECT {string.Join(",",names.Select(Quoted))} FROM public.{Quoted(key.Table)} WHERE {Quoted(key.Table)}=@id",c);
        cmd.Parameters.AddWithValue("id",NativeId(key.Id,s[key.Table+"."+key.Table]));
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r,key.Database) : throw new KeyNotFoundException("선택한 기록이 삭제되었거나 더 이상 존재하지 않습니다.");
        }finally{if(_snapshot is null)await c.DisposeAsync();}
    }
    private static void ValidateTable(string table)
    { if(table is not ("lot_id" or "layer" or "inspection" or "defects")) throw new ArgumentException("조회할 수 없는 테이블입니다."); }
    public async Task<ImagePayload?> ImageAsync(ConnectionProfile p,RowKey key,CancellationToken ct)
    {
        if(_snapshot is not null&&_snapshotDatabase!=key.Database)throw new InvalidOperationException("스냅샷의 DB 범위를 벗어났습니다.");
        if(key.Table!="defects") throw new ArgumentException("불량 이미지를 선택하세요.");
        var c=_snapshot??await OpenAsync(p,key.Database,ct);
        try{var s=await SchemaAsync(p,c,ct);
        string Col(string name)=>s.ContainsKey("defects."+name)?Quoted(name):"NULL";
        await using var cmd=new NpgsqlCommand($"SELECT CASE WHEN octet_length(image)<=67108864 THEN image END,{Col("image_width")},{Col("image_height")},{Col("image_channels")},octet_length(image) FROM public.defects WHERE defects=@id",c);
        cmd.Parameters.AddWithValue("id",NativeId(key.Id,s["defects.defects"]));
        await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return null;
        // Prevent a malformed row from allocating unbounded memory on a client.
        if(!r.IsDBNull(4)&&r.GetInt32(4)>64*1024*1024) throw new InvalidDataException("이미지 한 장이 64MB를 초과합니다. 원본 추출 범위를 확인하세요.");
        if(r.IsDBNull(0)) return null;
        var bytes=(byte[])r[0];
        return new(bytes,r.IsDBNull(1)?0:Convert.ToInt32(r[1]),r.IsDBNull(2)?0:Convert.ToInt32(r[2]),r.IsDBNull(3)?1:Convert.ToInt32(r[3]),key.Database,key.Id);
        }finally{if(_snapshot is null)await c.DisposeAsync();}
    }
    public async Task<ModelGeometry> GeometryAsync(ConnectionProfile p,string model,CancellationToken ct)
    {
        await using var c=await OpenAsync(p,p.ModelDatabase,ct);
        await using var modelTransaction=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await using var cmd=new NpgsqlCommand("SELECT model_id FROM public.aoi_model WHERE model_name=@model",c);
        cmd.Parameters.AddWithValue("model",model);
        object? id=await cmd.ExecuteScalarAsync(ct);
        if(id is null) throw new KeyNotFoundException("모델 DB에 해당 모델이 없습니다. 원본 필드 조회·CSV·이미지 저장은 계속 사용할 수 있습니다.");
        async Task<ResultRow> One(string table,string fields)
        {
            await using var q=new NpgsqlCommand($"SELECT {fields} FROM public.{table} WHERE model_id=@id",c); q.Parameters.AddWithValue("id",id);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) throw new InvalidDataException($"ERT 재구성에 필요한 {table} 정보가 없습니다.");
            var row=ReadRow(r,p.ModelDatabase);
            if(await r.ReadAsync(ct)) throw new InvalidDataException($"{table}의 모델 행이 여러 개여서 자동 선택할 수 없습니다.");
            return row;
        }
        static double[] Arr(ResultRow r,string field)=>r[field] as double[] ?? throw new InvalidDataException($"모델 {field} 배열이 없습니다.");
        var bp=await One("aoi_block_pos","image_resolution,x,y,width,height");
        var cu=await One("aoi_cellunits","frame_pos,unit_pos");
        var eu=await One("aoi_elem_unit","x,width,height");
        var ir=await One("aoi_inspect_region","x,y,width,height");
        double[] frames=Arr(cu,"frame_pos"); var ew=Arr(eu,"width"); var eh=Arr(eu,"height");
        if(ew.Length==0||eh.Length==0) throw new InvalidDataException("모델 유닛 크기가 없습니다.");
        if(Arr(eu,"x").Length>=2)
        {
            if(ew.Length<2)throw new InvalidDataException("분할 유닛의 폭 배열이 부족합니다.");
            frames=frames.Concat(frames.Select((x,index)=>x+ew[index%2])).Order().ToArray();
        }
        else frames=frames.Order().ToArray();
        var bx=Arr(bp,"x").Distinct().Order().ToArray(); var by=Arr(bp,"y").Distinct().Order().ToArray();
        var bw=Arr(bp,"width"); var bh=Arr(bp,"height");
        if(bx.Length==0||by.Length==0||bw.Length==0||bh.Length==0) throw new InvalidDataException("모델 블록 범위가 없습니다.");
        double Required(ResultRow r,string k)=>r.Number(k)??throw new InvalidDataException($"모델 {k} 값이 없습니다.");
        bool punchApply=false;int[] punches=[];
        await using(var exists=new NpgsqlCommand("SELECT to_regclass('public.aoi_punch') IS NOT NULL",c))
        if(await exists.ExecuteScalarAsync(ct) is true)
        {
            await using var punch=new NpgsqlCommand("SELECT punch_apply,punch_units FROM public.aoi_punch WHERE model_id=@id",c);punch.Parameters.AddWithValue("id",id);
            await using var r=await punch.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){punchApply=!r.IsDBNull(0)&&r.GetBoolean(0);if(!r.IsDBNull(1))punches=r.GetFieldValue<int[]>(1);}
        }
        return new(Required(bp,"image_resolution"),Required(ir,"x"),Required(ir,"y"),Required(ir,"x")+Required(ir,"width"),Required(ir,"y")+Required(ir,"height"),bx,by,bw[0],bh[0],frames,Arr(cu,"unit_pos").Order().ToArray(),ew[0],eh[0],Required(ir,"width"),Required(ir,"height")){PunchApply=punchApply,PunchUnits=punches};
    }
    public async Task StreamInspectionAsync(ConnectionProfile p,string database,string inspectionId,
        Func<long,IAsyncEnumerable<ResultRow>,CancellationToken,Task> consume,CancellationToken ct)
    {
        if(_snapshot is not null&&_snapshotDatabase!=database)throw new InvalidOperationException("스냅샷의 DB 범위를 벗어났습니다.");
        var c=_snapshot??await OpenAsync(p,database,ct);
        try{
        await using var transaction=_snapshot is null?await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct):null;
        var s=await SchemaAsync(p,c,ct);object id=NativeId(inspectionId,s["defects.inspection"]);
        long count;
        await using(var countCommand=new NpgsqlCommand("SELECT count(*) FROM public.defects WHERE inspection=@id",c,transaction))
        {countCommand.Parameters.AddWithValue("id",id);count=Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct));}
        async IAsyncEnumerable<ResultRow> Rows([EnumeratorCancellation]CancellationToken token=default)
        {
            var columns=s.Keys.Where(x=>x.StartsWith("defects.",StringComparison.Ordinal)).Select(x=>x[8..]).Where(x=>x!="image").Order(StringComparer.Ordinal);
            await using var cmd=new NpgsqlCommand($"SELECT {string.Join(",",columns.Select(Quoted))} FROM public.defects WHERE inspection=@id ORDER BY unit_idx,defects",c,transaction);cmd.Parameters.AddWithValue("id",id);
            await using var reader=await cmd.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))yield return ReadRow(reader,database);
        }
        await consume(count,Rows(ct),ct);if(transaction is not null)await transaction.CommitAsync(ct);
        }finally{if(_snapshot is null)await c.DisposeAsync();}
    }

    public async Task WithSnapshotAsync(ConnectionProfile p,string database,Func<IExplorerRepository,DateTimeOffset,CancellationToken,Task> consume,CancellationToken ct)
    {
        if(_snapshot is not null)throw new InvalidOperationException("중첩 스냅샷은 지원하지 않습니다.");
        await using var c=await OpenAsync(p,database,ct);
        await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await using var pin=new NpgsqlCommand("SELECT clock_timestamp(),pg_current_snapshot()::text",c,tx);
        DateTimeOffset captured;
        await using(var r=await pin.ExecuteReaderAsync(ct)){await r.ReadAsync(ct);captured=new DateTimeOffset(r.GetDateTime(0));}
        using var scoped=new ExplorerRepository{_snapshot=c,_snapshotDatabase=database};
        foreach(var pair in _schemas)scoped._schemas[pair.Key]=pair.Value;
        await consume(scoped,captured,ct);
        await tx.CommitAsync(ct);
    }

    public async IAsyncEnumerable<ResultRow> StreamAsync(ConnectionProfile p,string database,BrowseLevel level,SearchFilter filter,bool images,Action<DateTimeOffset>? captured,[EnumeratorCancellation]CancellationToken ct,IReadOnlySet<string>? selectedIds=null)
    {
        await using var c=await OpenAsync(p,database,ct);
        await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await using(var pin=new NpgsqlCommand("SELECT clock_timestamp(),pg_current_snapshot()::text",c,tx))
        {await using var r=await pin.ExecuteReaderAsync(ct);await r.ReadAsync(ct);captured?.Invoke(new DateTimeOffset(r.GetDateTime(0)));}
        using var scoped=new ExplorerRepository{_snapshot=c,_snapshotDatabase=database};
        foreach(var pair in _schemas)scoped._schemas[pair.Key]=pair.Value;
        string? cursor=null;bool more;var remaining=selectedIds?.ToHashSet(StringComparer.Ordinal);
        do
        {
            var page=await scoped.QueryAsync(p,database,level,filter,500,cursor,ct);
            foreach(var row in page.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if(remaining is not null&&!remaining.Remove(row.Key(p.Identity,level).Id))continue;
                // Do not attach image buffers to the retained page of 500 metadata rows.
                if(images)yield return new ResultRow{Database=row.Database,Values=row.Values,SnapshotImage=await scoped.ImageAsync(p,row.Key(p.Identity,BrowseLevel.Defects),ct)};
                else yield return row;
                if(remaining?.Count==0)break;
            }
            more=page.HasMore;cursor=page.Cursor;
        }while(more&&remaining?.Count!=0);
        if(remaining?.Count>0)throw new InvalidDataException($"선택한 기록 {remaining.Count}개가 현재 검색조건에 없거나 삭제되었습니다.");
        await tx.CommitAsync(ct);
    }

    public async Task<ScopeSummary> SummaryAsync(ConnectionProfile p,string database,SearchFilter filter,CancellationToken ct)
    {
        if(_snapshot is not null&&_snapshotDatabase!=database)throw new InvalidOperationException("스냅샷의 DB 범위를 벗어났습니다.");
        if(_snapshot is null)
        {
            ScopeSummary? result=null;
            await WithSnapshotAsync(p,database,async(repo,_,token)=>result=await repo.SummaryAsync(p,database,filter,token),ct);
            return result!;
        }
        var s=await SchemaAsync(p,_snapshot,ct);
        async Task<long> Count(BrowseLevel level)
        {
            await using var cmd=BuildQuery(_snapshot,s,level,filter);
            cmd.CommandText="SELECT count(*) FROM ("+cmd.CommandText+") q";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        long lots=await Count(BrowseLevel.Lots),inspections=await Count(BrowseLevel.Inspections),defects=await Count(BrowseLevel.Defects);
        await using var group=BuildQuery(_snapshot,s,BrowseLevel.Defects,filter);
        await using var iq=BuildQuery(_snapshot,s,BrowseLevel.Inspections,filter);
        // Both projections use the same named parameters and filter values.
        foreach(NpgsqlParameter parameter in iq.Parameters)
            if(!group.Parameters.Contains(parameter.ParameterName))group.Parameters.Add(new NpgsqlParameter(parameter.ParameterName,parameter.Value));
        string columns="ii.lot_id,ii.layer_id,ii.model_name,ii.lot_id_name,ii.layer_name,ii.machine_id,ii.panel_no";
        string defectSql=group.CommandText;
        string grouped=$"WITH ii AS ({iq.CommandText}),dd AS ({defectSql}) SELECT {columns},count(DISTINCT ii.inspection) AS inspection_count,count(dd.defects) AS defect_count,count(dd.defects) FILTER(WHERE dd.has_image) AS image_count,count(dd.defects) FILTER(WHERE dd.vms_state::text='1') AS vms_ng_count,count(dd.defects) FILTER(WHERE dd.vms_state::text='2') AS vms_pass_count FROM ii LEFT JOIN dd ON dd.inspection=ii.inspection GROUP BY {columns}";
        group.CommandText="SELECT count(*) FROM ("+grouped+") q";
        long panelGroups=Convert.ToInt64(await group.ExecuteScalarAsync(ct));
        group.CommandText=grouped+" ORDER BY ii.model_name,ii.lot_id_name,ii.layer_name,ii.machine_id,ii.panel_no LIMIT 1000";
        var rows=new List<ResultRow>();await using(var r=await group.ExecuteReaderAsync(ct))while(await r.ReadAsync(ct))rows.Add(ReadRow(r,database));
        await using var lotQuery=BuildQuery(_snapshot,s,BrowseLevel.Lots,filter);
        foreach(NpgsqlParameter parameter in lotQuery.Parameters)
            if(!group.Parameters.Contains(parameter.ParameterName))group.Parameters.Add(new NpgsqlParameter(parameter.ParameterName,parameter.Value));
        group.CommandText=$"WITH ll AS ({lotQuery.CommandText}),ii AS ({iq.CommandText}),dd AS ({defectSql}),ic AS (SELECT lot_id,count(*) AS inspection_count FROM ii GROUP BY lot_id),dc AS (SELECT lot_id,count(*) AS defect_count FROM dd GROUP BY lot_id) SELECT ll.lot_id,ll.model_name,ll.lot_id_name,coalesce(ic.inspection_count,0) AS inspection_count,coalesce(dc.defect_count,0) AS defect_count FROM ll LEFT JOIN ic USING(lot_id) LEFT JOIN dc USING(lot_id) ORDER BY ll.lot_id DESC LIMIT 1000";
        var lotRows=new List<ResultRow>();await using(var r=await group.ExecuteReaderAsync(ct))while(await r.ReadAsync(ct))lotRows.Add(ReadRow(r,database));
        return new(lots,inspections,defects,panelGroups,rows){LotCounts=lotRows};
    }
}
