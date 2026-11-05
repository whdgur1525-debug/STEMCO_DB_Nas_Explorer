using System.Globalization;
using System.Text;
using System.Text.Json;
using Aoi.NasExplorer.Core;
namespace Aoi.NasExplorer.Export;

// Handler NO_DB result contracts: original mask, DB-proven punch markers, and current-model MES mapping.
public static class HandlerSidecars
{
    public sealed record Reason(string Artifact,string Code,string Message);
    public sealed record CurrentLayerMarker(long Count,string? Suffix,string Database,string LayerId,Dictionary<string,object?> RawFields);
    public sealed record LayerContribution(string LayerFolder,string Lot,long Panel,long? Mask,int[]? MesBars,int[] PunchUnits,CurrentLayerMarker? CurrentLayer);
    public sealed record Outcome(IReadOnlyList<string> SavedFiles,IReadOnlyList<Reason> NotRequired,
        IReadOnlyList<Reason> Unrecoverable,IReadOnlyList<Reason> Failures,LayerContribution? Contribution)
    { public IReadOnlyList<string> SourceNotes {get;init;}=[]; }
    private static readonly Encoding Utf8WithoutBom=new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};

    /// <summary>Writes the panel mask and returns contributions for one later write per layer folder.</summary>
    /// <param name="dbPunchUnitIds">Only zero-based unit IDs proven by the DB punch marker, including all four raw/CAD coordinates.</param>
    public static async Task<Outcome> PreparePanelAsync(string layerFolder,string panelFolder,
        ResultRow inspection,ResultRow detail,ResultRow? layerDetail,IReadOnlyCollection<int> dbPunchUnitIds,
        ModelGeometry? currentGeometry,CancellationToken ct)
    {
        var saved=new List<string>();var normal=new List<Reason>();var unavailable=new List<Reason>();var failed=new List<Reason>();var notes=new List<string>();
        async Task Save(string path,string text)
        {
            try{saved.Add(await ExportService.WriteNewAsync(path,text,ct,Utf8WithoutBom));}
            catch(Exception error) when(error is not OperationCanceledException)
            {failed.Add(new(path,"write_failed",error.Message));}
        }
        ct.ThrowIfCancellationRequested();
        long? panel=inspection.Long("panel_no")??detail.Long("panel_no");
        if(panel is null||panel<0||Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(panelFolder)))!=panel.Value.ToString(CultureInfo.InvariantCulture))
        {
            failed.Add(new("panelFolder","invalid_panel_folder","패널 번호와 정수형 폴더 이름이 일치하지 않습니다."));
            return new(saved,normal,unavailable,failed,null);
        }
        long? mask=detail.Values.ContainsKey("disposed_bar_mask")?detail.Long("disposed_bar_mask"):inspection.Long("disposed_bar_mask");
        if(mask is >=0)await Save(Path.Combine(panelFolder,"block_dispose.txt"),mask.Value.ToString(CultureInfo.InvariantCulture)+"\r\n");
        else unavailable.Add(new("block_dispose.txt","mask_not_recorded","disposed_bar_mask가 NULL·비정수·음수이므로 정상 0을 만들지 않습니다."));

        int[] punches=dbPunchUnitIds.Where(x=>x>=0).Distinct().OrderBy(x=>x).ToArray();
        if(dbPunchUnitIds.Any(x=>x<0))unavailable.Add(new("punch.json","invalid_punch_unit","음수 증거 유닛은 제외했습니다. 0 기반 비음수 UnitIdx만 허용합니다."));
        if(punches.Length==0)normal.Add(new("punch.json","no_punch_evidence","이 검사에는 전달된 DB 펀치 증거가 없어 생성하지 않습니다. 펀치 기능 꺼짐을 의미하지 않습니다."));
        else notes.Add("punch.json의 UnitIdx는 선택 검사에서 DB 증거로 확인한 0 기반 유닛입니다. 검사 당시 펀치 설정 전체 목록과 같음을 보장하지 않습니다.");

        CurrentLayerMarker? marker=null;
        long? retest=layerDetail is not null&&layerDetail.Values.ContainsKey("retest_count")
            ?layerDetail.Long("retest_count"):detail.Long("current_layer_retest");
        if(retest is >0)
        {
            marker=new(retest.Value,layerDetail?.Text("retest_suffix")??detail.Text("current_layer_retest_suffix"),
                layerDetail?.Database??inspection.Database,layerDetail?.Text("layer")??inspection.Text("layer"),
                layerDetail is null?new Dictionary<string,object?>{{"current_layer_retest",retest},{"current_layer_retest_suffix",detail["current_layer_retest_suffix"]}}:new(layerDetail.Values));
            unavailable.Add(new("RETEST.INI","historical_retest_unavailable","현재 Layer 재검 표식은 검사 당시 표식으로 보장되지 않습니다. 원본 위치에 RETEST.INI를 합성하지 않습니다."));
        }
        else if(retest==0)normal.Add(new("RETEST.INI","current_retest_zero","현재 Layer 재검 횟수 0이므로 현재 참고 표식은 불필요합니다. 과거 재검 없음의 증거는 아닙니다."));
        else unavailable.Add(new("RETEST.INI","current_retest_not_recorded","현재 Layer 재검 표식이 미기록이어서 정상 0으로 추정하지 않습니다."));
        unavailable.Add(new("Defect Point_*.txt","handler_eq_count_unavailable","Handler 검사 당시 eq 원본 총수 및 누적 통계가 없어 Defect Point 호환 파일을 만들지 않습니다. DB COUNT로 eq/ert/vms를 동일하게 채우지 않습니다."));

        int[]? mesBars=null;
        if(mask==0)mesBars=[];
        else if(mask is >0)
        {
            long blockCount=currentGeometry is null||currentGeometry.BlockX.Any(x=>!double.IsFinite(x))||currentGeometry.BlockY.Any(y=>!double.IsFinite(y))
                ?0:(long)currentGeometry.BlockX.Distinct().Count()*currentGeometry.BlockY.Distinct().Count();
            int[]? mapping=blockCount switch{4=>[1,3,4,2],9=>[1,4,7,8,5,2,3,6,9],_=>null};
            if(mapping is null)unavailable.Add(new("Bar_result_*.txt","bar_mapping_unavailable","폐기 마스크는 있으나 현재 모델의 전체 바 수가 4/9로 확인되지 않아 MES 번호를 만들지 않습니다."));
            else if(((ulong)mask.Value>>mapping.Length)!=0)unavailable.Add(new("Bar_result_*.txt","mask_outside_model","폐기 마스크에 현재 모델 범위 밖 비트가 있어 일부 바를 조용히 버리지 않습니다."));
            else
            {
                mesBars=Enumerable.Range(0,mapping.Length).Where(i=>(mask.Value&(1L<<i))!=0).Select(i=>mapping[i]).OrderBy(x=>x).ToArray();
                notes.Add("MES 바 번호는 현재 모델의 전체 바 수로 변환했습니다. 검사 당시 모델과 같음을 보장하지 않습니다.");
            }
        }
        else unavailable.Add(new("Bar_result_*.txt","mask_not_recorded","폐기 마스크 미기록이므로 빈 바 목록을 정상으로 합성하지 않습니다."));
        string lot=inspection.Text("lot_id_name");
        if(string.IsNullOrEmpty(lot))lot=detail.Text("lot_id_name");
        return new(saved,normal,unavailable,failed,new(Path.GetFullPath(layerFolder),lot,panel.Value,mask,mesBars,punches,marker)){SourceNotes=notes};
    }

    // Call once per actual export layer folder, after collecting all panel contributions.
    // This avoids first-panel-only punch lists and CreateNew collisions across panels.
    public static async Task<Outcome> FinalizeLayerAsync(IReadOnlyList<LayerContribution> panels,CancellationToken ct,string? diagnosticFolder=null)
    {
        var saved=new List<string>();var normal=new List<Reason>();var unavailable=new List<Reason>();var failed=new List<Reason>();var notes=new List<string>();
        if(panels.Count==0){normal.Add(new("layer_sidecars","empty_scope","선택된 검사 없음"));return new(saved,normal,unavailable,failed,null);}
        string folder=Path.GetFullPath(panels[0].LayerFolder),lot=panels[0].Lot;
        if(panels.Any(p=>!Path.GetFullPath(p.LayerFolder).Equals(folder,StringComparison.OrdinalIgnoreCase)||p.Lot!=lot))
            throw new ArgumentException("동일한 실제 layerFolder/LOT의 검사 기여분만 한 번에 전달해야 합니다.");
        async Task Save(string path,string text)
        {
            try{saved.Add(await ExportService.WriteNewAsync(path,text,ct,Utf8WithoutBom));}
            catch(Exception error) when(error is not OperationCanceledException){failed.Add(new(path,"write_failed",error.Message));}
        }
        ct.ThrowIfCancellationRequested();
        int[] punches=panels.SelectMany(p=>p.PunchUnits).Distinct().OrderBy(x=>x).ToArray();
        if(punches.Length>0)
        {
            await Save(Path.Combine(folder,"punch.json"),JsonSerializer.Serialize(new{PunchFlag=1,DefectType="Open",Result="BAD",UnitIdx=punches},JsonOptions).Replace("\r\n","\n").Replace("\n","\r\n")+"\r\n");
            notes.Add("punch.json은 선택 검사에서 DB 증거로 확인한 0 기반 유닛의 합집합입니다. 원래 펀치 설정 전체 목록의 복원을 의미하지 않습니다.");
        }
        else normal.Add(new("punch.json","no_punch_evidence","전체 선택 검사에서 확인된 펀치 증거가 없어 생성하지 않습니다."));

        var markers=panels.Where(p=>p.CurrentLayer is not null).Select(p=>p.CurrentLayer!).ToArray();
        if(markers.Length>0&&diagnosticFolder is not null)
        {
            string markerPath=Path.Combine(diagnosticFolder,"RETEST.reference.json");
            try
            {
                await ExportService.WriteNewAsync(markerPath,JsonSerializer.Serialize(new{kind="current_layer_reference_only",handlerCompatible=false,
                    historicalInspectionMarker=false,generatedAt=DateTimeOffset.Now,
                    note="현재 조회된 Layer 표식입니다. 선택 검사 당시 RETEST.INI의 복원이 아닙니다.",
                    markers=markers.DistinctBy(x=>(x.Database,x.LayerId,x.Count,x.Suffix))},JsonOptions)+"\r\n",ct,Utf8WithoutBom);
                notes.Add("현재 Layer 표식 참고 자료는 반출 파일에 포함하지 않고 로컬 진단 위치에 보관했습니다: "+markerPath);
            }
            catch(Exception error) when(error is not OperationCanceledException){failed.Add(new(markerPath,"local_reference_write_failed",error.Message));}
        }

        // NO_DB erases a panel's row when its mask is zero; it does not write '18,'.
        // Header-only output is therefore the faithful zero-mask representation.
        var known=panels.Where(p=>p.MesBars is not null).GroupBy(p=>p.Panel).ToArray();
        bool conflict=known.Any(group=>group.Select(p=>string.Join(",",p.MesBars!)).Distinct().Count()>1);
        if(conflict)failed.Add(new("Bar_result_*.txt","conflicting_panel_masks","동일 패널 번호에 상충하는 폐기 바 이력이 있습니다. 각 검사 이력 폴더를 나눠야 합니다."));
        else if(panels.Any(p=>p.MesBars is null))
            unavailable.Add(new("Bar_result_*.txt","incomplete_selected_scope","선택 범위 내 마스크 미기록·매핑 불가 검사가 있어 원본 호환 전체 바 결과 파일을 만들지 않습니다."));
        else if(known.Length>0)
        {
            string fileName="Bar_result_"+lot+".txt";
            if(string.IsNullOrEmpty(lot)||fileName.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                failed.Add(new(fileName,"invalid_lot_file_name","원본 LOT를 파일명으로 안전하게 보존할 수 없어 호환 파일 생성을 생략합니다."));
            else
            {
                var text=new StringBuilder("Panel No,MaskingBar\r\n");
                foreach(var group in known.OrderBy(x=>x.Key))
                {
                    var bars=group.First().MesBars!;
                    if(bars.Length>0)text.Append(group.Key.ToString(CultureInfo.InvariantCulture)).Append(',').AppendJoin(',',bars).Append("\r\n");
                }
                await Save(Path.Combine(folder,fileName),text.ToString());
            }
        }
        else unavailable.Add(new("Bar_result_*.txt","no_verified_bar_contribution","검증된 마스크/바 기여분이 없어 파일을 만들지 않습니다."));
        return new(saved,normal,unavailable,failed,null){SourceNotes=notes};
    }
}
