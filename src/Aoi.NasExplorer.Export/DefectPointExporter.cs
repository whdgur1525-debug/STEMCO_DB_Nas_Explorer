using System.Globalization;
using System.Text;

namespace Aoi.NasExplorer.Export;

/// <summary>
/// A selected inspection's complete snapshot count and independently recorded Handler values.
/// Null means unknown; it must never be replaced by the stored defect count or a normal zero.
/// </summary>
public sealed record DefectPointPanel(long PanelNo,long ErtCount,long? EquipmentCount=null,
    long? VmsCount=null,long? AutoPassCount=null,long? NgCount=null,long? FovCount=null,
    double? Resolution=null,double? TactSeconds=null);

public sealed record DefectPointDocument(string FileName,string Text,IReadOnlyList<string> Notes,bool HasMissingFields);

/// <summary>Handler NO_DB Defect Point's 16 columns, without fabricating unavailable historical counters.</summary>
public static class DefectPointExporter
{
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    public static DefectPointDocument Render(string model,string lot,string layer,string machine,
        string? processCode,IReadOnlyList<DefectPointPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if(panels.Count==0)throw new ArgumentException("Defect Point를 만들 검사 기록이 없습니다.",nameof(panels));
        string process=string.IsNullOrWhiteSpace(processCode)?"":processCode;
        foreach(string value in new[]{model,lot,layer,machine,process})
        {
            if(value.IndexOfAny(Path.GetInvalidFileNameChars())>=0||value.Any(c=>char.IsControl(c)||c==','))
                throw new InvalidDataException("Defect Point 이름·필드에는 파일명 금지문자·쉼표·줄바꿈을 사용할 수 없습니다.");
        }
        if(new[]{model,lot,layer,machine}.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Defect Point의 Model·LOT·Layer·장비 이름이 미기록입니다.");
        var notes=new HashSet<string>(StringComparer.Ordinal)
        {
            "NO_DB Defect Point 16열 양식입니다. ERT 칸은 선택 검사 전체의 DB snapshot 불량 행수이며 ERT 파일 선택 여부와 무관합니다.",
            "원본 Handler eq·vms·autoPass·NG가 미기록이면 공란입니다. DB 불량 행수를 다른 카운터에 복제하거나 미기록을 정상 0으로 만들지 않습니다.",
            "Total은 이번에 선택한 검사 범위입니다. 같은 패널의 여러 검사 이력을 전달하면 각 이력을 별도 행으로 포함합니다."
        };
        bool missing=false;
        long? Count(long? value,string field)
        {
            if(value is >=0)return value;
            missing=true;notes.Add(field+" 미기록·비정상 값은 공란입니다.");return null;
        }
        double? Measure(double? value,string field,bool positive)
        {
            if(value.HasValue&&double.IsFinite(value.Value)&&(positive?value.Value>0:value.Value>=0))return value;
            missing=true;notes.Add(field+" 미기록·비정상 값은 공란입니다.");return null;
        }
        var rows=panels.Select(p=>
        {
            if(p.PanelNo<0||p.ErtCount<0)throw new InvalidDataException("Defect Point의 패널 번호와 snapshot 불량 행수는 비음수여야 합니다.");
            return p with
            {
                EquipmentCount=Count(p.EquipmentCount,"eq"),VmsCount=Count(p.VmsCount,"vms"),
                AutoPassCount=Count(p.AutoPassCount,"autoPass"),NgCount=Count(p.NgCount,"NG"),
                FovCount=Count(p.FovCount,"FOV"),Resolution=Measure(p.Resolution,"해상도",true),
                TactSeconds=Measure(p.TactSeconds,"Tact",false)
            };
        }).ToArray();
        long? Sum(Func<DefectPointPanel,long?> select)
        {
            long total=0;
            foreach(var row in rows){long? value=select(row);if(!value.HasValue)return null;total=checked(total+value.Value);}
            return total;
        }
        T? Common<T>(Func<DefectPointPanel,T?> select,string field) where T:struct
        {
            T? value=select(rows[0]);
            if(!value.HasValue||rows.Any(p=>!Nullable.Equals(select(p),value)))
            {missing=true;notes.Add("Total "+field+"은 모든 선택 검사의 값이 기록되고 서로 같을 때만 표시합니다.");return null;}
            return value;
        }
        double? AverageTact()
        {
            if(rows.Any(p=>!p.TactSeconds.HasValue))return null;
            // Divide before summing to avoid overflow for otherwise finite recorded values.
            double average=0;foreach(var row in rows)average+=row.TactSeconds!.Value/rows.Length;
            if(double.IsFinite(average))return average;
            missing=true;notes.Add("Total Tact 계산 결과가 유한하지 않아 공란입니다.");return null;
        }
        string layerType=layer.Split(['-','_',' '],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??layer;
        string prefix=string.Join(", ",model,lot,layer,layerType,process,machine)+", ";
        var text=new StringBuilder();
        void Line(string panel,long? eq,long ert,long? vms,long? autoPass,long? ng,long? fov,double? resolution,double? tact)
        {
            string judge=eq.HasValue?(eq.Value==ert?"OK":"NG"):"";
            string Integer(long? value)=>value?.ToString(Inv)??"";
            string Decimal(double? value)=>value?.ToString("F1",Inv)??"";
            text.Append(prefix).AppendJoin(", ",panel,Integer(eq),ert.ToString(Inv),Integer(vms),judge,
                Integer(autoPass),Integer(ng),Integer(fov),Decimal(resolution),Decimal(tact)).Append("\r\n");
        }
        Line("Total",Sum(p=>p.EquipmentCount),Sum(p=>p.ErtCount)!.Value,Sum(p=>p.VmsCount),
            Sum(p=>p.AutoPassCount),Sum(p=>p.NgCount),Common(p=>p.FovCount,"FOV"),
            Common(p=>p.Resolution,"해상도"),AverageTact());
        foreach(var p in rows)Line(p.PanelNo.ToString("D3",Inv),p.EquipmentCount,p.ErtCount,p.VmsCount,
            p.AutoPassCount,p.NgCount,p.FovCount,p.Resolution,p.TactSeconds);
        string fileName=$"Defect Point_{model}_{lot}_{layer}_{layerType}_{(process.Length==0?"ProcessCode":process)}_{machine}.txt";
        return new(fileName,text.ToString(),notes.ToArray(),missing);
    }
}
