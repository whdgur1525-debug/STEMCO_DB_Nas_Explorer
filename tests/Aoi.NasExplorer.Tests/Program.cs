using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Data;
using Aoi.NasExplorer.Export;
using Npgsql;

internal static class Program
{
    private static readonly List<object> Outcomes=[];
    private static int _failures;
    private static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"expected={expected}; actual={actual}");}
    private static void True(bool condition){if(!condition)throw new Exception("assertion failed");}
    private static void Throws<T>(Action action) where T:Exception{try{action();}catch(T){return;}throw new Exception($"Expected {typeof(T).Name}");}
    private static async Task Check(string name,Func<Task> action)
    {var clock=Stopwatch.StartNew();try{await action();Outcomes.Add(new{name,status="passed",ms=clock.ElapsedMilliseconds});Console.WriteLine("PASS "+name);}catch(Exception ex){_failures++;Outcomes.Add(new{name,status="failed",error=ExplorerRepository.SafeError(ex),ms=clock.ElapsedMilliseconds});Console.WriteLine("FAIL "+name+": "+ExplorerRepository.SafeError(ex));}}
    private static Task Check(string name,Action action)=>Check(name,()=>{action();return Task.CompletedTask;});
    public static async Task<int> Main(string[] args)
    {
        string output=Environment.GetEnvironmentVariable("NAS_TEST_OUTPUT")??Path.Combine(Path.GetTempPath(),"nas-explorer-tests");Directory.CreateDirectory(output);
        if(!args.Contains("--recovered"))await UnitAsync(output);
        if(args.Contains("--integration"))await IntegrationAsync(output);
        if(args.Contains("--stress"))await StressAsync(output);
        if(args.Contains("--recovered"))await RecoveredAsync(output);
        await File.WriteAllTextAsync(Path.Combine(output,"test-results.json"),JsonSerializer.Serialize(new{endedAt=DateTimeOffset.Now,failures=_failures,tests=Outcomes},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Tests: {Outcomes.Count}; failed: {_failures}");return _failures==0?0:1;
    }
    private static async Task UnitAsync(string output)
    {
        await ErtFormatChecks.RunAsync(output,(name,action)=>Check(name,action));
        await Check("profile rejects empty host",()=>Throws<ArgumentException>(()=>new ConnectionProfile().Validate()));
        await Check("profile diagnostic text does not expose the password",()=>
        {string value=(new ConnectionProfile{Password="synthetic-test-secret"}).ToString();True(!value.Contains("synthetic-test-secret",StringComparison.Ordinal)&&value.Contains("[redacted]",StringComparison.Ordinal));});
        await Check("profile rejects multiple hosts",()=>Throws<ArgumentException>(()=>new ConnectionProfile{Host="a,b"}.Validate()));
        await Check("profile rejects invalid port",()=>Throws<ArgumentException>(()=>new ConnectionProfile{Host="a",Port=0}.Validate()));
        await Check("query rejects reversed dates",()=>Throws<ArgumentException>(()=>new SearchFilter{From=new(2026,11,4),UntilExclusive=new(2026,11,3)}.Validate()));
        await Check("query rejects NaN",()=>Throws<ArgumentException>(()=>new SearchFilter{MinimumSize=double.NaN}.Validate()));
        await Check("connection strings escape password delimiters",()=>{var p=new ConnectionProfile{Host="127.0.0.1",Password="a;b='quoted'"};var b=new NpgsqlConnectionStringBuilder(ExplorerRepository.ConnectionString(p,"test"));Equal(p.Password,b.Password);True(b.Options!.Contains("default_transaction_read_only=on"));Equal(1,b.MaxPoolSize);True(!b.IncludeErrorDetail);});
        await Check("LIKE literal percent and underscore",()=>Equal("%a!%!_!!%",ExplorerRepository.Like("a%_!")));
        await Check("CSV quote and newline",()=>Equal("\"a\"\"b\nc\"",ExportService.CsvCell("a\"b\nc")));
        await Check("CSV spreadsheet formula escaped",()=>Equal("\"'=1+2\"",ExportService.CsvCell("=1+2")));
        await Check("CSV negative numeric remains numeric",()=>Equal("\"-2.5\"",ExportService.CsvCell(-2.5)));
        await Check("safe paths prevent traversal",()=>{string path=ExportService.SafeComponent("../a\\b:bad");True(!path.Contains('/')&&!path.Contains('\\')&&!path.Contains(':'));});
        await Check("sanitized filename collisions distinguished",()=>True(ExportService.SafeComponent("a/b")!=ExportService.SafeComponent("a\\b")));
        await Check("reserved Windows names escaped",()=>True(ExportService.SafeComponent("CON").StartsWith('_')));
        await Check("raw dimensions take priority over hex marker",()=>{byte[] b=[92,120,48,48];True(ReferenceEquals(b,ImageBytes.Normalize(b,2,2,1)));});
        await Check("bytea hex decoded",()=>True(ImageBytes.Normalize("\\x00ff"u8.ToArray(),0,0,1).SequenceEqual(new byte[]{0,255})));
        await Check("malformed hex diagnosed",()=>Throws<InvalidDataException>(()=>ImageBytes.Normalize("\\xzz"u8.ToArray(),0,0,1)));
        await Check("PNG signature detected",()=>Equal(".png",ImageBytes.Extension(new byte[]{137,80,78,71,13,10,26,10})));
        await Check("raw signature remains raw",()=>Equal(".raw",ImageBytes.Extension(new byte[]{0,1,2})));
        await Check("database is part of row identity",()=>{var a=new ResultRow{Database="a",Values=new(){["defects"]=1}};var b=new ResultRow{Database="b",Values=new(){["defects"]=1}};True(a.Key("nas",BrowseLevel.Defects)!=b.Key("nas",BrowseLevel.Defects));});
        await Check("JSON preserves null and zero",()=>{string j=ExportService.Json(new(){Database="a",Values=new(){["missing"]=null,["zero"]=0}});True(j.Contains("\"missing\": null")&&j.Contains("\"zero\": 0"));});
        await Check("non-finite source arrays remain visible",()=>Equal("[\"NaN\",\"Infinity\",\"-Infinity\"]",ResultRow.ValueText(new double[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})));
        await Check("JSON preserves non-finite values as named strings",()=>
        {
            using var json=JsonDocument.Parse(ExportService.Json(new(){Values=new(){["d_attr"]=new double[]{double.NaN,double.PositiveInfinity},["size"]=double.NegativeInfinity}}));
            Equal("NaN",json.RootElement.GetProperty("fields").GetProperty("d_attr")[0].GetString());Equal("-Infinity",json.RootElement.GetProperty("fields").GetProperty("size").GetString());
        });
        var g=new ModelGeometry(2.2,0,0,100,100,new double[]{0},new double[]{0},100,100,new double[]{10,30},new double[]{10,30},10,10,100,100);
        var row=new ResultRow{Database="a",Values=new(){["defects"]=1,["unit_idx"]=1,["unit_x"]=1,["unit_y"]=1,["raw_x"]=1.5,["raw_y"]=2.5,["cad_x"]=3.9,["cad_y"]=4.1,["defect_type"]=2,["vms_state"]=1,["vms_code"]=4,["source_metadata_version"]=1,["source_block_idx"]=2,["source_axis_x"]=3,["source_axis_y"]=4}};
        await Check("ERT raw coordinates integer CAD and source block",()=>{var ert=ErtExporter.Render("C:\\test","model","lot","L1",1,new(2026,11,4,9,1,2),"VMS_COMPLETED",1,g,new[]{row});True(ert.Text.Contains("1.5, 2.5, 2, 0, 1, 3, 4")&&ert.Text.Contains(", 4, 1, , ,B, 0, 0, 4, 3,"));Equal(5,ert.Text.Split("\r\n",StringSplitOptions.RemoveEmptyEntries).Length);});
        await Check("ERT no-defect panel retained",()=>{var ert=ErtExporter.Render("C:\\test","model","lot","L1",2,new(2026,11,4),"COMPLETED",null,g,Array.Empty<ResultRow>());True(ert.Text.StartsWith("4.3.1, 0,"));Equal(4,ert.Text.Split("\r\n",StringSplitOptions.RemoveEmptyEntries).Length);});
        await Check("ERT Handler area mapping explicitly noted",()=>True(ErtExporter.Render("C:\\test","m","l","L1",1,new(2026,11,4),"COMPLETED",1,g,new[]{row}).Notes.Any(x=>x.Contains("Handler area"))));
        await Check("ERT rejects comma in header fields",()=>Throws<InvalidDataException>(()=>ErtExporter.Render("C:\\test","m,x","l","L1",1,new(2026,11,4),"COMPLETED",1,g,new[]{row})));
        await Check("ERT missing raw coordinates cannot be invented",()=>{var values=new Dictionary<string,object?>(row.Values);values["raw_x"]=null;Throws<InvalidDataException>(()=>ErtExporter.Render("C:\\test","m","l","L1",1,new(2026,11,4),"COMPLETED",1,g,new[]{new ResultRow{Values=values}}));});
        await Check("geometry excludes configured punch units",()=>Equal(3,(g with{PunchApply=true,PunchUnits=new[]{1}}).TotalUnits));
        await Check("punch evidence requires raw and CAD coordinates plus both recorded attributes",()=>
        {
            var values=new Dictionary<string,object?>(row.Values){["raw_x"]=512d,["raw_y"]=512d,["cad_x"]=511d,["cad_y"]=512d,["d_attr"]=new double[]{9999,10},["vms_state"]=0,["defect_type"]=1,["defect_size"]=1.25};
            var ordinary=new ResultRow{Database="a",Values=values};True(!ExportService.IsRecordedPunch(ordinary));
            True(ErtExporter.Render("C:\\test","m","l","L1",1,new(2026,11,4),"COMPLETED",1,g,new[]{ordinary}).Text.Split("\r\n")[1].Split(',')[8].Trim()=="0");
            values["cad_x"]=512d;True(ExportService.IsRecordedPunch(new(){Values=values}));
        });
        await Check("unreviewed alignment and inspection errors follow latest DataTransfer VMS state",()=>
        {
            foreach(int code in new[]{19,20})
            {var error=new ResultRow{Values=new(row.Values){["defect_type"]=code,["vms_state"]=0}};True(ErtExporter.Render("C:\\test","m","l","L1",1,new(2026,11,4),"COMPLETED",1,g,new[]{error}).Text.Contains(",V, 0, 0,"));}
        });
        await Check("atomic output never overwrites existing file",async()=>{string path=Path.Combine(output,"existing.txt");await File.WriteAllTextAsync(path,"original");try{await ExportService.WriteNewAsync(path,"new",default);throw new Exception("Expected IOException");}catch(IOException){}Equal("original",await File.ReadAllTextAsync(path));});
        await Check("CSV streams all input and writes provenance",async()=>{string path=Path.Combine(output,"csv-"+Guid.NewGuid().ToString("N")+".csv");long count=await ExportService.CsvAsync(path,ExportService.Enumerate(new[]{row,row}),default);Equal(2L,count);True((await File.ReadAllTextAsync(path)).StartsWith("\"source_database\","));});
        await Check("cancelled CSV not exposed as completed output",async()=>{string path=Path.Combine(output,"cancel-"+Guid.NewGuid().ToString("N")+".csv");using var cts=new CancellationTokenSource();cts.Cancel();try{await ExportService.CsvAsync(path,ExportService.Enumerate(new[]{row}),cts.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}True(!File.Exists(path));});
        await Check("streamed ERT is identical to in-memory reference",async()=>
        {
            string path=Path.Combine(output,"stream-"+Guid.NewGuid().ToString("N"),"001.ert");string folder=Path.GetDirectoryName(path)!;
            var expected=ErtExporter.Render(folder,"model","lot","L1",1,new(2026,11,4,9,1,2),"VMS_COMPLETED",1,g,new[]{row});
            var result=await ErtExporter.WriteStreamingAsync(path,"model","lot","L1",1,new(2026,11,4,9,1,2),"VMS_COMPLETED",1,g,1,ExportService.Enumerate(new[]{row}),new UTF8Encoding(false),default);
            Equal(expected.Text,await File.ReadAllTextAsync(path));Equal(1,result.Rows);
        });
        await Check("streamed ERT mismatch preserves partial and no completed file",async()=>
        {
            string path=Path.Combine(output,"mismatch-"+Guid.NewGuid().ToString("N"),"001.ert");
            try{await ErtExporter.WriteStreamingAsync(path,"model","lot","L1",1,new(2026,11,4),"COMPLETED",1,g,2,ExportService.Enumerate(new[]{row}),new UTF8Encoding(false),default);throw new Exception("Expected mismatch");}catch(InvalidDataException){}
            True(!File.Exists(path));True(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.partial-*").Length==1);
        });
    }
    private static ConnectionProfile TestProfile()
    {
        if(!int.TryParse(Environment.GetEnvironmentVariable("NAS_TEST_PORT"),out int port)||port is <55000 or >55999)throw new Exception("NAS_TEST_PORT must identify the private test server (55000..55999).");
        return new(){Host="127.0.0.1",Port=port,Username="nas_test",Password=File.ReadAllText(Environment.GetEnvironmentVariable("NAS_TEST_PASSWORD_FILE")??throw new Exception("password file missing")).TrimEnd(),SslMode="Disable",TimeoutSeconds=10};
    }
    private static async Task Admin(ConnectionProfile p,string db,string sql)
    {
        var b=new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=p.Port,Username=p.Username,Password=p.Password,Database=db,Pooling=false,CommandTimeout=120};
        await using var c=new NpgsqlConnection(b.ConnectionString);await c.OpenAsync();await using var cmd=new NpgsqlCommand(sql,c);await cmd.ExecuteNonQueryAsync();
    }
    private static async Task IntegrationAsync(string output)
    {
        var p=TestProfile();string schemaPath=Environment.GetEnvironmentVariable("NAS_TEST_SCHEMA")??throw new Exception("schema path missing");
        string schema=string.Join('\n',(await File.ReadAllLinesAsync(schemaPath)).Where(x=>!x.StartsWith('\\')));
        if(schema.Contains("SUBSCRIPTION",StringComparison.OrdinalIgnoreCase)||schema.Contains("PUBLICATION",StringComparison.OrdinalIgnoreCase))throw new Exception("Replication DDL must be excluded before testing.");
        foreach(string db in new[]{"aoi_20990101","aoi_20990102","aoi_legacy","aoi_config","aoi_model"})
        {
            try{await Admin(p,"postgres",$"CREATE DATABASE \"{db}\"");}catch(PostgresException ex) when(ex.SqlState=="42P04"){}
        }
        foreach(string db in new[]{"aoi_20990101","aoi_20990102","aoi_legacy"})
        {
            await Admin(p,db,"DROP SCHEMA public CASCADE; CREATE SCHEMA public;");await Admin(p,db,schema);
            await Admin(p,db,"""
INSERT INTO lot_id(lot_id,lot_id_name,model_name,time,status) VALUES (1,'LOT_A','MODEL_A','2026-11-01','ACTIVE'),(2,'LOT_B','MODEL_B','2026-11-02','VMS_COMPLETED'),(3,'LITERAL_%','MODEL_A','2026-11-01','ACTIVE'),(4,'NO_INSPECTION','MODEL_A','2026-11-01','ACTIVE');
INSERT INTO layer(layer,layer_name,model_name,lot_id,vms_status) VALUES (1,'L1','MODEL_A',1,'VMS_COMPLETED'),(2,'L1','MODEL_B',2,'VMS_COMPLETED');
INSERT INTO inspection(inspection,layer,machine_id,pc_no,panel_no,time,status,fov_count) VALUES (1,1,'EQ09',1,1,'2026-11-02 23:59:59','COMPLETED',1),(2,1,'EQ09',1,1,'2026-11-03 10:00','VMS_COMPLETED',1),(3,1,'EQ09',2,1,'2026-11-03 10:00','VMS_COMPLETED',1),(4,1,'EQ10',1,1,'2026-11-03 10:00','START',1),(5,2,'EQ09',1,1,'2026-11-03 10:00','VMS_COMPLETED',1);
INSERT INTO defects(defects,inspection,unit_idx,unit_x,unit_y,raw_x,raw_y,cad_x,cad_y,defect_type,vms_state,vms_code,image,image_width,image_height,image_channels,cause_code,cause_key,defect_size,source_metadata_version,source_block_idx,source_axis_x,source_axis_y)
VALUES (1,1,1,1,1,10,20,30,40,1,0,0,decode('00010203','hex'),2,2,1,NULL,NULL,NULL,1,1,1,1),
(2,2,2,1,2,11,21,31,41,2,1,4,NULL,0,0,1,4001,'PINHOLE_MIN_SIZE',0,1,1,1,2),
(3,3,3,2,1,12,22,32,42,2,2,0,decode('11223344','hex'),2,2,1,4001,'PINHOLE_MIN_SIZE',2.5,1,1,2,1),
(4,4,4,2,2,13,23,33,43,3,0,0,NULL,0,0,1,NULL,NULL,NULL,1,1,2,2),
(5,5,1,1,1,14,24,34,44,1,1,1,NULL,0,0,1,NULL,NULL,NULL,1,1,1,1);
""");
        }
        await Admin(p,"aoi_legacy","ALTER TABLE defects DROP COLUMN cause_code,DROP COLUMN cause_key,DROP COLUMN defect_size,DROP COLUMN defect_extra,DROP COLUMN image_width,DROP COLUMN image_height,DROP COLUMN image_channels,DROP COLUMN source_metadata_version,DROP COLUMN source_block_idx,DROP COLUMN source_axis_x,DROP COLUMN source_axis_y;");
        await Admin(p,"aoi_config","CREATE TABLE IF NOT EXISTS db_status(db_name text primary key,status text); TRUNCATE db_status; INSERT INTO db_status VALUES ('aoi_20990101','ARCHIVE'),('aoi_20990102','ACTIVE');");
        await Admin(p,"aoi_model","""
DROP SCHEMA public CASCADE; CREATE SCHEMA public;
CREATE TABLE aoi_model(model_id bigint,model_name text); INSERT INTO aoi_model VALUES(1,'MODEL_A_LayerL1');
CREATE TABLE aoi_block_pos(model_id bigint,image_resolution double precision,x double precision[],y double precision[],width double precision[],height double precision[]); INSERT INTO aoi_block_pos VALUES(1,2.2,ARRAY[0.]::float8[],ARRAY[0.]::float8[],ARRAY[100.]::float8[],ARRAY[100.]::float8[]);
CREATE TABLE aoi_cellunits(model_id bigint,frame_pos double precision[],unit_pos double precision[]); INSERT INTO aoi_cellunits VALUES(1,ARRAY[10.,30.]::float8[],ARRAY[10.,30.]::float8[]);
CREATE TABLE aoi_elem_unit(model_id bigint,x double precision[],width double precision[],height double precision[]); INSERT INTO aoi_elem_unit VALUES(1,ARRAY[0.]::float8[],ARRAY[10.]::float8[],ARRAY[10.]::float8[]);
CREATE TABLE aoi_inspect_region(model_id bigint,x double precision,y double precision,width double precision,height double precision); INSERT INTO aoi_inspect_region VALUES(1,0,0,100,100);
CREATE TABLE aoi_punch(model_id bigint,punch_apply boolean,punch_units integer[]); INSERT INTO aoi_punch VALUES(1,false,'{}');
""");
        var repo=new ExplorerRepository();const string db1="aoi_20990101",db2="aoi_20990102";
        async Task<QueryPage> Q(BrowseLevel level,SearchFilter? f=null,int count=250,string? cursor=null,string db=db1)=>await repo.QueryAsync(p,db,level,f??new(),count,cursor,default);
        await Check("discovery finds archived and active DB without writes",async()=>{var dbs=await repo.DiscoverAsync(p,default);True(dbs.Any(x=>x.Name==db1&&x.State=="ARCHIVE"&&x.IsInspection));True(dbs.Any(x=>x.Name==db2&&x.State=="ACTIVE"&&x.IsInspection));True(dbs.Any(x=>x.Name=="aoi_model"&&!x.IsInspection));});
        await Check("models are distinct and pageable",async()=>{var first=await Q(BrowseLevel.Models,count:1);True(first.HasMore);Equal("MODEL_A",first.Rows[0].Text("model_name"));Equal("MODEL_B",(await Q(BrowseLevel.Models,count:1,cursor:first.Cursor)).Rows[0].Text("model_name"));});
        await Check("lots without inspections retained",async()=>Equal(4,(await Q(BrowseLevel.Lots)).Rows.Count));
        await Check("LOT and model searches have explicit distinct targets",async()=>
        {
            Equal(0,(await Q(BrowseLevel.Lots,new(){Text="MODEL_A",Target=SearchTarget.Lot})).Rows.Count);
            Equal(3,(await Q(BrowseLevel.Lots,new(){Text="MODEL_A",Target=SearchTarget.Model})).Rows.Count);
            Equal(1,(await Q(BrowseLevel.Lots,new(){Text="LOT_A",Target=SearchTarget.Lot})).Rows.Count);
        });
        await Check("whole scope counts and panel groups include PCs and retests exactly once",async()=>
        {
            var summary=await repo.SummaryAsync(p,db1,new(),default);
            Equal(4L,summary.Lots);Equal(5L,summary.Inspections);Equal(5L,summary.Defects);Equal(3L,summary.PanelGroups);
            var panel=summary.Panels.Single(x=>x.Text("model_name")=="MODEL_A"&&x.Text("machine_id")=="EQ09");
            Equal(3L,panel.Long("inspection_count"));Equal(3L,panel.Long("defect_count"));Equal(2L,panel.Long("image_count"));
            Equal(1L,panel.Long("vms_ng_count"));Equal(1L,panel.Long("vms_pass_count"));
        });
        await Check("panel summary obeys cause and VMS filters",async()=>
        {
            var summary=await repo.SummaryAsync(p,db1,new(){CauseCode=4001,VmsState=1},default);
            Equal(1L,summary.Defects);Equal(1L,summary.Inspections);Equal(1L,summary.Panels.Single().Long("defect_count"));
        });
        await Check("LOT counts retain empty LOTs and physical identities",async()=>
        {
            var summary=await repo.SummaryAsync(p,db1,new(),default);
            Equal(4,summary.LotCounts.Count);Equal(0L,summary.LotCounts.Single(x=>x.Text("lot_id_name")=="NO_INSPECTION").Long("defect_count"));
            Equal(4L,summary.LotCounts.Single(x=>x.Text("lot_id_name")=="LOT_A").Long("defect_count"));
            Equal(4,summary.LotCounts.Select(x=>x.Text("lot_id")).Distinct().Count());
            Equal("1",summary.Panels.Single(x=>x.Text("model_name")=="MODEL_A"&&x.Text("machine_id")=="EQ09").Text("lot_id"));
        });
        await Check("exact group machine does not broaden EQ0 to EQ09 and EQ10",async()=>
        {
            Equal(4,(await Q(BrowseLevel.Inspections,new(){Machine="EQ0"})).Rows.Count);
            Equal(0,(await Q(BrowseLevel.Inspections,new(){Machine="EQ0",MachineExact=true})).Rows.Count);
        });
        await Check("zero-defect panels remain in summary",async()=>
        {
            try
            {
                await Admin(p,db1,"INSERT INTO inspection(inspection,layer,machine_id,pc_no,panel_no,time,status) VALUES(888,1,'EQ09',1,9,now(),'COMPLETED')");
                var summary=await repo.SummaryAsync(p,db1,new(){Model="MODEL_A"},default);
                Equal(0L,summary.Panels.Single(x=>x.Long("panel_no")==9).Long("defect_count"));Equal(5L,summary.Inspections);
            }
            finally{await Admin(p,db1,"DELETE FROM inspection WHERE inspection=888");}
        });
        await Check("literal wildcard search does not broaden",async()=>Equal(1,(await Q(BrowseLevel.Lots,new(){Text="_%"})).Rows.Count));
        await Check("SQL injection remains a search string",async()=>Equal(0,(await Q(BrowseLevel.Lots,new(){Text="'; DROP TABLE defects; --"})).Rows.Count));
        await Check("model prevents same layer name mixing",async()=>Equal(1,(await Q(BrowseLevel.Layers,new(){Model="MODEL_A"})).Rows.Count));
        await Check("PC and machine histories stay separate",async()=>Equal(5,(await Q(BrowseLevel.Inspections)).Rows.Count));
        await Check("latest keeps other PCs and machines",async()=>Equal(4,(await Q(BrowseLevel.Inspections,new(){LatestOnly=true})).Rows.Count));
        await Check("date filter uses inspection time not lot time",async()=>Equal(4,(await Q(BrowseLevel.Inspections,new(){From=new(2026,11,3),UntilExclusive=new(2026,11,4)})).Rows.Count));
        await Check("half-open date excludes next day",async()=>Equal(1,(await Q(BrowseLevel.Inspections,new(){From=new(2026,11,2),UntilExclusive=new(2026,11,3)})).Rows.Count));
        await Check("defect page cursor no duplicates",async()=>{var all=new List<string>();string? cursor=null;do{var page=await Q(BrowseLevel.Defects,count:2,cursor:cursor);all.AddRange(page.Rows.Select(x=>x.Text("defects")));cursor=page.Cursor;}while(cursor is not null);Equal(5,all.Count);Equal(5,all.Distinct().Count());});
        await Check("metadata list never selects image bytes",async()=>{var page=await Q(BrowseLevel.Defects);True(page.Rows.All(x=>!x.Values.ContainsKey("image")));True(page.Rows[0]["d_attr"] is null);});
        await Check("detail preserves NULL and actual zero",async()=>{var a=await repo.DetailAsync(p,new(p.Identity,db1,"defects","1"),default);var b=await repo.DetailAsync(p,new(p.Identity,db1,"defects","2"),default);True(a["defect_size"] is null);Equal(0.0,b.Number("defect_size"));});
        await Check("image lookup uses physical source DB",async()=>{await Admin(p,db2,"UPDATE defects SET image=decode('aabbccdd','hex') WHERE defects=1");var a=await repo.ImageAsync(p,new(p.Identity,db1,"defects","1"),default);var b=await repo.ImageAsync(p,new(p.Identity,db2,"defects","1"),default);True(a!.Bytes[0]!=b!.Bytes[0]);Equal(2,a.Width);});
        await Check("missing image is not a decode error",async()=>True(await repo.ImageAsync(p,new(p.Identity,db1,"defects","2"),default) is null));
        await Check("image-present filter",async()=>Equal(2,(await Q(BrowseLevel.Defects,new(){Images=ImageFilter.Present})).Rows.Count));
        await Check("zero is a valid size filter",async()=>Equal(1,(await Q(BrowseLevel.Defects,new(){MinimumSize=0,MaximumSize=0})).Rows.Count));
        await Check("cause code and VMS state filter",async()=>Equal(1,(await Q(BrowseLevel.Defects,new(){CauseCode=4001,VmsState=1})).Rows.Count));
        await Check("incomplete inspection is visible",async()=>Equal("START",(await Q(BrowseLevel.Inspections,new(){Machine="EQ10"})).Rows.Single().Text("inspection_status")));
        await Check("exact inspection confines defects",async()=>Equal("3",(await Q(BrowseLevel.Defects,new(){InspectionId="3"})).Rows.Single().Text("defects")));
        await Check("legacy schema lists safely with missing-field markers",async()=>{var page=await Q(BrowseLevel.Defects,db:"aoi_legacy");Equal(5,page.Rows.Count);True(page.Rows[0].Text("missing_fields").Contains("defect_size"));});
        await Check("legacy optional filter yields no invented data",async()=>Equal(0,(await Q(BrowseLevel.Defects,new(){MinimumSize=0},db:"aoi_legacy")).Rows.Count));
        await Check("legacy raw image dimensions remain unknown",async()=>{var image=await repo.ImageAsync(p,new(p.Identity,"aoi_legacy","defects","1"),default);Equal(0,image!.Width);});
        await Check("invalid table name rejected",async()=>{try{await repo.DetailAsync(p,new(p.Identity,db1,"defects;DROP","1"),default);throw new Exception("Expected ArgumentException");}catch(ArgumentException){}});
        await Check("read-only startup setting rejects INSERT",async()=>{await using var c=new NpgsqlConnection(ExplorerRepository.ConnectionString(p,db1));await c.OpenAsync();await using var cmd=new NpgsqlCommand("INSERT INTO lot_id(lot_id_name,model_name) VALUES('SHOULD_NOT_EXIST','x')",c);try{await cmd.ExecuteNonQueryAsync();throw new Exception("write unexpectedly allowed");}catch(PostgresException ex){Equal("25006",ex.SqlState);}});
        await Check("cancelled query does not succeed",async()=>{using var cts=new CancellationTokenSource();cts.Cancel();try{await repo.QueryAsync(p,db1,BrowseLevel.Defects,new(),10,null,cts.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}});
        await Check("model geometry from separate model DB",async()=>Equal(4,(await repo.GeometryAsync(p,"MODEL_A","L1",default)).TotalUnits));
        await Check("ERT reconstruction preserves selected record only",async()=>{var rows=(await Q(BrowseLevel.Defects,new(){InspectionId="2"})).Rows;var ert=ErtExporter.Render("C:\\test","MODEL_A","LOT_A","L1",1,new(2026,11,3),"VMS_COMPLETED",1,await repo.GeometryAsync(p,"MODEL_A","L1",default),rows);True(ert.Text.StartsWith("4.3.1, 1,"));True(ert.Text.Contains("11, 21"));});
        await Check("all-result CSV streams across overlapping IDs",async()=>{async IAsyncEnumerable<ResultRow> All(){foreach(var db in new[]{db1,db2}){string? cur=null;do{var page=await Q(BrowseLevel.Defects,count:2,cursor:cur,db:db);foreach(var row in page.Rows)yield return row;cur=page.Cursor;}while(cur is not null);}}string path=Path.Combine(output,"integration-"+Guid.NewGuid().ToString("N")+".csv");Equal(10L,await ExportService.CsvAsync(path,All(),default));});
        // UUID schema is isolated and validates older pre-BIGSERIAL deployments without changing any real DB.
        try{await Admin(p,"postgres","CREATE DATABASE aoi_uuid");}catch(PostgresException ex)when(ex.SqlState=="42P04"){}
        await Admin(p,"aoi_uuid","""
DROP SCHEMA public CASCADE;CREATE SCHEMA public;
CREATE TABLE lot_id(lot_id uuid PRIMARY KEY,lot_id_name text,model_name text,time timestamp,status text);
CREATE TABLE layer(layer uuid PRIMARY KEY,lot_id uuid,layer_name text,model_name text,vms_status text);
CREATE TABLE inspection(inspection uuid PRIMARY KEY,layer uuid,machine_id text,pc_no int,panel_no int,time timestamp,status text);
CREATE TABLE defects(defects uuid PRIMARY KEY,inspection uuid,unit_idx int,unit_x int,unit_y int,defect_type int,vms_state int,vms_code int,image bytea);
""");
        await Admin(p,"aoi_uuid","INSERT INTO lot_id VALUES('00000000-0000-0000-0000-000000000001','UUID_LOT','UUID_MODEL',now(),'ACTIVE');");
        await Check("UUID primary key lookup supported",async()=>{var first=await Q(BrowseLevel.Lots,db:"aoi_uuid");var detail=await repo.DetailAsync(p,first.Rows[0].Key(p.Identity,BrowseLevel.Lots),default);Equal("UUID_LOT",detail.Text("lot_id_name"));});
        await Check("legacy and empty UUID summaries stay valid",async()=>
        {Equal(5L,(await repo.SummaryAsync(p,"aoi_legacy",new(),default)).Defects);Equal(0L,(await repo.SummaryAsync(p,"aoi_uuid",new(),default)).Defects);});
        await Check("export snapshot pins metadata image and count across concurrent updates",async()=>
        {
            try
            {
                await repo.WithSnapshotAsync(p,db1,async(snapshot,captured,ct)=>
                {
                    True(captured<=DateTimeOffset.UtcNow.AddSeconds(2));
                    await Admin(p,db1,"UPDATE defects SET image=decode('aabbccdd','hex'),vms_state=2 WHERE defects=1;INSERT INTO defects(defects,inspection,unit_idx) VALUES(999,2,999)");
                    var rows=(await snapshot.QueryAsync(p,db1,BrowseLevel.Defects,new(),250,null,ct)).Rows;
                    Equal(5,rows.Count);Equal(0L,rows.Single(x=>x.Long("defects")==1).Long("vms_state"));
                    Equal((byte)0,(await snapshot.ImageAsync(p,new(p.Identity,db1,"defects","1"),ct))!.Bytes[0]);
                    Equal(5L,(await snapshot.SummaryAsync(p,db1,new(),ct)).Defects);
                },default);
                Equal(6,(await Q(BrowseLevel.Defects)).Rows.Count);
            }
            finally{await Admin(p,db1,"DELETE FROM defects WHERE defects=999;UPDATE defects SET image=decode('00010203','hex'),vms_state=0 WHERE defects=1");}
        });
        await Check("streaming snapshot keeps late image bytes and excludes new rows",async()=>
        {
            try
            {
                int count=0;DateTimeOffset? captured=null;
                await foreach(var row in repo.StreamAsync(p,db1,BrowseLevel.Defects,new(),true,t=>captured=t,default))
                {
                    if(++count==1)await Admin(p,db1,"UPDATE defects SET image=decode('aabbccdd','hex') WHERE defects=1;INSERT INTO defects(defects,inspection,unit_idx) VALUES(999,2,999)");
                    if(row.Long("defects")==1)Equal((byte)0,row.SnapshotImage!.Bytes[0]);
                }
                Equal(5,count);True(captured.HasValue);
            }
            finally{await Admin(p,db1,"DELETE FROM defects WHERE defects=999;UPDATE defects SET image=decode('00010203','hex') WHERE defects=1");}
        });
        await Check("cancelled streaming releases snapshot and permits subsequent query",async()=>
        {
            using var cts=new CancellationTokenSource();
            try{await foreach(var row in repo.StreamAsync(p,db1,BrowseLevel.Defects,new(),false,null,cts.Token)){cts.Cancel();}throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
            Equal(5,(await Q(BrowseLevel.Defects)).Rows.Count);
        });
        await Check("selected-image streaming exports exact selected keys",async()=>
        {
            var ids=new List<string>();await foreach(var row in repo.StreamAsync(p,db1,BrowseLevel.Defects,new(),true,null,default,new HashSet<string>{"1","3"})){ids.Add(row.Text("defects"));True(row.SnapshotImage is not null);}
            Equal("3,1",string.Join(",",ids));
            try{await foreach(var row in repo.StreamAsync(p,db1,BrowseLevel.Defects,new(),true,null,default,new HashSet<string>{"999"})){}throw new Exception("Expected missing selection error");}catch(InvalidDataException){}
        });
        await Check("ERT snapshot count and rows stay consistent during concurrent insert",async()=>
        {
            try
            {
                await repo.StreamInspectionAsync(p,db1,"2",async(count,rows,ct)=>
                {
                    Equal(1L,count);await Admin(p,db1,"INSERT INTO defects(defects,inspection,unit_idx) VALUES(999,2,999)");
                    int actual=0;await foreach(var row in rows.WithCancellation(ct)){actual++;True(!row.Values.ContainsKey("image"));}Equal(1,actual);
                },default);
            }
            finally{await Admin(p,db1,"DELETE FROM defects WHERE defects=999");}
        });
        await Check("connection pools stay bounded across changing profiles",async()=>
        {
            for(int timeout=10;timeout<24;timeout++)Equal(2,(await repo.QueryAsync(p with{TimeoutSeconds=timeout},db1,BrowseLevel.Models,new(),10,null,default)).Rows.Count);
            Equal(2,(await repo.QueryAsync(p,db1,BrowseLevel.Models,new(),10,null,default)).Rows.Count);
        });
        await Check("panel payload stream pins metadata and image bytes before concurrent changes",async()=>
        {
            try
            {
                await repo.StreamInspectionPayloadsAsync(p,db1,"1",async(count,source,ct)=>
                {
                    Equal(1L,count);await Admin(p,db1,"UPDATE defects SET image=decode('aabbccdd','hex') WHERE defects=1;INSERT INTO defects(defects,inspection,unit_idx) VALUES(999,1,999)");
                    int actual=0;await foreach(var row in source.WithCancellation(ct))
                    {actual++;Equal("1",row.Text("defects"));Equal((byte)0,row.SnapshotImage!.Bytes[0]);True(row.SnapshotImageError is null&&!row.Values.Keys.Any(x=>x=="image"||x.StartsWith("__nas_")));}Equal(1,actual);
                },default);
            }
            finally{await Admin(p,db1,"DELETE FROM defects WHERE defects=999;UPDATE defects SET image=decode('00010203','hex') WHERE defects=1");}
        });
        await Check("same database wait can be cancelled or timed out without leaking the connection budget",async()=>
        {
            await repo.WithSnapshotAsync(p,db1,async(scoped,_,ct)=>
            {
                using var cancel=new CancellationTokenSource(70);
                try{await repo.QueryAsync(p,db1,BrowseLevel.Models,new(),10,null,cancel.Token);throw new Exception("Expected cancelled lease wait");}catch(OperationCanceledException){}
                var clock=Stopwatch.StartNew();
                try{await repo.QueryAsync(p with{TimeoutSeconds=1},db1,BrowseLevel.Models,new(),10,null,ct);throw new Exception("Expected bounded lease wait");}catch(TimeoutException){}
                True(clock.Elapsed<TimeSpan.FromSeconds(3));Equal(2,(await scoped.QueryAsync(p,db1,BrowseLevel.Models,new(),10,null,ct)).Rows.Count);
            },default);
            Equal(2,(await Q(BrowseLevel.Models)).Rows.Count);
        });
        await Check("disposing idle pools does not break an active snapshot or its next query",async()=>
        {
            await repo.WithSnapshotAsync(p,db1,async(scoped,_,ct)=>
            {await repo.ReleaseIdleConnectionsAsync();Equal(5L,(await scoped.SummaryAsync(p,db1,new(),ct)).Defects);},default);
            Equal(2,(await Q(BrowseLevel.Models)).Rows.Count);
        });
        await Check("model lookup reuses an existing snapshot in the same database",async()=>
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await repo.WithSnapshotAsync(p,p.ModelDatabase,async(scoped,_,ct)=>
            {True((await scoped.GeometryAsync(p,"MODEL_A","L1",ct)).BlockX.Length>0);},deadline.Token);
        });
        async Task<string> ImageFile(ResultRow row,ImagePayload image,string folder,CancellationToken ct)
        {Directory.CreateDirectory(folder);string path=Path.Combine(folder,"defect_"+row.Text("defects")+".bin");await File.WriteAllBytesAsync(path,image.Bytes,ct);return path;}
        string BundleRoot(string name)=>Path.Combine(output,"bundle-"+name+"-"+Guid.NewGuid().ToString("N"));
        await Check("Handler-style bundle expands a LOT into full panels and preserves reinspection collisions",async()=>
        {
            string root=BundleRoot("lot");var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){LotId="1"})},new(true,true,true,true,IncludeHistory:true),ImageFile,null,default);
            Equal(4L,result.Panels);Equal(4L,result.Defects);Equal(4L,result.ErtFiles);Equal(2L,result.Images);Equal(2L,result.MissingImages);Equal(0L,result.Failures);
            True(File.Exists(Path.Combine(root,"EQ09","MODEL_A","LOT_A","L1","001.ERT")));
            Equal(4,Directory.GetFiles(root,"*.ERT",SearchOption.AllDirectories).Length);True(Directory.GetDirectories(root,"_검사이력",SearchOption.AllDirectories).Length>0);
            True(!Directory.GetDirectories(root,"*PC*",SearchOption.AllDirectories).Any());
            Equal(5,(await File.ReadAllLinesAsync(Path.Combine(root,"패널별불량수.csv"))).Length);
        });
        await Check("query export resolves matching LOTs before expanding complete panel contents",async()=>
        {
            string root=BundleRoot("search");var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){Images=ImageFilter.Present},BrowseLevel.Lots,true)},new(false,false,true,false,IncludeHistory:true),ImageFile,null,default);
            Equal(4L,result.Panels);Equal(4L,result.Defects);True(!Directory.Exists(Path.Combine(root,"EQ09","MODEL_B")));
        });
        await Check("compact LOT selects latest physical panels and keeps provenance on this PC",async()=>
        {
            string root=BundleRoot("compact");var updates=new List<BundleProgress>();
            var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){LotId="1"})},new(true,false,false,false),ImageFile,null,default,updates.Add);
            Equal(2L,result.Panels);Equal(2L,result.ErtFiles);Equal(0L,result.Failures);
            True(Directory.GetFiles(root,"*",SearchOption.AllDirectories).All(x=>Path.GetExtension(x).Equals(".ERT",StringComparison.OrdinalIgnoreCase)));
            True(!Directory.GetDirectories(root,"_검사이력",SearchOption.AllDirectories).Any());
            True(File.Exists(Path.Combine(result.DiagnosticFolder,"내보내기결과.json"))&&!Path.GetFullPath(result.DiagnosticFolder).StartsWith(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase));
            True(updates.Any(x=>x.PlannedPanels==2)&&updates.Any(x=>x.ProcessedPanels==2));
        });
        await Check("query inspection rows select latest while explicit older IDs remain exportable",async()=>
        {
            string root=BundleRoot("latest-query");
            var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){LotId="1"},BrowseLevel.Inspections,true)},new(false,false,true,false),ImageFile,null,default);
            Equal(2L,result.Panels);
            root=BundleRoot("explicit-history");
            result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="1"}),new BundleScope(db1,new(){InspectionId="2"})},new(true,false,false,false),ImageFile,null,default);
            Equal(2L,result.Panels);Equal(2L,result.ErtFiles);
        });
        await Check("declining plan confirmation creates no operator files and holds no snapshot",async()=>
        {
            string root=BundleRoot("declined");string diagnostic="";long planned=0;
            try
            {
                await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){LotId="1"})},new(true,true,true,true),ImageFile,null,default,confirmPlan:async(plan,ct)=>
                {diagnostic=plan.DiagnosticFolder;planned=plan.PlannedPanels;await Admin(p,db1,"BEGIN;LOCK TABLE defects IN ACCESS EXCLUSIVE MODE NOWAIT;ROLLBACK;");return false;});
                throw new Exception("Expected declined export cancellation");
            }
            catch(OperationCanceledException){}
            Equal(2L,planned);True(!Directory.GetFiles(root,"*",SearchOption.AllDirectories).Any());
            using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(diagnostic,"내보내기결과.json")));Equal("cancelled",receipt.RootElement.GetProperty("status").GetString());
        });
        await Check("one defect selection exports its whole panel and deduplicates checked defects",async()=>
        {
            var row=(await Q(BrowseLevel.Defects,new(){InspectionId="2"})).Rows.Single();
            var f=InspectionBundleExporter.SelectionFilter(new(){CauseCode=4001,VmsState=1},BrowseLevel.Defects,row);
            string root=BundleRoot("panel");var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,f),new BundleScope(db1,f)},new(true,false,true,false),ImageFile,null,default);
            Equal(1L,result.Panels);Equal(1L,result.Defects);Equal(1L,result.ErtFiles);
        });
        await Check("missing ERT model does not block images and defect-count output",async()=>
        {
            string root=BundleRoot("missing-model");var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="5"})},new(true,true,true,false),ImageFile,null,default);
            Equal("partial_failure",result.Status);Equal(1L,result.Panels);Equal(1L,result.Defects);Equal(1L,result.Failures);Equal(0L,result.ErtFiles);True(File.Exists(Path.Combine(root,"패널별불량수.csv")));
        });
        await Check("SEMCO folder ordering and original safe names stay readable",()=>
        {
            var row=new ResultRow{Values=new(){["machine_id"]="EQ_09",["model_name"]="p2411-004d101",["lot_id_name"]="agsd",["layer_name"]="L1-T-SHORT3.1-0200"}};
            Equal(Path.Combine("root","EQ_09","p2411-004d101","L1-T-SHORT3.1-0200","agsd"),InspectionBundleExporter.LayerFolder("root",row,true));
            Equal("p2411-004d101",InspectionBundleExporter.Component("p2411-004d101"));True(!InspectionBundleExporter.Component("../bad").Contains('/'));
        });
        await Check("bundle cancellation writes a cancelled receipt and releases the snapshot",async()=>
        {
            string root=BundleRoot("cancel");string diagnostic="";using var cts=new CancellationTokenSource();
            try
            {
                await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="1"})},new(false,true,true,false,IncludeHistory:true),async(row,image,folder,ct)=>{cts.Cancel();await Task.Delay(1,ct);return "unreachable";},null,cts.Token,confirmPlan:(plan,_)=>{diagnostic=plan.DiagnosticFolder;return Task.FromResult(true);});
                throw new Exception("Expected cancellation");
            }
            catch(OperationCanceledException){}
            using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(diagnostic,"내보내기결과.json")));
            Equal("cancelled",receipt.RootElement.GetProperty("status").GetString());Equal(5,(await Q(BrowseLevel.Defects)).Rows.Count);
            string spool=receipt.RootElement.GetProperty("retainedSpoolDirectory").GetString()!;
            True(Path.IsPathFullyQualified(spool)&&Directory.Exists(spool)&&!Path.GetFullPath(spool).StartsWith(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase));
        });
        await Check("bundle fixes inspection IDs then releases each panel snapshot before file conversion",async()=>
        {
            try
            {
                bool changed=false;string root=BundleRoot("live");
                var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){LotId="1"})},new(false,true,true,false,IncludeHistory:true),async(row,image,folder,ct)=>
                {
                    if(!changed)
                    {
                        changed=true;
                        // This would fail while any bundle snapshot retained an ACCESS SHARE lock.
                        await Admin(p,db1,"BEGIN;LOCK TABLE defects IN ACCESS EXCLUSIVE MODE NOWAIT;ROLLBACK;");
                        await Admin(p,db1,"UPDATE defects SET image=decode('aabbccdd','hex') WHERE defects=1;INSERT INTO defects(defects,inspection,unit_idx) VALUES(999,1,999);INSERT INTO inspection(inspection,layer,machine_id,pc_no,panel_no,time,status) VALUES(998,1,'EQ09',0,998,now(),'COMPLETED');INSERT INTO defects(defects,inspection,unit_idx) VALUES(998,998,998)");
                    }
                    if(row.Text("defects")=="1")Equal((byte)0xaa,image.Bytes[0]);return await ImageFile(row,image,folder,ct);
                },null,default);
                Equal(5L,result.Defects);Equal(4L,result.Panels);Equal(2L,result.Images);Equal(7,(await Q(BrowseLevel.Defects)).Rows.Count);
            }
            finally{await Admin(p,db1,"DELETE FROM defects WHERE defects IN(998,999);DELETE FROM inspection WHERE inspection=998;UPDATE defects SET image=decode('00010203','hex') WHERE defects=1");}
        });
        await Check("zero-defect panel bundle still writes ERT and a zero count row",async()=>
        {
            try
            {
                await Admin(p,db1,"INSERT INTO inspection(inspection,layer,machine_id,pc_no,panel_no,time,status) VALUES(887,1,'EQ09',0,9,now(),'COMPLETED')");
                string root=BundleRoot("zero");var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="887"})},new(true,true,true,false),ImageFile,null,default);
                Equal(1L,result.Panels);Equal(0L,result.Defects);Equal(1L,result.ErtFiles);Equal(0L,result.Failures);Equal(2,(await File.ReadAllLinesAsync(Path.Combine(root,"패널별불량수.csv"))).Length);
            }
            finally{await Admin(p,db1,"DELETE FROM inspection WHERE inspection=887");}
        });
        await Check("same face with different Layer recipes and resolutions does not mix",async()=>
        {
            try
            {
                await Admin(p,db1,"INSERT INTO layer(layer,layer_name,model_name,lot_id) VALUES(777,'L1-SHORT1.0-0150','MODEL_A',1),(778,'L1-T-SHORT3.1-0200','MODEL_A',1);INSERT INTO inspection(inspection,layer,machine_id,pc_no,panel_no,time,status) VALUES(777,777,'EQ09',0,1,now(),'COMPLETED'),(778,778,'EQ09',0,1,now(),'COMPLETED')");
                Equal("777",(await Q(BrowseLevel.Inspections,new(){LotId="1",Layer="L1-SHORT1.0-0150"})).Rows.Single().Text("inspection"));
                Equal("778",(await Q(BrowseLevel.Inspections,new(){LotId="1",Layer="L1-T-SHORT3.1-0200"})).Rows.Single().Text("inspection"));
            }
            finally{await Admin(p,db1,"DELETE FROM inspection WHERE inspection IN(777,778);DELETE FROM layer WHERE layer IN(777,778)");}
        });
        await Check("NO_DB optional Bar result and Defect Point are separate from other exports",async()=>
        {
            try
            {
                await Admin(p,db1,"UPDATE inspection SET disposed_bar_mask=0,handler_tact_sec=12.3,fov_count=16 WHERE inspection=1");
                foreach(var choice in new[]{(Bar:true,Point:false),(Bar:false,Point:true),(Bar:true,Point:true)})
                {
                    string root=BundleRoot("no-db-options");
                    var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="1"})},new(false,false,false,false,BarResult:choice.Bar,DefectPoint:choice.Point),ImageFile,null,default);
                    Equal(0L,result.Failures);Equal(choice.Bar?1L:0L,result.BarResultFiles);Equal(choice.Point?1L:0L,result.DefectPointFiles);
                    var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories);Equal((choice.Bar?1:0)+(choice.Point?1:0),files.Length);
                    True(files.All(x=>Path.GetFileName(x).StartsWith("Bar_result_",StringComparison.Ordinal)||Path.GetFileName(x).StartsWith("Defect Point_",StringComparison.Ordinal)));
                    if(choice.Point)
                    {
                        var lines=await File.ReadAllLinesAsync(files.Single(x=>Path.GetFileName(x).StartsWith("Defect Point_",StringComparison.Ordinal)));
                        Equal(2,lines.Length);var fields=lines[1].Split(',').Select(x=>x.Trim()).ToArray();Equal(16,fields.Length);Equal("",fields[7]);Equal("1",fields[8]);Equal("",fields[9]);Equal("",fields[10]);Equal("16",fields[13]);Equal("12.3",fields[15]);
                    }
                }
            }
            finally{await Admin(p,db1,"UPDATE inspection SET disposed_bar_mask=NULL,handler_tact_sec=NULL,fov_count=1 WHERE inspection=1");}
        });
        await Check("Defect Point Handler tact is not replaced by unrelated scan time",async()=>
        {
            try
            {
                await Admin(p,db1,"UPDATE inspection SET handler_tact_sec=NULL,tact_ms=12345 WHERE inspection=1");
                string root=BundleRoot("count-scan-only");
                var result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="1"})},new(false,false,false,false,DefectPoint:true),ImageFile,null,default);
                Equal(0L,result.Failures);string path=Directory.GetFiles(root,"Defect Point_*.txt",SearchOption.AllDirectories).Single();
                foreach(string line in await File.ReadAllLinesAsync(path))Equal("",line.Split(',')[15].Trim());
                True((await File.ReadAllTextAsync(Path.Combine(result.DiagnosticFolder,"처리내역.jsonl"))).Contains("resolutionSource"));
            }
            finally{await Admin(p,db1,"UPDATE inspection SET handler_tact_sec=NULL,tact_ms=NULL WHERE inspection=1");}
        });
        await Check("Defect Point zero-defect missing-model panel remains explicit and produces no invented counters",async()=>
        {
            try
            {
                await Admin(p,db1,"INSERT INTO inspection(inspection,layer,machine_id,pc_no,panel_no,time,status) VALUES(889,1,'EQ09',0,9,now(),'COMPLETED')");
                string root=BundleRoot("count-zero");
                var result=await InspectionBundleExporter.ExportAsync(repo,p with{ModelDatabase="missing_model_for_count"},root,new[]{new BundleScope(db1,new(){InspectionId="889"})},new(false,false,false,false,DefectPoint:true),ImageFile,null,default);
                Equal(0L,result.Failures);Equal(1L,result.DefectPointFiles);Equal(0L,result.Defects);True(result.Unrecoverable>0);
                string path=Directory.GetFiles(root,"Defect Point_*.txt",SearchOption.AllDirectories).Single();var values=(await File.ReadAllLinesAsync(path))[1].Split(',').Select(x=>x.Trim()).ToArray();
                Equal("0",values[8]);foreach(int i in new[]{7,9,10,11,12,13,14,15})Equal("",values[i]);
            }
            finally{await Admin(p,db1,"DELETE FROM inspection WHERE inspection=889");}
        });
        await Check("Handler sidecar-only bundle distinguishes recorded zero from NULL and reports reconstruction limits",async()=>
        {
            try
            {
                await Admin(p,db1,"UPDATE inspection SET disposed_bar_mask=0 WHERE inspection=1");
                string root=BundleRoot("handler-zero");
                var result=await InspectionBundleExporter.ExportAsync(repo,p with{ModelDatabase="missing_model_must_not_be_queried"},root,new[]{new BundleScope(db1,new(){InspectionId="1"})},new(false,false,false,false,HandlerFiles:true,BarResult:true),ImageFile,null,default);
                Equal(0L,result.Failures);Equal(0L,result.Unrecoverable);Equal("completed",result.Status);
                string layer=Path.Combine(root,"EQ09","MODEL_A","LOT_A","L1");
                Equal("0",(await File.ReadAllTextAsync(Path.Combine(layer,"1","block_dispose.txt"))).Trim());
                Equal("Panel No,MaskingBar",(await File.ReadAllTextAsync(Path.Combine(layer,"Bar_result_LOT_A.txt"))).Trim());
                await Admin(p,db1,"UPDATE inspection SET disposed_bar_mask=NULL WHERE inspection=1");
                root=BundleRoot("handler-null");result=await InspectionBundleExporter.ExportAsync(repo,p,root,new[]{new BundleScope(db1,new(){InspectionId="1"})},new(false,false,false,false,HandlerFiles:true,BarResult:true),ImageFile,null,default);
                Equal(0L,result.Failures);True(result.Unrecoverable>0);True(!Directory.GetFiles(root,"block_dispose.txt",SearchOption.AllDirectories).Any());
                using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.DiagnosticFolder,"내보내기결과.json")));True(receipt.RootElement.GetProperty("unrecoverable").GetInt64()>0);
            }
            finally{await Admin(p,db1,"UPDATE inspection SET disposed_bar_mask=NULL WHERE inspection=1");}
        });
    }
    private static async Task StressAsync(string output)
    {
        DateTime korea=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime;
        if(korea.Date==new DateTime(2026,11,4)&&korea.Hour<22)throw new Exception("Heavy tests are prohibited before 2026-11-04 22:00 KST.");
        var p=TestProfile();var repo=new ExplorerRepository();
        await Check("200k defect paging and streaming export",async()=>
        {
            await Admin(p,"aoi_20990102","INSERT INTO defects(defects,inspection,unit_idx,unit_x,unit_y,defect_type,vms_state,vms_code) SELECT n,2,n%1000,1,1,1,0,0 FROM generate_series(100,200099) n ON CONFLICT DO NOTHING;");
            var sw=Stopwatch.StartNew();long total=0;string? cursor=null;
            async IAsyncEnumerable<ResultRow> All(){do{var page=await repo.QueryAsync(p,"aoi_20990102",BrowseLevel.Defects,new(),500,cursor,default);foreach(var row in page.Rows){total++;yield return row;}cursor=page.Cursor;}while(cursor is not null);}
            long count=await ExportService.CsvAsync(Path.Combine(output,"stress-"+Guid.NewGuid().ToString("N")+".csv"),All(),default);True(count>=200000);Console.WriteLine($"Stress rows={total}; seconds={sw.Elapsed.TotalSeconds:F2}; workingSet={Environment.WorkingSet/1024/1024}MB");
        });
        await Check("one million rows stream with bounded client memory",async()=>
        {
            await Admin(p,"aoi_20990102","INSERT INTO defects(defects,inspection,unit_idx,unit_x,unit_y,defect_type,vms_state,vms_code) SELECT n,2,n%1000,1,1,1,0,0 FROM generate_series(200100,1000099) n ON CONFLICT DO NOTHING;");
            long count=0;string? cursor=null;var sw=Stopwatch.StartNew();long peak=0;
            do{var page=await repo.QueryAsync(p,"aoi_20990102",BrowseLevel.Defects,new(),1000,cursor,default);count+=page.Rows.Count;cursor=page.Cursor;peak=Math.Max(peak,Environment.WorkingSet);}while(cursor is not null);
            True(count>=1000000);True(peak<768L*1024*1024);
            await File.WriteAllTextAsync(Path.Combine(output,"million-row-metrics.json"),JsonSerializer.Serialize(new{rows=count,seconds=sw.Elapsed.TotalSeconds,peakClientWorkingSetBytes=peak}));
        });
        await Check("cancellation releases a pooled connection for next query",async()=>
        {
            await using(var c=new NpgsqlConnection(ExplorerRepository.ConnectionString(p,"aoi_20990102")))
            {
                await c.OpenAsync();await using var command=new NpgsqlCommand("SELECT pg_sleep(10)",c);using var cts=new CancellationTokenSource(100);
                try{await command.ExecuteScalarAsync(cts.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
            }
            Equal(10,(await repo.QueryAsync(p,"aoi_20990102",BrowseLevel.Defects,new(),10,null,default)).Rows.Count);
        });
        await Check("repeated page navigation does not retain old pages",async()=>
        {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long start=GC.GetTotalMemory(true);
            for(int i=0;i<200;i++)await repo.QueryAsync(p,"aoi_20990102",BrowseLevel.Defects,new(),250,(100+i*250).ToString(),default);
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long delta=GC.GetTotalMemory(true)-start;True(delta<64L*1024*1024);
        });
    }
    private static async Task RecoveredAsync(string output)
    {
        DateTime korea=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime;
        if(korea.Date==new DateTime(2026,11,4)&&korea.Hour<22)throw new Exception("Recovered dump verification is prohibited before 22:00 KST.");
        var p=TestProfile();string db=Environment.GetEnvironmentVariable("NAS_RECOVERED_DATABASE")??throw new Exception("Recovered database missing");
        if(!db.StartsWith("nas_recovered_",StringComparison.Ordinal))throw new Exception("Only a private restored database is permitted.");
        var repo=new ExplorerRepository();var baseline=new Dictionary<string,long>();
        await using(var c=new NpgsqlConnection(new NpgsqlConnectionStringBuilder{Host=p.Host,Port=p.Port,Username=p.Username,Password=p.Password,Database=db,Pooling=false,CommandTimeout=120}.ConnectionString))
        {
            await c.OpenAsync();foreach(string table in new[]{"lot_id","layer","inspection","defects"})
            {await using var cmd=new NpgsqlCommand($"SELECT COUNT(*) FROM public.{table}",c);baseline[table]=Convert.ToInt64(await cmd.ExecuteScalarAsync());}
            await Check("restored dump has no subscriptions or publications",async()=>
            {await using var cmd=new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_subscription WHERE subdbid=(SELECT oid FROM pg_database WHERE datname=current_database()))+(SELECT count(*) FROM pg_publication)",c);Equal(0L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));});
        }
        var metrics=new List<object>();
        await Check("recovered defect-filtered inspection counts do not repeat per joined defect",async()=>
        {
            var clock=Stopwatch.StartNew();var page=await repo.QueryAsync(p,db,BrowseLevel.Inspections,new(){Images=ImageFilter.Present},1000,null,default);
            Equal(78,page.Rows.Count);Equal(baseline["defects"],page.Rows.Sum(x=>x.Long("stored_defect_count")??0));
            var summary=await repo.SummaryAsync(p,db,new(){Images=ImageFilter.Present},default);Equal(78L,summary.Inspections);Equal(baseline["defects"],summary.Defects);
            metrics.Add(new{query="inspection_with_image_filter_and_counts",seconds=clock.Elapsed.TotalSeconds});
        });
        await Check("recovered LOT and panel summary matches independent SQL totals",async()=>
        {
            var summary=await repo.SummaryAsync(p,db,new(),default);
            Equal(baseline["lot_id"],summary.Lots);Equal(baseline["inspection"],summary.Inspections);Equal(baseline["defects"],summary.Defects);
            Equal(summary.Defects,summary.Panels.Sum(x=>x.Long("defect_count")??0));
            var lot=await repo.SummaryAsync(p,db,new(){Text="agsd",Target=SearchTarget.Lot},default);
            Equal(1L,lot.Lots);Equal(17L,lot.Inspections);Equal(82836L,lot.Defects);
            await File.WriteAllTextAsync(Path.Combine(output,"recovered-panel-counts.json"),JsonSerializer.Serialize(new{summary.Lots,summary.Inspections,summary.Defects,summary.PanelGroups,panels=summary.Panels,agsd=new{lot.Inspections,lot.Defects}},new JsonSerializerOptions{WriteIndented=true}));
        });
        foreach(var pair in new[]{(BrowseLevel.Lots,"lot_id"),(BrowseLevel.Layers,"layer"),(BrowseLevel.Inspections,"inspection"),(BrowseLevel.Defects,"defects")})
        {
            await Check("recovered exact row count: "+pair.Item2,async()=>
            {
                long count=0;string? cursor=null;var clock=Stopwatch.StartNew();
                do{var page=await repo.QueryAsync(p,db,pair.Item1,new(),1000,cursor,default);count+=page.Rows.Count;True(page.Rows.All(x=>x.Database==db&&!x.Values.ContainsKey("image")));cursor=page.Cursor;}while(cursor is not null);
                Equal(baseline[pair.Item2],count);metrics.Add(new{table=pair.Item2,rows=count,seconds=clock.Elapsed.TotalSeconds});
            });
        }
        await Check("recovered list and raw detail agree",async()=>
        {
            var first=await repo.QueryAsync(p,db,BrowseLevel.Defects,new(),30,null,default);
            foreach(var row in first.Rows)
            {var detail=await repo.DetailAsync(p,row.Key(p.Identity,BrowseLevel.Defects),default);foreach(string field in new[]{"raw_x","raw_y","defect_type","unit_idx","vms_code","cause_code","defect_size"})if(detail.Values.ContainsKey(field))Equal(detail.Text(field),row.Text(field));}
        });
        await Check("recovered actual images keep source IDs and metadata",async()=>
        {
            var first=await repo.QueryAsync(p,db,BrowseLevel.Defects,new(){Images=ImageFilter.Present},64,null,default);int encoded=0,raw=0,unknown=0;
            foreach(var row in first.Rows)
            {var image=await repo.ImageAsync(p,row.Key(p.Identity,BrowseLevel.Defects),default);True(image is not null&&image.Bytes.Length>0);Equal(db,image!.Database);byte[] bytes=ImageBytes.Normalize(image.Bytes,image.Width,image.Height,image.Channels);if(image.Width>0&&image.Height>0&&(long)image.Width*image.Height*image.Channels==bytes.Length)raw++;else if(ImageBytes.Extension(bytes)!=".raw")encoded++;else unknown++;}
            await File.WriteAllTextAsync(Path.Combine(output,"real-image-metadata.json"),JsonSerializer.Serialize(new{sampled=first.Rows.Count,encoded,raw,unknown}));
            True(first.Rows.Count>0);
        });
        await Check("recovered full CSV count matches database",async()=>
        {
            async IAsyncEnumerable<ResultRow> All(){await foreach(var row in repo.StreamAsync(p,db,BrowseLevel.Defects,new(),false,null,default))yield return row;}
            long count=await ExportService.CsvAsync(Path.Combine(output,"회수DB_불량전체.csv"),All(),default);Equal(baseline["defects"],count);
        });
        await File.WriteAllTextAsync(Path.Combine(output,"recovered-counts-and-timing.json"),JsonSerializer.Serialize(new{database=db,baseline,metrics,workingSet=Environment.WorkingSet},new JsonSerializerOptions{WriteIndented=true}));
    }
}
