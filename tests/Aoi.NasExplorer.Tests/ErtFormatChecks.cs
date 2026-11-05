using System.Globalization;
using System.Text;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Export;

internal static class ErtFormatChecks
{
    public static async Task RunAsync(string output,Func<string,Func<Task>,Task> check)
    {
        var g=new ModelGeometry(1.5,96.5,116,278.75,337.01,[0],[0],10,10,[0.5],[0.5],1,1,10,10);
        var row=new ResultRow{Database="fixture",Values=new()
        {
            ["defects"]=1L,["unit_idx"]=591,["unit_x"]=5,["unit_y"]=36,
            ["raw_x"]=853d,["raw_y"]=291d,["cad_x"]=1130.9,["cad_y"]=628.9,
            ["defect_type"]=8,["vms_state"]=0,["vms_code"]=0,["defect_size"]=-9999d,
            ["source_metadata_version"]=1,["source_block_idx"]=1,["source_axis_x"]=5,["source_axis_y"]=36
        }};
        ErtDocument Render(ResultRow r,string status="VMS_COMPLETED")=>ErtExporter.Render("C:\\export","model","lot","L1",12,new(2026,9,18,2,42,47),status,1,g,[r]);
        static string Row(ErtDocument doc)=>doc.Text.Split("\r\n")[1];
        static void Assert(bool value,string why){if(!value)throw new InvalidDataException(why);}
        await check("ERT file VMS delimiter numbers reserved and zero footer",()=>
        {
            string text=Render(row).Text;
            string expected="0, 853, 291, 8, 0, 5, 1130, 628, 0, 591, 0, 0, , ,V, 0, 0, 36, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, ";
            Assert(Row(Render(row))==expected,"File VMS row bytes differ from the NO_DB reference.");
            Assert(text.Contains("0, 0, 0, 0, 0, 0, \r\nmodel, L1, lot, 12, 1.5, 0, \r\n2026, 9, 18, 2, 42, 47, \r\n"),"Footer, unpadded panel/time or terminal delimiter differs.");
            Assert(text.Split("\r\n",StringSplitOptions.RemoveEmptyEntries).All(x=>x.EndsWith(", ",StringComparison.Ordinal)),"Every ERT line must end in comma-space.");
            return Task.CompletedTask;
        });
        await check("ERT source measurement sentinel never leaks into area field",()=>
        {
            foreach(double size in new[]{-9999d,123.45,double.NaN})
            {var r=new ResultRow{Values=new(row.Values){["defect_size"]=size}};Assert(Row(Render(r)).Split(',')[8].Trim()=="0","Measured defect_size was incorrectly mapped into Handler area.");}
            Assert(row.Number("defect_size")==-9999,"Source measurement was mutated.");return Task.CompletedTask;
        });
        await check("ERT raw nonintegral coordinates remain precise without fixed zero padding",()=>
        {
            var r=new ResultRow{Values=new(row.Values){["raw_x"]=372.1891481,["raw_y"]=696.8786009}};
            Assert(Row(Render(r)).StartsWith("0, 372.189148, 696.878601,",StringComparison.Ordinal),"Fixed-six-decimal Handler values must be reserialized with VMS G15, rather than truncating valid decimals.");
            return Task.CompletedTask;
        });
        await check("ERT all VMS states and special error codes use latest DataTransfer mapping",()=>
        {
            foreach(int type in new[]{8,19,20})foreach(var state in new[]{(0,"V"),(1,"B"),(2,"P")})
            {var r=new ResultRow{Values=new(row.Values){["defect_type"]=type,["vms_state"]=state.Item1}};Assert(Row(Render(r)).Contains($", , ,{state.Item2}, 0, 0,"),"vms_state must follow current DataTransfer semantics.");}
            return Task.CompletedTask;
        });
        await check("ERT motion and review flags match empty and completed panel states",()=>
        {
            foreach(string status in new[]{"START","COMPLETED","VMS_COMPLETED","MES_COMPLETED"})
            {
                bool reviewed=status is "VMS_COMPLETED" or "MES_COMPLETED";
                var nonempty=Render(row,status).Text.Split("\r\n")[0].Split(',');
                var empty=ErtExporter.Render("C:\\export","m","l","L1",1,new(2026,1,1),status,1,g,[]).Text.Split("\r\n")[0].Split(',');
                Assert(nonempty[11].Trim()==(reviewed?"1":"0")&&nonempty[12].Trim()==(reviewed?"1":"0"),"Nonempty header flags differ.");
                Assert(empty[11].Trim()=="0"&&empty[12].Trim()==(reviewed?"1":"0"),"Empty reviewed panel must have motion0/review1.");
            }
            return Task.CompletedTask;
        });
        await check("ERT recorded master path is independent from output path",async()=>
        {
            const string master="O:\\EQ_09\\model\\lot\\L1";
            string path=Path.Combine(output,"ert-master-"+Guid.NewGuid().ToString("N"),"012.ERT");
            var expected=ErtExporter.Render(Path.GetDirectoryName(path)!,"model","lot","L1",12,new(2026,9,18,2,42,47),"VMS_COMPLETED",1,g,[row],master);
            await ErtExporter.WriteStreamingAsync(path,"model","lot","L1",12,new(2026,9,18,2,42,47),"VMS_COMPLETED",1,g,1,ExportService.Enumerate([row]),new UTF8Encoding(false),default,master);
            Assert(await File.ReadAllTextAsync(path)==expected.Text,"Stream and memory master paths/flags differ.");
            Assert(expected.Text.Split("\r\n")[0].Contains(master),"Recorded path lost.");
            Assert(!expected.Notes.Any(x=>x.Contains("경로 미기록",StringComparison.Ordinal)),"Recorded master path incorrectly reported missing.");
        });
        await check("ERT missing block dimensions and negative axes match DataTransfer fallbacks",()=>
        {
            var fallback=g with{BlockX=[0,100],BlockY=[0,200],BlockWidth=0,BlockHeight=0};
            var missing=new ResultRow{Values=new(row.Values){["source_metadata_version"]=0,["unit_x"]=1,["unit_y"]=1}};
            string line=Row(ErtExporter.Render("C:\\export","m","l","L2",1,new(2026,1,1),"COMPLETED",1,fallback,[missing]));
            Assert(line.Split(',')[11].Trim()=="3","L2 block width193/height232 fallback differs from DataTransfer.");
            var negative=new ResultRow{Values=new(missing.Values){["unit_x"]=-3,["unit_y"]=-4}};
            line=Row(ErtExporter.Render("C:\\export","m","l","L1",1,new(2026,1,1),"COMPLETED",1,g,[negative]));
            Assert(line.Split(',')[17].Trim()=="0"&&line.Split(',')[18].Trim()=="0","Negative computed axes must use DataTransfer's zero clamp.");
            return Task.CompletedTask;
        });
        string? reference=Environment.GetEnvironmentVariable("NAS_ERT_REFERENCE");
        if(!string.IsNullOrWhiteSpace(reference))await check("ERT complete supplied NO_DB 002 file is byte-identical",async()=>
        {
            byte[] bytes=await File.ReadAllBytesAsync(reference);
            string text=Encoding.UTF8.GetString(bytes);string[] lines=text.Split("\r\n",StringSplitOptions.RemoveEmptyEntries);
            string[] Fields(string s)=>s.Split(',').Select(x=>x.Trim()).ToArray();
            string[] h=Fields(lines[0]),f=Fields(lines[^2]),date=Fields(lines[^1]);
            int total=int.Parse(h[5],CultureInfo.InvariantCulture),count=int.Parse(h[1],CultureInfo.InvariantCulture);
            // Geometry fixture supplies only the known total. Original axes are explicit metadata
            // from the supplied ERT, so this checks serialization, not historical-model recovery.
            var geometry=g with{FrameX=Enumerable.Repeat(0.5,total).ToArray(),UnitY=[0.5],Resolution=double.Parse(f[4],CultureInfo.InvariantCulture)};
            var rows=new List<ResultRow>();
            for(int n=1;n<=count;n++)
            {
                var fields=Fields(lines[n]);double N(int i)=>double.Parse(fields[i],CultureInfo.InvariantCulture);
                rows.Add(new(){Values=new()
                {
                    ["defects"]=(long)n,["unit_idx"]=(int)N(9),["unit_x"]=(int)N(5),["unit_y"]=1,
                    ["raw_x"]=N(1),["raw_y"]=N(2),["cad_x"]=N(6),["cad_y"]=N(7),["defect_type"]=(int)N(3),
                    ["vms_state"]=fields[14] switch{"B"=>1,"P"=>2,_=>0},["vms_code"]=(int)N(10),["defect_size"]=-9999d,
                    ["source_metadata_version"]=1,["source_block_idx"]=(int)N(11)+1,["source_axis_x"]=(int)N(18),["source_axis_y"]=(int)N(17)
                }});
            }
            var time=new DateTime(int.Parse(date[0],CultureInfo.InvariantCulture),int.Parse(date[1],CultureInfo.InvariantCulture),int.Parse(date[2],CultureInfo.InvariantCulture),int.Parse(date[3],CultureInfo.InvariantCulture),int.Parse(date[4],CultureInfo.InvariantCulture),int.Parse(date[5],CultureInfo.InvariantCulture));
            string path=Path.Combine(output,"ert-canonical-"+Guid.NewGuid().ToString("N"),"012.ERT");
            var doc=ErtExporter.Render("C:\\export",f[0],f[2],f[1],int.Parse(f[3],CultureInfo.InvariantCulture),time,"VMS_COMPLETED",26,geometry,rows,h[2]);
            Assert(Encoding.UTF8.GetBytes(doc.Text).SequenceEqual(bytes),"Whole NO_DB canonical bytes differ.");
            await ErtExporter.WriteStreamingAsync(path,f[0],f[2],f[1],int.Parse(f[3],CultureInfo.InvariantCulture),time,"VMS_COMPLETED",26,geometry,count,ExportService.Enumerate(rows),new UTF8Encoding(false),default,h[2]);
            Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes),"Whole streaming NO_DB canonical bytes differ.");
        });
    }
}
