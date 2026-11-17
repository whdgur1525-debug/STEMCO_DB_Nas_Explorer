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
        if(name=="panel_no"&&_filter.Panel.HasValue)return false;
        if(name=="machine_id"&&_filter.MachineExact&&_filter.Machine.Length>0)return false;
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
        Breadcrumb.Text=$"{mode}"+(f.Text.Length>0?$" ‘{f.Text}’":"")+" · "+LevelNames[(int)_level]+" · DB: "+(_queryDatabases.Count==1?_queryDatabases[0]:$"전체 {_queryDatabases.Count:N0}개");
        if(_trail.Count>1)
        {
            var root=_trail[0];var back=new Button{Content="검색 결과",Padding=new Thickness(4,2,4,2),Margin=new Thickness(0),ToolTip="처음 검색했던 조건으로 돌아갑니다"};
            back.Click+=async(_,_)=>await ReturnToPathAsync(root,[root]);PathPanel.Children.Add(back);
        }
        void Part(string label,SearchFilter target,BrowseLevel level,bool enabled)
        {
            PathPanel.Children.Add(new TextBlock{Text=" / ",Foreground=System.Windows.Media.Brushes.Gray});
            var button=new Button{Content=label,Padding=new Thickness(6,4,6,4),Margin=new Thickness(0,3,0,0),BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.White,Foreground=new SolidColorBrush(Color.FromRgb(30,64,130)),FontWeight=FontWeights.SemiBold,IsEnabled=enabled,ToolTip=label+" · 이 범위의 목록으로 이동"};
            button.Click+=async(_,_)=>await ReturnToPathPartAsync(target,level);PathPanel.Children.Add(button);
        }
        Part("Model ("+(f.Model.Length>0?f.Model:"전체")+")",f with{Lot="",LotId="",Layer="",LayerId="",InspectionId="",Panel=null,Text=""},f.Model.Length>0?BrowseLevel.Lots:BrowseLevel.Models,true);
        Part("LotID ("+(f.Lot.Length>0?f.Lot:"전체")+")",f with{Layer="",LayerId="",InspectionId="",Panel=null},f.Lot.Length>0?BrowseLevel.Layers:BrowseLevel.Lots,true);
        Part("Layer ("+(f.Layer.Length>0?f.Layer:"전체")+")",f with{InspectionId="",Panel=null},f.Layer.Length>0?BrowseLevel.Inspections:BrowseLevel.Layers,f.Lot.Length>0||f.Layer.Length>0);
        if(f.MachineExact&&f.Machine.Length>0)PathPanel.Children.Add(new TextBlock{Text=" / 장비 ("+f.Machine+")",FontSize=12,FontWeight=FontWeights.SemiBold});
        if(f.Panel.HasValue)PathPanel.Children.Add(new TextBlock{Text=$" / 패널 ({f.Panel})"+(f.InspectionId.Length>0?$" · 검사 Index {f.InspectionId}":""),FontSize=12,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(Color.FromRgb(30,64,130))});
        if(f.Layer.Length>0)
        {
            var row=new ResultRow{Values=new(){["layer_name"]=f.Layer}};EnrichLayer(row);
            ScopeHeader.ToolTip=$"Model: {f.Model}\nLOT: {f.Lot}\nLayer: {f.Layer}\n레이어 코드 {row.Text("layer_side")} / 방향 {row.Text("layer_direction")} / 프로파일 {row.Text("layer_recipe")} / 해상도 코드 {row.Text("layer_resolution_code")}";
        }
        else ScopeHeader.ToolTip="파란 경로가 현재 표에 적용된 범위입니다. 경로를 클릭하면 해당 목록으로 돌아갑니다.";
    }
    private string? CurrentPanelId()=>_filter.InspectionId.Length>0?_filter.InspectionId:Selected()?.Text("inspection") is string id&&id.Length>0?id:null;
    private async void Bundle_Click(object sender,RoutedEventArgs e)
    {
        if(_idleLocked||_queryCts is not null)return;
        string? panelId=_level==BrowseLevel.Defects?CurrentPanelId():null;
        ResultRow? panelPreview=null;bool cancelled=false;
        if(panelId is not null)
        {
            string? database=_queryDatabases.Count==1?_queryDatabases[0]:Selected()?.Database;
            if(database is not null)await RunAsync(async ct=>
            {
                try{panelPreview=(await _repository.QueryAsync(_profile,database,BrowseLevel.Inspections,new(){InspectionId=panelId},1,null,ct)).Rows.SingleOrDefault();}
                catch(OperationCanceledException){cancelled=true;throw;}
                catch(Exception ex){Log("패널 수량 미리보기 실패 · 실제 대상은 다음 화면에서 확인: "+Aoi.NasExplorer.Data.ExplorerRepository.SafeError(ex));}
            });
        }
        if(cancelled||_idleLocked)return;
        var dialog=CreateBundleWindow(sender is MenuItem{Tag:"panel"},panelPreview);
        if(ShowModal(()=>dialog.ShowDialog())!=true)return;
        List<BundleScope> scopes;
        try{scopes=CreateBundleScopes(dialog.Mode,panelId);}
        catch(Exception ex){Notice.Text="내보내기 범위 확인 실패: "+ex.Message;return;}
        string root=Path.Combine(dialog.Folder,"검사결과_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        await RunAsync(async ct=>
        {
            if(dialog.Mode==BundleMode.Query)
            {
                var verified=(await ResolveInspectionDatabasesAsync(scopes.Select(scope=>scope.Database),ct)).ToHashSet(StringComparer.Ordinal);
                scopes=scopes.Where(scope=>verified.Contains(scope.Database)).ToList();
            }
            string description=DescribeBundleSelection(dialog.Mode,scopes,dialog.Options)+"\n\n"+dialog.SelectionDescription;
            await RunBundleAsync(root,scopes,dialog.Options,ct,plan=>
            {
            ct.ThrowIfCancellationRequested();
            string targets=description+"\n\n실제 대상 폴더 "+plan.FolderCount.ToString("N0")+"개\n"+string.Join("\n",plan.Folders);
            if(plan.UnknownIdentityPanels>0)targets+=$"\n식별 정보가 일부 미기록인 검사 {plan.UnknownIdentityPanels:N0}개도 위 검사 수에 포함되어 있으며 별도로 보존합니다.";
            if(plan.FolderCount>plan.Folders.Count)targets+=$"\n… 나머지 {plan.FolderCount-plan.Folders.Count:N0}개는 상세 기록의 패널목록에서 확인할 수 있습니다.";
            var confirmation=new BundleConfirmWindow(plan.PlannedPanels,dialog.Mode,targets,root,dialog.Options){Owner=this};
            bool accepted=ShowModal(()=>confirmation.ShowDialog())==true;ct.ThrowIfCancellationRequested();return Task.FromResult(accepted);
            });
        },true);
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
    private string? _lastDiagnosticFolder;
    private void OpenDiagnostics_Click(object sender,RoutedEventArgs e)
    {
        if(_lastDiagnosticFolder is null)return;
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=_lastDiagnosticFolder,UseShellExecute=true});}
        catch(Exception ex){Log("작업 기록 폴더 열기 실패: "+ex.Message);}
    }
    private void UpdateBundleProgress(BundleProgress progress)
    {
        ExportProgressPanel.Visibility=Visibility.Visible;
        bool planned=progress.Phase is not ("planning" or "selection");
        ExportProgress.IsIndeterminate=!planned||progress.PlannedPanels<=0;
        if(!ExportProgress.IsIndeterminate)
        {
            double panelFraction=progress.CurrentPanelExpected>0?Math.Min(0.95,(double)progress.CurrentPanelImages/progress.CurrentPanelExpected):0;
            ExportProgress.Maximum=progress.PlannedPanels;ExportProgress.Value=Math.Min(progress.PlannedPanels,progress.ProcessedPanels+panelFraction);
        }
    }
    private static string DescribeBundleSelection(BundleMode mode,IReadOnlyList<BundleScope> scopes,BundleOptions options)
    {
        var text=new System.Text.StringBuilder();
        text.AppendLine(mode switch{BundleMode.Query=>"범위: 현재 검색 전체 (아직 읽지 않은 항목 포함)",BundleMode.Checked=>"범위: 체크한 항목",_=>"범위: 현재 패널"});
        text.AppendLine(options.IncludeHistory?"검사 이력: 과거 검사도 포함":"검사 이력: 폴더는 패널별 최신 검사 / 명시한 검사 Index는 그대로");
        text.AppendLine("Model·LOT·Layer 폴더를 선택하면 해당 폴더의 전체 패널 불량을 저장합니다. 불량 코드·이미지 유무 등의 필터로 패널 안의 기록을 잘라내지 않습니다.");
        foreach(var scope in scopes)
        {
            var f=scope.Filter;text.AppendLine();text.AppendLine("DB: "+scope.Database);
            text.AppendLine($"Model: {(f.Model.Length>0?f.Model:"전체")} / LotID: {(f.Lot.Length>0?f.Lot:"전체")} / Layer: {(f.Layer.Length>0?f.Layer:"전체")}");
            if(f.InspectionId.Length>0)text.AppendLine("검사 Index: "+f.InspectionId);
            if(f.Panel.HasValue)text.AppendLine("패널: "+f.Panel);
            if(f.Machine.Length>0)text.AppendLine("장비: "+f.Machine);
            if(scope.ExpandQuery&&f.Text.Length>0)text.AppendLine("검색어: "+f.Text);
        }
        return text.ToString();
    }
    private async Task RunBundleAsync(string root,IReadOnlyList<BundleScope> scopes,BundleOptions options,CancellationToken ct,Func<BundlePlanSummary,Task<bool>>? confirm=null)
    {
        PrepareOutput(Path.Combine(root,".write-check"));
        try
        {
            var result=await InspectionBundleExporter.ExportAsync(_repository,_profile,root,scopes,options,SaveHandlerImageAsync,text=>{Status.Text=text;ExportProgressLabel.Text=text;},ct,UpdateBundleProgress,async(plan,_)=>
            {
                _lastDiagnosticFolder=plan.DiagnosticFolder;DiagnosticButton.IsEnabled=true;
                return confirm is null||await confirm(plan);
            });
            _lastDiagnosticFolder=result.DiagnosticFolder;Log("내보내기 작업 기록: "+result.DiagnosticFolder);
            string state=result.Status switch{"completed"=>"완료","completed_with_limitations"=>"완료 · 복원 제한 있음","partial_failure"=>"일부 실패","empty"=>"해당 패널 없음",_=>result.Status};
            SetOutput(root,$"내보내기 {state} · 패널 {result.Panels:N0}개 / 불량 {result.Defects:N0}건 / ERT {result.ErtFiles:N0}개 / 이미지 {result.Images:N0}개 (미기록 {result.MissingImages:N0}건) / Bar_result 파일 {result.BarResultFiles:N0}개 / Defect Point 파일 {result.DefectPointFiles:N0}개 / 미기록 정보 {result.Unrecoverable:N0}항목 / 실패 {result.Failures:N0}건 · 자세한 내용은 작업 기록",true);
            ExportProgressLabel.Text=Notice.Text;
        }
        catch(Exception ex){_lastOutput=root;if(ex.Data["DiagnosticFolder"] is string diagnostics){_lastDiagnosticFolder=diagnostics;DiagnosticButton.IsEnabled=true;Log("내보내기 작업 기록: "+diagnostics);}throw;}
    }
    private static async Task<string> SaveHandlerImageAsync(ResultRow row,ImagePayload image,string folder,CancellationToken ct)
    {
        Directory.CreateDirectory(folder);string source=row.Text("source_file_name").Replace('\\','/').Split('/')[^1];
        if(source.Length==0)source="defect_"+row.Text("defects");
        string extension=Path.GetExtension(source);if(new[]{".png",".bmp",".jpg",".jpeg",".tif",".tiff",".raw"}.Contains(extension,StringComparer.OrdinalIgnoreCase))source=source[..^extension.Length];
        source=InspectionBundleExporter.Component(source);
        string path=Path.Combine(folder,source+".png");if(File.Exists(path))path=Path.Combine(folder,source+"__D"+InspectionBundleExporter.Component(row.Text("defects"))+".png");
        // Bundle staging retains the original DB payload locally if conversion fails.
        // The operator's result folder contains only the requested image.
        string partial=path+".partial-"+Guid.NewGuid().ToString("N");
        try
        {
            var bitmap=BitmapDecoderService.Decode(image);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=new FileStream(partial,FileMode.CreateNew))png.Save(stream);
            ct.ThrowIfCancellationRequested();File.Move(partial,path,false);return path;
        }
        catch
        {
            string recovery=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synapse","NasExplorer","ImageFailures",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery);await File.WriteAllBytesAsync(Path.Combine(recovery,"original.bytes"),image.Bytes,CancellationToken.None);
            await ExportService.WriteNewAsync(Path.Combine(recovery,"source.json"),ExportService.Json(row),CancellationToken.None);throw;
        }
        finally{if(File.Exists(partial))File.Delete(partial);}
    }
}
