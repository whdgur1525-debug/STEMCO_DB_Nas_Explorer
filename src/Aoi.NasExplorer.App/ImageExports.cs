using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private void Results_ContextMenuOpening(object sender,ContextMenuEventArgs e)=>UpdateResultContextMenu();
    private void UpdateResultContextMenu()
    {
        bool ready=_queryCts is null&&!_idleLocked;var selected=SelectedRows();
        foreach(var item in Results.ContextMenu.Items.OfType<MenuItem>())
        {
            string tag=Convert.ToString(item.Tag)??"";string? reason=null;bool enabled=false;
            switch(tag)
            {
                case "json":enabled=selected.Count==1;reason="목록에서 원본 기록 하나를 선택하세요.";break;
                case "csv":enabled=_rows.Count>0;reason="현재 검색 조건에 맞는 전체 기록을 저장합니다. 화면의 250개나 체크한 항목만 저장하는 기능이 아닙니다.";break;
                case "checked-images":enabled=_level==BrowseLevel.Defects&&_checkedRecords.Count>0;reason="불량 목록에서 저장할 이미지를 체크하세요.";item.Header=$"체크한 불량 이미지 {_checkedRecords.Count:N0}개 저장…";break;
                case "ert":
                    enabled=_level==BrowseLevel.Inspections&&selected.Count==1;
                    if(enabled){string key=selected[0].Text("model_name")+"_Layer"+selected[0].Text("layer_name");enabled=_ertAvailability.TryGetValue(key,out reason)&&reason is null;}
                    reason=enabled?"선택한 검사와 현재 모델 자료로 ERT를 재구성하여 미리 봅니다.":reason??"패널 검사 이력에서 검사 하나를 선택하고 모델 자료를 확인하세요.";break;
            }
            item.IsEnabled=ready&&enabled;item.ToolTip=ready?reason:"진행 중인 작업이 끝난 뒤 사용하세요.";ToolTipService.SetShowOnDisabled(item,true);
        }
    }
    private void Image_ContextMenuOpening(object sender,ContextMenuEventArgs e)
    {
        if(sender is FrameworkElement{ContextMenu:not null} element)
            foreach(var item in element.ContextMenu.Items.OfType<MenuItem>())
                item.IsEnabled=_queryCts is null&&!_idleLocked&&_level==BrowseLevel.Defects&&(Convert.ToString(item.Tag)=="panel"?CurrentPanelId() is not null:_image is{Bytes.Length:>0});
    }
    private async void SaveCurrentImage_Click(object sender,RoutedEventArgs e)
    {
        if(_image is not ImagePayload{Bytes.Length:>0} image||Selected() is not ResultRow row)return;
        string? folder=PickFolder("현재 열람 이미지 저장 위치");if(folder is null)return;
        string root=Path.Combine(folder,"열람이미지_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        await RunAsync(async ct=>{string path=await SaveHandlerImageAsync(row,image,root,ct);SetOutput(path,"열람 이미지 저장 완료");},true);
    }
    private async void ExportCheckedImages_Click(object sender,RoutedEventArgs e)
    {
        if(_level!=BrowseLevel.Defects||_checkedRecords.Count==0)return;
        string? folder=PickFolder("체크한 불량 이미지만 저장");if(folder is null)return;
        string root=Path.Combine(folder,"체크이미지_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        var selected=_checkedRecords.Values.ToArray();await RunAsync(ct=>SaveCheckedImagesAsync(root,selected,ct),true);
    }
    private async Task SaveCheckedImagesAsync(string root,IReadOnlyList<ResultRow> selected,CancellationToken ct)
    {
        Directory.CreateDirectory(root);long saved=0,missing=0,failed=0;string status="completed";
        string diagnostic=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synapse","NasExplorer","ExportHistory",DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagnostic);_lastDiagnosticFolder=diagnostic;DiagnosticButton.IsEnabled=true;
        await using var log=new StreamWriter(Path.Combine(diagnostic,"처리내역.jsonl"));
        try
        {
            foreach(var row in selected)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ResultRow? metadata=null;ImagePayload? image=null;DateTimeOffset captured=default;
                    await _repository.WithSnapshotAsync(_profile,row.Database,async(snapshot,time,token)=>
                    {captured=time;metadata=await snapshot.DetailAsync(_profile,row.Key(_profile.Identity,BrowseLevel.Defects),token);image=await snapshot.ImageAsync(_profile,row.Key(_profile.Identity,BrowseLevel.Defects),token);},ct);
                    if(image is null||image.Bytes.Length==0){missing++;await log.WriteLineAsync(JsonSerializer.Serialize(new{row.Database,id=row.Text("defects"),status="not_recorded"}));continue;}
                    string folder=Path.Combine(root,Aoi.NasExplorer.Export.InspectionBundleExporter.Component(row.Database));
                    string path=await SaveHandlerImageAsync(metadata!,image,folder,ct);saved++;
                    await log.WriteLineAsync(JsonSerializer.Serialize(new{row.Database,id=row.Text("defects"),capturedAt=captured,status="saved",path}));
                }
                catch(Exception ex) when(ex is not OperationCanceledException){failed++;await log.WriteLineAsync(JsonSerializer.Serialize(new{row.Database,id=row.Text("defects"),status="failed",error=ex.Message}));}
                Status.Text=ExportProgressLabel.Text=$"체크 이미지 · 선택 {selected.Count:N0}개 중 저장 {saved:N0} / 미기록 {missing:N0} / 실패 {failed:N0}";
                ExportProgress.IsIndeterminate=false;ExportProgress.Maximum=Math.Max(1,selected.Count);ExportProgress.Value=saved+missing+failed;
            }
            if(failed>0)status="partial_failure";
        }
        catch(OperationCanceledException){status="cancelled";throw;}
        catch{status="failed";throw;}
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(diagnostic,"결과.json"),JsonSerializer.Serialize(new{status,outputDirectory=Path.GetFullPath(root),selected=selected.Count,saved,missing,failed,endedAt=DateTimeOffset.Now}),CancellationToken.None);
            Log("체크 이미지 작업 기록: "+diagnostic);
            string state=status switch{"cancelled"=>"취소됨","partial_failure"=>"일부 실패","failed"=>"실패",_=>missing>0?"완료 · 이미지 미기록 있음":"완료"};
            SetOutput(root,$"체크 이미지 {state} · 선택 {selected.Count:N0}개 중 저장 {saved:N0} / 미기록 {missing:N0} / 실패 {failed:N0}",true);
        }
    }
}
