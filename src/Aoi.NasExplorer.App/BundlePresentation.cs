using System.Text;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private BundleWindow CreateBundleWindow(bool preferPanel,ResultRow? panelPreview=null)
    {
        string? panelId=_level==BrowseLevel.Defects?CurrentPanelId():null;
        int checkedPanels=_checkedRecords.Values.Select(row=>(row.Database,Id:row.Text("inspection"))).Where(x=>x.Id.Length>0).Distinct().Count();
        var dialog=new BundleWindow(_checkedRecords.Count,_level,panelId,SummaryLabel.Text,checkedPanels,preferPanel){Owner=this};
        dialog.SetScopeInfo(mode=>DescribeBundleScope(mode,panelPreview));
        return dialog;
    }

    private BundleScopeInfo DescribeBundleScope(BundleMode mode,ResultRow? panelPreview=null)
    {
        var rows=mode switch
        {
            BundleMode.Checked=>_checkedRecords.Values.ToArray(),
            BundleMode.Panel=>panelPreview is not null?new[]{panelPreview}:Selected() is ResultRow row?new[]{row}:Array.Empty<ResultRow>(),
            _=>_rows.ToArray()
        };
        var text=new StringBuilder("선택 반출 범위: ");
        if(mode==BundleMode.Panel)text.AppendLine("현재 패널의 검사 기록 전체 (불량 검색조건으로 자르지 않음)");
        else if(mode==BundleMode.Checked)text.AppendLine(_level==BrowseLevel.Defects?$"체크한 불량 {rows.Length:N0}건이 속한 검사 전체":$"체크한 {_level switch{BrowseLevel.Models=>"Model",BrowseLevel.Lots=>"LotID",BrowseLevel.Layers=>"Layer",_=>"검사"}} {rows.Length:N0}개 전체");
        else
        {
            text.AppendLine("현재 검색 결과 전체 (아직 읽지 않은 항목 포함)");
            text.AppendLine($"Model: {ScopeValue(_filter.Model)} / LotID: {ScopeValue(_filter.Lot)} / Layer: {ScopeValue(_filter.Layer)}");
            if(_filter.Panel.HasValue)text.AppendLine("패널 번호: "+_filter.Panel);
            if(_filter.InspectionId.Length>0)text.AppendLine("검사 Index: "+_filter.InspectionId);
        }
        var records=rows.GroupBy(row=>(row.Database,Id:row.Text("inspection"))).Select(group=>group.First()).ToArray();
        if(mode!=BundleMode.Query)
        {
            var identities=rows.Select(DescribeBundleIdentity).Distinct().ToArray();
            foreach(string identity in identities.Take(5))text.AppendLine(identity);
            if(identities.Length>5)text.AppendLine($"… 나머지 {identities.Length-5:N0}개는 대상 확인에서 확인합니다.");
            if(records.Length>0&&records.All(row=>row.Text("inspection").Length>0&&row.Long("stored_defect_count") is >=0))
                text.AppendLine($"미리보기 조회 기준: 검사 {records.Length:N0}개 / 패널 불량 전체 {records.Sum(row=>row.Long("stored_defect_count")!.Value):N0}건 (수집 중 변경될 수 있음)");
        }
        text.Append("실제 반출 검사 수와 폴더는 다음 화면에서 확정합니다. 최종 저장 수량은 각 패널을 읽는 시점의 값입니다.");
        var issues=rows.Where(row=>row.Text("model_name").Length>0&&row.Text("layer_name").Length>0)
            .Select(row=>(Model:row.Text("model_name"),Layer:row.Text("layer_name"))).Distinct()
            .Select(pair=>_ertAvailability.TryGetValue(pair.Model+"_Layer"+pair.Layer,out string? reason)&&reason is not null?$"{pair.Model} / {pair.Layer}: {reason}":null)
            .Where(reason=>reason is not null).Distinct().Take(5).ToArray();
        // The query can include folders not loaded on screen; known failures are informational, never an all-scope availability claim.
        return new(text.ToString(),issues.Length==0?null:"이미 확인된 모델 자료\n"+string.Join("\n",issues));
    }

    private static string ScopeValue(string value)=>value.Length>0?value:"전체";
    private static string DescribeBundleIdentity(ResultRow row)
    {
        var parts=new List<string>{"DB: "+row.Database};
        foreach(var (field,label) in new[]{("model_name","Model"),("lot_id_name","LotID"),("layer_name","Layer"),("machine_id","장비"),("panel_no","패널 번호"),("inspection","검사 Index")})
            if(row.Text(field).Length>0)parts.Add(label+": "+row.Text(field));
        return string.Join(" / ",parts);
    }
}
