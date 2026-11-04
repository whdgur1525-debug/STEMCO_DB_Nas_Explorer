using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.Export;

public static class ExportService
{
    public static string CsvCell(object? value)
    {
        string s=ResultRow.ValueText(value);
        // Spreadsheet applications must not execute strings imported from a model/LOT/cause field.
        if(value is string && s.TrimStart().Length>0 && "=+-@\t\r".Contains(s.TrimStart()[0])) s="'"+s;
        return "\""+s.Replace("\"","\"\"")+"\"";
    }
    public static string SafeComponent(string value)
    {
        var invalid=Path.GetInvalidFileNameChars();
        var safe=new string(value.Select(x=>invalid.Contains(x)||x<32?'_':x).ToArray()).Trim().TrimEnd('.');
        if(safe.Length==0||safe is "." or "..") safe="item";
        if(safe.Length>70) safe=safe[..70];
        string stem=safe.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem,StringComparer.OrdinalIgnoreCase)) safe="_"+safe;
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return safe+"_"+hash;
    }
    public static async Task<string> WriteNewAsync(string path,string text,CancellationToken ct,Encoding? encoding=null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string pending=path+".partial-"+Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(pending,text,encoding??new UTF8Encoding(true),ct);
        ct.ThrowIfCancellationRequested();
        File.Move(pending,path,false);
        return path;
    }
    public static async Task<long> CsvAsync(string path,IAsyncEnumerable<ResultRow> rows,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string partial=path+".partial-"+Guid.NewGuid().ToString("N");
        long count=0; string[]? columns=null;
        await using(var file=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true))
        await using(var writer=new StreamWriter(file,new UTF8Encoding(true)))
        {
            await foreach(var row in rows.WithCancellation(ct))
            {
                if(columns is null)
                {
                    columns=row.Values.Keys.Order(StringComparer.Ordinal).ToArray();
                    await writer.WriteLineAsync("\"source_database\","+string.Join(",",columns.Select(x=>CsvCell(x))));
                }
                // A fixed projection is required; silently dropping fields from newer DBs is not acceptable.
                if(row.Values.Keys.Except(columns,StringComparer.Ordinal).Any()) throw new InvalidDataException("DB별 출력 열이 다릅니다. DB별로 나누어 추출하세요.");
                await writer.WriteLineAsync((CsvCell(row.Database)+","+string.Join(",",columns.Select(x=>CsvCell(row.Values.GetValueOrDefault(x))))).AsMemory(),ct);
                count++;
            }
            if(columns is null) await writer.WriteLineAsync("\"source_database\"");
        }
        ct.ThrowIfCancellationRequested(); File.Move(partial,path,false); return count;
    }
    public static async IAsyncEnumerable<ResultRow> Enumerate(IEnumerable<ResultRow> rows)
    { foreach(var row in rows){yield return row;} await Task.CompletedTask; }
    public static string Json(ResultRow row)=>JsonSerializer.Serialize(new{sourceDatabase=row.Database,fields=row.Values},new JsonSerializerOptions{WriteIndented=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals});
}

public sealed record ErtDocument(string Text,IReadOnlyList<string> Notes,int Rows);
public static class ErtExporter
{
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    public static ErtDocument Render(string folder,string model,string lot,string layer,int panel,DateTime time,
        string status,int? fovCount,ModelGeometry g,IReadOnlyList<ResultRow> defects)
    {
        if(new[]{model,lot,layer,folder}.Any(x=>x.IndexOfAny(new[]{',','\r','\n'})>=0)) throw new InvalidDataException("ERT 경로·이름에는 쉼표나 줄바꿈을 사용할 수 없습니다.");
        if(g.Resolution<=0 || !double.IsFinite(g.Resolution)) throw new InvalidDataException("모델 해상도가 올바르지 않습니다.");
        var notes=new HashSet<string>(StringComparer.Ordinal){"모델 위치·해상도는 현재 모델 DB 기준입니다. 검사 당시 모델 스냅샷임을 보장하지 않습니다.","헤더 유닛수·분할 축은 모델 기반 재구성입니다. Handler 원본 open_short_GM/검사 당시 축 분할 정보가 없어 NO_DB 원본 헤더와 같음을 보장하지 않습니다.","VMS 판정 변경시각이 DB에 별도 기록되지 않아 ERT의 리뷰 시각은 -1입니다."};
        int total=g.TotalUnits; int perFov=fovCount is >0 ? total/3/fovCount.Value : 0;
        if(fovCount is null or <=0) notes.Add("FOV 개수 미기록: ERT FOV당 유닛수는 호환 기본값 0입니다.");
        int reviewed=status is "VMS_COMPLETED" or "MES_COMPLETED"?1:0;
        var text=new StringBuilder();
        void Line(FormattableString s)=>text.Append(s.ToString(Inv)).Append("\r\n");
        Line($"4.3.1, {defects.Count}, {folder}, 10000, 10000, {total}, {total/3}, {perFov}, {perFov}, {perFov}, {model}, 0, {reviewed}");
        foreach(var d in defects.OrderBy(x=>x.Long("unit_idx")))text.Append(RenderRow(d,g,layer,reviewed,notes));
        text.Append(Footer(model,lot,layer,panel,time,g));
        return new(text.ToString(),notes.ToArray(),defects.Count);
    }
    private static string RenderRow(ResultRow d,ModelGeometry g,string layer,int reviewed,HashSet<string> notes)
    {
            double Num(string key)=>d.Number(key)??throw new InvalidDataException($"불량 {d.Text("defects")}: {key} 미기록. JSON·CSV 원본 추출을 사용할 수 있습니다.");
            int ux=checked((int)Num("unit_x")),uy=checked((int)Num("unit_y"));
            int block=0,ax=0,ay=0;
            if(d.Long("source_metadata_version")>=1 && d.Long("source_block_idx")>0 && d.Long("source_axis_x")>0 && d.Long("source_axis_y")>0)
            { block=checked((int)d.Long("source_block_idx")!.Value); ax=checked((int)d.Long("source_axis_x")!.Value); ay=checked((int)d.Long("source_axis_y")!.Value); }
            else
            {
                notes.Add("원본 바·축 미기록 행: 현재 모델을 사용해 ERT 위치를 계산했습니다.");
                if(ux>0&&ux<=g.FrameX.Length&&uy>0&&uy<=g.UnitY.Length)
                {
                    double rx=g.FrameX[ux-1],ry=g.UnitY[uy-1];
                    int col=Array.FindIndex(g.BlockX,x=>rx>=x&&rx<=x+g.BlockWidth);
                    int row=Array.FindIndex(g.BlockY,y=>ry>=y&&ry<=y+g.BlockHeight);
                    if(col>=0&&row>=0){ if(layer.Contains("l2",StringComparison.OrdinalIgnoreCase)) col=g.BlockX.Length-1-col; block=col*g.BlockY.Length+(col%2==0?row+1:g.BlockY.Length-row); }
                }
                int nx=g.UnitWidth>0?(int)Math.Round(g.InspectWidth/g.UnitWidth):0;
                int ny=g.UnitHeight>0?(int)Math.Round(g.InspectHeight/g.UnitHeight):0;
                if(nx>0&&ny>0){ax=(ux-1)%nx+1; ay=(uy-1)%ny+1;}
                if(block<=0){block=1;notes.Add("모델 범위 밖 행: 기존 다운로드 규약의 바 인덱스 0을 사용했습니다. 원본 좌표는 그대로입니다.");}
            }
            var attrs=d["d_attr"] as double[];
            bool punch=attrs is {Length:>=2}&&attrs[0]==9999&&attrs[1]==10&&Num("raw_x")==512&&Num("raw_y")==512;
            int code=(int)Num("vms_code"),state=(int)Num("vms_state");
            string mark=state==1?"B":state==2?"P":"V";
            if(state==0&&d.Long("defect_type") is 19 or 20)mark="B";
            if(punch&&reviewed==0){mark="B";if(code==0)code=4;}
            // Match the current DataTransfer writer, retaining recorded sizes only when available.
            double area=punch?9999:d.Number("defect_size")??0;
            if(!punch&&!d.Number("defect_size").HasValue) notes.Add("크기 미기록 행: ERT 면적에 호환 기본값 0을 썼습니다. 원본 상세의 미기록 상태는 유지됩니다.");
            return FormattableString.Invariant($"0, {Num("raw_x"):F6}, {Num("raw_y"):F6}, {(int)Num("defect_type")}, 0, {ux}, {(int)Num("cad_x")}, {(int)Num("cad_y")}, {area:F6}, {(int)Num("unit_idx")}, {code}, {block-1}, , , {mark}, -1, -1, {ay}, {ax}, 0.0, 0.0, 0, 0, 0, 0, 0, 0, 0, \r\n");
    }
    private static string Footer(string model,string lot,string layer,int panel,DateTime time,ModelGeometry g)=>
        FormattableString.Invariant($"{g.Left:F6}, {g.Top:F6}, {g.Right:F6}, {g.Bottom:F6}, 0, 0, \r\n{model}, {layer}, {lot}, {panel:D3}, {g.Resolution:F1}, 0, \r\n{time.Year}, {time.Month:D2}, {time.Day:D2}, {time.Hour:D2}, {time.Minute:D2}, {time.Second:D2}, \r\n");
    public static async Task<ErtDocument> WriteStreamingAsync(string path,string model,string lot,string layer,int panel,DateTime time,
        string status,int? fovCount,ModelGeometry geometry,long expectedRows,IAsyncEnumerable<ResultRow> source,Encoding encoding,CancellationToken ct)
    {
        string folder=Path.GetDirectoryName(Path.GetFullPath(path))!;
        var template=Render(folder,model,lot,layer,panel,time,status,fovCount,geometry,Array.Empty<ResultRow>());
        var notes=template.Notes.ToHashSet(StringComparer.Ordinal);int reviewed=status is "VMS_COMPLETED" or "MES_COMPLETED"?1:0;
        string header=template.Text[..template.Text.IndexOf("\r\n",StringComparison.Ordinal)].Replace("4.3.1, 0,",$"4.3.1, {expectedRows},",StringComparison.Ordinal);
        string pending=path+".partial-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(folder);long count=0;
        await using(var file=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true))
        await using(var writer=new StreamWriter(file,encoding))
        {
            await writer.WriteAsync((header+"\r\n").AsMemory(),ct);
            await foreach(var row in source.WithCancellation(ct)){await writer.WriteAsync(RenderRow(row,geometry,layer,reviewed,notes).AsMemory(),ct);count++;}
            if(count!=expectedRows)throw new InvalidDataException($"ERT 행수 불일치: expected={expectedRows}, actual={count}. 부분 파일을 보존했습니다.");
            await writer.WriteAsync(Footer(model,lot,layer,panel,time,geometry).AsMemory(),ct);
        }
        ct.ThrowIfCancellationRequested();File.Move(pending,path,false);
        return new("",notes.ToArray(),checked((int)count));
    }
}
