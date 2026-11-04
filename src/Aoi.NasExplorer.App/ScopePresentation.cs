using System.Data;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Export;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private readonly Dictionary<string,ResultRow> _checkedRecords=new(StringComparer.Ordinal);
    private CheckBox? _checkHeader;
    private bool _changingChecks;
    private string CheckKey(ResultRow row)=>$"{row.Database}/{_level}/{(_level==BrowseLevel.Models?row.Text("model_name"):row.Key(_profile.Identity,_level).Id)}";
    private static void EnrichLayer(ResultRow row)
    {
        string name=row.Text("layer_name");if(name.Length==0)return;
        var match=Regex.Match(name,@"^(L\d+)-(.+)-(\d{4})$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
        string[] parts=match.Success?match.Groups[2].Value.Split('-'):Array.Empty<string>();bool direction=parts.Length>1&&parts[0] is "T" or "B";
        row.Values["layer_side"]=match.Success?match.Groups[1].Value:"";
        row.Values["layer_direction"]=direction?parts[0]:"";
        row.Values["layer_recipe"]=direction?string.Join('-',parts.Skip(1)):match.Success?match.Groups[2].Value:"";
        row.Values["layer_resolution_code"]=match.Success?match.Groups[3].Value:"미기록";
    }
    private bool ShowMainColumn(string name)
    {
        if(name.StartsWith("__",StringComparison.Ordinal)||name=="pc_no")return false;
        if(name=="model_name"&&_filter.Model.Length>0||name=="lot_id_name"&&_filter.Lot.Length>0||name=="layer_name"&&_filter.Layer.Length>0)return false;
        if(name is "layer_side" or "layer_direction" or "layer_recipe" or "layer_resolution_code")return _filter.Layer.Length==0&&_level is BrowseLevel.Layers or BrowseLevel.Inspections or BrowseLevel.Defects;
        if(name=="source_database")return _queryDatabases.Count>1;
        if(name=="lot_id")return _level==BrowseLevel.Lots;
        if(name is "layer" or "layer_id" or "missing_fields" or "fov_count" or "total_defects")return false;
        if(name=="panel_no"&&_filter.InspectionId.Length>0&&_filter.Panel.HasValue)return false;
        if(_level==BrowseLevel.Defects)return new[]{"model_name","lot_id_name","layer_name","panel_no","machine_id","inspection_time","inspection","unit_idx","unit_x","unit_y","raw_x","raw_y","cad_x","cad_y","ert_cad_x","ert_cad_y","source_block_idx","source_axis_x","source_axis_y","defect_type","vms_state","vms_code","cause_key","defect_size","has_image","defects"}.Contains(name);
        return true;
    }
    private void AddCheckColumn()
    {
        _checkHeader=new CheckBox{IsThreeState=true,ToolTip="현재 표시된 행 전체 체크/해제. 검색 전체는 내보내기 창에서 선택합니다.",Margin=new Thickness(0)};
        _checkHeader.Click+=(_,_)=>SetVisibleChecks(_checkHeader.IsChecked==true);
        var check=new FrameworkElementFactory(typeof(CheckBox));check.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);check.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding("[__checked]"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        Results.Columns.Add(new DataGridTemplateColumn{Header=_checkHeader,CellTemplate=new DataTemplate{VisualTree=check},Width=38,MinWidth=38,CanUserSort=false,CanUserResize=false,CanUserReorder=false});
    }
    private void CheckedColumnChanged(object? sender,DataColumnChangeEventArgs e)
    {
        if(e.Column?.ColumnName!="__checked"||!ReferenceEquals(sender,_table))return;
        int index=(int)e.Row["__index"];if(index<0||index>=_rows.Count)return;var row=_rows[index];
        if(e.ProposedValue is true)_checkedRecords[CheckKey(row)]=row;else _checkedRecords.Remove(CheckKey(row));
        if(!_changingChecks){UpdateCheckedLabel();UpdateActions();}
    }
    private void SetVisibleChecks(bool selected)
    {
        if(_idleLocked||_queryCts is not null)return;
        _changingChecks=true;
        try{foreach(DataRow row in _table.Rows)row["__checked"]=selected;}
        finally{_changingChecks=false;UpdateCheckedLabel();UpdateActions();}
    }
    private void UpdateCheckedLabel()
    {
        if(CheckedLabel is null)return;
        string shown=Selected() is ResultRow row?_level switch{BrowseLevel.Models=>"열람: Model "+row.Text("model_name"),BrowseLevel.Lots=>"열람: LotID "+row.Text("lot_id_name"),BrowseLevel.Layers=>"열람: "+row.Text("layer_name"),BrowseLevel.Inspections=>"열람: 패널 "+row.Text("panel_no"),_=>"열람: 불량 "+row.Text("defects")}:"";
        CheckedLabel.Text=$"체크 {_checkedRecords.Count:N0}개"+(_checkedRecords.Count>0?" 보기…":"")+(shown.Length>0?" · "+shown:"");
        CheckedLabel.ToolTip=CheckedLabel.Text+" · 클릭해서 체크한 항목을 확인하고 해제합니다.";
        if(_checkHeader is not null){int count=_rows.Count(x=>_checkedRecords.ContainsKey(CheckKey(x)));_checkHeader.IsChecked=count==0?false:count==_rows.Count?true:null;}
    }
    private void RenderScopePath()
    {
        PathPanel.Children.Clear();var f=_filter;string mode=f.Target switch{SearchTarget.Lot=>"LOT 검색",SearchTarget.Model=>"모델 검색",_=>"모델/LOT 검색"};
        PathPanel.Children.Add(new TextBlock{Text=$"검색 방식: {mode}"+(f.Text.Length>0?$" ({f.Text})":""),FontSize=11,Margin=new Thickness(0,0,5,0)});
        if(_trail.Count>1)
        {
            var root=_trail[0];var back=new Button{Content="검색 결과",Padding=new Thickness(4,2,4,2),Margin=new Thickness(0),ToolTip="처음 검색했던 조건으로 돌아갑니다"};
            back.Click+=async(_,_)=>{if(_queryCts is null)await RunAsync(ct=>BeginQueryAsync(root.Filter,root.Level,root.Databases,ct,true,new[]{root},root.AllDatabases));};PathPanel.Children.Add(back);
        }
        void Part(string label,SearchFilter target,BrowseLevel level,bool enabled)
        {
            PathPanel.Children.Add(new TextBlock{Text=" / ",Foreground=System.Windows.Media.Brushes.Gray});
            var button=new Button{Content=label,Padding=new Thickness(4,2,4,2),Margin=new Thickness(0),BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,IsEnabled=enabled,ToolTip="이 범위의 목록으로 이동"};
            button.Click+=async(_,_)=>{if(_queryCts is null)await RunAsync(ct=>BeginQueryAsync(target,level,_queryDatabases.ToArray(),ct,allDatabases:_allDatabases));};PathPanel.Children.Add(button);
        }
        Part("Model ("+(f.Model.Length>0?f.Model:"전체")+")",f with{Lot="",LotId="",Layer="",LayerId="",InspectionId="",Panel=null,Text=""},f.Model.Length>0?BrowseLevel.Lots:BrowseLevel.Models,true);
        Part("LotID ("+(f.Lot.Length>0?f.Lot:"전체")+")",f with{Layer="",LayerId="",InspectionId="",Panel=null},f.Lot.Length>0?BrowseLevel.Layers:BrowseLevel.Lots,true);
        Part("Layer ("+(f.Layer.Length>0?f.Layer:"전체")+")",f with{InspectionId="",Panel=null},f.Layer.Length>0?BrowseLevel.Inspections:BrowseLevel.Layers,f.Lot.Length>0||f.Layer.Length>0);
        if(f.Panel.HasValue)PathPanel.Children.Add(new TextBlock{Text=$" / 패널 ({f.Panel})"+(f.InspectionId.Length>0?$" · 검사 Index {f.InspectionId}":""),FontSize=12});
        if(f.Layer.Length>0)
        {
            var row=new ResultRow{Values=new(){["layer_name"]=f.Layer}};EnrichLayer(row);
            PathPanel.Children.Add(new TextBlock{Text=$"  [레이어 코드 {row.Text("layer_side")}"+(row.Text("layer_direction").Length>0?" / 방향 코드 "+row.Text("layer_direction"):"")+$" / 프로파일 {row.Text("layer_recipe")} / 해상도 코드 {row.Text("layer_resolution_code")}]",FontSize=11,Foreground=System.Windows.Media.Brushes.SlateGray});
        }
    }
    private string? CurrentPanelId()=>_filter.InspectionId.Length>0?_filter.InspectionId:Selected()?.Text("inspection") is string id&&id.Length>0?id:null;
    private async void Bundle_Click(object sender,RoutedEventArgs e)
    {
        if(_idleLocked||_queryCts is not null)return;
        string? panelId=_level==BrowseLevel.Defects?CurrentPanelId():null;
        int checkedPanels=_checkedRecords.Values.Select(x=>(x.Database,Id:x.Text("inspection"))).Where(x=>x.Id.Length>0).Distinct().Count();
        var dialog=new BundleWindow(_checkedRecords.Count,_level,panelId,SummaryLabel.Text,checkedPanels,sender is MenuItem{Tag:"panel"}){Owner=this};
        if(ShowModal(()=>dialog.ShowDialog())!=true)return;
        List<BundleScope> scopes;
        try{scopes=CreateBundleScopes(dialog.Mode,panelId);}
        catch(Exception ex){Notice.Text="내보내기 범위 확인 실패: "+ex.Message;return;}
        string root=Path.Combine(dialog.Folder,"검사결과_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        await RunAsync(ct=>RunBundleAsync(root,scopes,dialog.Options,ct),true);
    }
    private List<BundleScope> CreateBundleScopes(BundleMode mode,string? panelId)
    {
        if(mode==BundleMode.Panel)
        {
            var row=Selected();string db=_queryDatabases.Count==1?_queryDatabases[0]:row?.Database??throw new InvalidDataException("패널의 출처 DB를 선택하세요.");
            if(panelId is null)throw new InvalidDataException("패널을 선택하세요.");
            return new(){new(db,new(){InspectionId=panelId})};
        }
        if(mode==BundleMode.Checked)return _checkedRecords.Values.Select(row=>new BundleScope(row.Database,InspectionBundleExporter.SelectionFilter(_filter,_level,row))).ToList();
        return _queryDatabases.Select(db=>new BundleScope(db,_filter,_level,true)).ToList();
    }
    private async Task RunBundleAsync(string root,IReadOnlyList<BundleScope> scopes,BundleOptions options,CancellationToken ct)
    {
        PrepareOutput(Path.Combine(root,".write-check"));
        try
        {
            var result=await InspectionBundleExporter.ExportAsync(_repository,_profile,root,scopes,options,SaveHandlerImageAsync,text=>Status.Text=text,ct);
            string state=result.Status switch{"completed"=>"완료","completed_with_limitations"=>"완료 · 복원 제한 있음","partial_failure"=>"일부 실패","empty"=>"해당 패널 없음",_=>result.Status};
            SetOutput(root,$"내보내기 {state} · 패널 {result.Panels:N0}개 / 불량 {result.Defects:N0}건 / ERT {result.ErtFiles:N0}개 / 이미지 {result.Images:N0}개 / 이미지 미기록 {result.MissingImages:N0}건 / 보조 파일 {result.SidecarFiles:N0}개 / 복원 제한 {result.Unrecoverable:N0}항목 / 실패 {result.Failures:N0}건 · 사유는 처리내역",true);
        }
        catch{_lastOutput=root;throw;}
    }
    private static async Task<string> SaveHandlerImageAsync(ResultRow row,ImagePayload image,string folder,CancellationToken ct)
    {
        Directory.CreateDirectory(folder);string source=row.Text("source_file_name").Replace('\\','/').Split('/')[^1];
        if(source.Length==0)source="defect_"+row.Text("defects");
        string extension=Path.GetExtension(source);if(new[]{".png",".bmp",".jpg",".jpeg",".tif",".tiff",".raw"}.Contains(extension,StringComparer.OrdinalIgnoreCase))source=source[..^extension.Length];
        source=InspectionBundleExporter.Component(source);
        string path=Path.Combine(folder,source+".png");if(File.Exists(path))path=Path.Combine(folder,source+"__D"+InspectionBundleExporter.Component(row.Text("defects"))+".png");
        // Preserve the original payload independently of a display conversion failure.
        string rawFolder=Path.Combine(folder,"_원본");Directory.CreateDirectory(rawFolder);
        string raw=Path.Combine(rawFolder,Path.GetFileNameWithoutExtension(path)+".bytes");
        await using(var file=new FileStream(raw,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true))await file.WriteAsync(image.Bytes,ct);
        await ExportService.WriteNewAsync(raw+".json",ExportService.Json(row),ct);
        var bitmap=BitmapDecoderService.Decode(image);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        string partial=path+".partial-"+Guid.NewGuid().ToString("N");using(var stream=new FileStream(partial,FileMode.CreateNew))png.Save(stream);
        ct.ThrowIfCancellationRequested();File.Move(partial,path,false);return path;
    }
}
