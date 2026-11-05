using System.Data;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Data;
using Aoi.NasExplorer.Export;
using Microsoft.Win32;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private sealed record Location(SearchFilter Filter,BrowseLevel Level,string[] Databases,bool AllDatabases=false);
    private sealed record NavigationState(Location Current,Location[] Trail)
    { public RowKey? SelectedKey {get;init;} }
    private readonly Stack<NavigationState> _forward=new();
    private readonly List<Location> _trail=[];
    private readonly DispatcherTimer _refreshTimer=new();
    private readonly List<ResultRow> _summaryRows=[];
    private readonly List<ResultRow> _lotSummaryRows=[];
    private readonly Dictionary<string,string?> _ertAvailability=new(StringComparer.Ordinal);
    private readonly List<object> _exportSnapshots=[];
    private DateTimeOffset? _lastRefresh;
    private DateTimeOffset? _lastRefreshAttempt;
    private DateTimeOffset _lastDiscoveryAt=DateTimeOffset.Now;
    private string? _lastOutput;
    private bool _experienceReady;
    private bool _detailLoading;
    private bool _allDatabases;
    private bool _restoringChecks;

    private void InitializeExperience()
    {
        _experienceReady=true;
        AutoRefreshBox.SelectedIndex=_view.RefreshSeconds switch{>0 and <300=>1,>=300=>2,_=>0};
        if(_view.RefreshSeconds>0)_view.RefreshSeconds=Math.Max(60,_view.RefreshSeconds);
        InitializeIdleSession();ScheduleAutoRefresh();
        void FitFilters()
        {
            bool small=ActualHeight<800;SearchHint.Visibility=small?Visibility.Collapsed:Visibility.Visible;DetailFilterScroll.MaxHeight=Math.Clamp(ActualHeight-650,70,180);ScopeScroll.MaxHeight=small?96:130;
            SummaryLabel.Margin=new Thickness(0,small?2:5,0,small?2:5);
            foreach(var label in new[]{Breadcrumb,SummaryLabel,ActiveFilterLabel,Notice}){label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;}
            OpenInspectionsButton.Visibility=OpenDefectsButton.Visibility=small?Visibility.Collapsed:Visibility.Visible;
            PanelSummary.MaxHeight=LotSummary.MaxHeight=small?70:180;
        }
        SizeChanged+=(_,_)=>FitFilters();FitFilters();
        DetailSearchExpander.Expanded+=(_,_)=>{if(ActualHeight<800)SummaryExpander.IsExpanded=false;};
        SummaryExpander.Expanded+=(_,_)=>{if(ActualHeight<800)DetailSearchExpander.IsExpanded=false;};
        _refreshTimer.Interval=TimeSpan.FromSeconds(1);
        _refreshTimer.Tick+=async(_,_)=>await AutoRefreshTickAsync();
        _refreshTimer.Start();
        PreviewKeyDown+=async(_,e)=>
        {
            if(Keyboard.Modifiers==ModifierKeys.Alt&&e.SystemKey is Key.Left or Key.Right)
            {e.Handled=true;await NavigateHistoryAsync(e.SystemKey==Key.Right);}
            else if(e.Key==Key.F5){e.Handled=true;await RefreshCurrentAsync();}
        };
        foreach(var b in new[]{CsvButton,SelectedImagesButton,AllImagesButton,ErtPreviewButton,ErtButton,JsonButton,SaveImageButton,BackButton,ForwardButton,SummaryCsvButton,OpenOutputButton})ToolTipService.SetShowOnDisabled(b,true);
        string[] columns=["source_database","model_name","lot_id_name","layer_name","machine_id","panel_no","inspection_count","defect_count","image_count","vms_ng_count","vms_pass_count"];
        string[] headers=["출처 DB","모델","LOT","레이어","장비","패널","검사 기록 수","검색 일치 불량","이미지 수","VMS 불량","VMS 통과"];
        for(int n=0;n<columns.Length;n++)PanelSummary.Columns.Add(new DataGridTextColumn{Header=headers[n],Binding=new System.Windows.Data.Binding("["+columns[n]+"]"),Width=DataGridLength.Auto,MinWidth=65});
        foreach(int n in new[]{0,1,2,6,7})LotSummary.Columns.Add(new DataGridTextColumn{Header=headers[n],Binding=new System.Windows.Data.Binding("["+columns[n]+"]"),Width=DataGridLength.Auto,MinWidth=65});
        UpdateActions();
    }
    private SearchTarget SelectedTarget()=>SearchTargetBox.SelectedIndex switch{0=>SearchTarget.Lot,1=>SearchTarget.Model,_=>SearchTarget.ModelAndLot};
    private SearchFilter CreateSearchFilter()
    {
        var filter=ReadFilter();
        bool inheritedMachine=_filter.MachineExact&&filter.Machine==_filter.Machine,inheritedPanel=_filter.MachineExact&&filter.Panel==_filter.Panel;
        return filter with{Model=filter.Model==_filter.Model?"":filter.Model,Lot=filter.Lot==_filter.Lot?"":filter.Lot,Layer=filter.Layer==_filter.Layer?"":filter.Layer,LotId="",LayerId="",InspectionId="",Machine=inheritedMachine?"":filter.Machine,MachineExact=false,Panel=inheritedPanel?null:filter.Panel};
    }
    private async Task SearchGlobalAsync()=>await RunAsync(ct=>BeginQueryAsync(CreateSearchFilter(),SelectedTarget()==SearchTarget.Model?BrowseLevel.Models:BrowseLevel.Lots,SelectedDatabases(),ct,allDatabases:ScopeBox.SelectedItem is Scope{Database:null}));
    private async void Filter_Click(object sender,RoutedEventArgs e)
    {
        if(_idleLocked||_queryCts is not null)return;
        await RunAsync(ct=>BeginQueryAsync(ReadFilter(),_level,SelectedDatabases(),ct,allDatabases:ScopeBox.SelectedItem is Scope{Database:null}));
        DetailSearchExpander.IsExpanded=false;
    }
    private async Task OpenSelectedAsync(BrowseLevel target)
    {
        if(_idleLocked||_queryCts is not null||Selected() is not ResultRow row)return;
        var child=Child(row,_level,_filter);
        await RunAsync(ct=>BeginQueryAsync(child.Filter,target,new[]{row.Database},ct));
    }
    private async void OpenInspections_Click(object sender,RoutedEventArgs e)=>await OpenSelectedAsync(BrowseLevel.Inspections);
    private async void OpenDefects_Click(object sender,RoutedEventArgs e)=>await OpenSelectedAsync(BrowseLevel.Defects);
    private NavigationState CaptureNavigation()=>new(new(_filter,_level,_queryDatabases.ToArray(),_allDatabases),_trail.ToArray()){SelectedKey=Selected()?.Key(_profile.Identity,_level)};
    private void ResetExperience(){_history.Clear();_forward.Clear();_trail.Clear();_ertAvailability.Clear();_summaryRows.Clear();}
    private void UpdatePath(SearchFilter filter,BrowseLevel level,string[] databases,Location[]? trail,bool allDatabases)
    {
        var next=new Location(filter,level,databases,allDatabases);
        if(trail is not null){_trail.Clear();_trail.AddRange(trail);}
        else if(_trail.Count>0&&level>_level&&databases.All(x=>_queryDatabases.Contains(x))&&filter.Text==_filter.Text&&filter.Target==_filter.Target&&(filter.Model.Length>0||filter.LotId.Length>0||filter.LayerId.Length>0||filter.InspectionId.Length>0))
            _trail.Add(next);
        else{_trail.Clear();_trail.Add(next);}
    }
    private async Task NavigateHistoryAsync(bool forward)
    {
        if(_idleLocked||_queryCts is not null)return;
        var from=forward?_forward:_history;var to=forward?_history:_forward;
        if(!from.TryPop(out var state))return;
        to.Push(CaptureNavigation());
        await RunAsync(async ct=>
        {
            await BeginQueryAsync(state.Current.Filter,state.Current.Level,state.Current.Databases,ct,false,state.Trail,state.Current.AllDatabases);
            if(state.SelectedKey is not null)
            {
                int index=_rows.FindIndex(x=>x.Key(_profile.Identity,_level)==state.SelectedKey);Results.SelectedIndex=index;
                if(index>=0)Results.ScrollIntoView(Results.SelectedItem);
                else Notice.Text+=" · 이전 열람 항목은 첫 페이지에 없습니다. 목록에서 다시 선택하세요.";
            }
        });
    }
    private async void Forward_Click(object sender,RoutedEventArgs e)=>await NavigateHistoryAsync(true);
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshCurrentAsync();
    private async Task RefreshCurrentAsync(bool rediscover=true)
    {
        if(_idleLocked&&!_resuming||_queryCts is not null||_queryDatabases.Count==0)return;
        var selection=Selected()?.Key(_profile.Identity,_level);var state=CaptureNavigation();var checks=_checkedRecords.ToArray();
        ClearImages();_ertAvailability.Clear();
        await RunAsync(async ct=>
        {
            var databases=state.Current.Databases;
            if(rediscover)
            {
                _lastDiscoveryAt=DateTimeOffset.Now;
                bool all=state.Current.AllDatabases;
                if(_repository is ExplorerRepository repo)repo.ClearSchemaCache();
                _databases=(await _repository.DiscoverAsync(_profile,ct)).ToList();ConfigureNavigation();
                var found=_databases.Where(x=>x.IsInspection).Select(x=>x.Name).ToArray();
                databases=all?found:databases.Where(found.Contains).ToArray();
            }
            _restoringChecks=true;
            try{await BeginQueryAsync(state.Current.Filter,state.Current.Level,databases,ct,false,state.Trail,state.Current.AllDatabases);}
            finally
            {
                _restoringChecks=false;var visible=_rows.ToDictionary(CheckKey,StringComparer.Ordinal);
                foreach(var entry in checks.Where(x=>databases.Contains(x.Value.Database)))_checkedRecords[entry.Key]=visible.GetValueOrDefault(entry.Key,entry.Value);
                ApplyCheckedReview(_checkedRecords.Keys.ToArray());
            }
            if(selection is not null)
            {
                int index=_rows.FindIndex(x=>x.Key(_profile.Identity,_level)==selection);
                if(index>=0){Results.SelectedIndex=index;Results.ScrollIntoView(Results.SelectedItem);}
                else Notice.Text+=" · 이전 선택은 첫 페이지에 없습니다. 목록에서 다시 선택하세요.";
            }
        });
    }
    private void AutoRefresh_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!_experienceReady)return;
        _view.RefreshSeconds=AutoRefreshBox.SelectedIndex switch{1=>60,2=>300,_=>0};
        ScheduleAutoRefresh();SaveView();UpdateRefreshLabel();
    }
    private async Task AutoRefreshTickAsync(bool? activeOverride=null)
    {
        if(await EnterIdleIfDueAsync())return;
        UpdateRefreshLabel();
        if(_view.RefreshSeconds<=0||_queryCts is not null||_modalDepth>0||!(activeOverride??IsActive)||_nextAutoRefreshAt is null||_queryDatabases.Count==0)return;
        // Do not overwrite an in-progress edit in search/filters or cancel a selected image load.
        if(Keyboard.FocusedElement is TextBox||_detailLoading||_checkedRecords.Count>0)return;
        try{if(ReadFilter()!=_filter||!SelectedDatabases().SequenceEqual(_queryDatabases))return;}catch(ArgumentException){return;}
        if(DateTimeOffset.Now>=_nextAutoRefreshAt.Value)await RefreshCurrentAsync(_allDatabases&&DateTimeOffset.Now-_lastDiscoveryAt>=TimeSpan.FromMinutes(5));
    }
    private void UpdateRefreshLabel()
    {
        if(!_experienceReady)return;
        RefreshLabel.Text=(_lastRefresh.HasValue?$"최근 조회 {_lastRefresh:HH:mm:ss}":"아직 조회하지 않음")+(_exporting?" · 내보내기 중 갱신 일시정지":_view.RefreshSeconds>0?$" · {_view.RefreshSeconds/60}분+분산 대기 · 10분 미사용 시 중지":" · 수동 갱신 (F5)");
    }
    private async void Databases_Click(object sender,RoutedEventArgs e)
    {
        await RunAsync(async ct=>
        {
            string[] old=_queryDatabases.ToArray();
            if(_repository is ExplorerRepository repo)repo.ClearSchemaCache();
            _databases=(await _repository.DiscoverAsync(_profile,ct)).ToList();ConfigureNavigation();
            var existing=old.Where(x=>_databases.Any(d=>d.IsInspection&&d.Name==x)).ToArray();
            await BeginQueryAsync(_filter,_level,existing.Length>0?existing:SelectedDatabases(),ct,false,_trail.ToArray());
        });
    }
    private async Task LoadSummaryAsync(CancellationToken ct)
    {
        _summaryRows.Clear();_lotSummaryRows.Clear();PanelSummary.ItemsSource=LotSummary.ItemsSource=null;SummaryLabel.Text="검색 전체 건수 집계 중…";
        long lots=0,inspections=0,defects=0,panels=0;var failures=new List<string>();
        foreach(var db in _queryDatabases)
        {
            try
            {
                var summary=await _repository.SummaryAsync(_profile,db,_filter,ct);ct.ThrowIfCancellationRequested();
                lots+=summary.Lots;inspections+=summary.Inspections;defects+=summary.Defects;panels+=summary.PanelGroups;
                _summaryRows.AddRange(summary.Panels);
                _lotSummaryRows.AddRange(summary.LotCounts);
            }
            catch(Exception ex) when(ex is not OperationCanceledException){failures.Add(db);Log("건수 집계 실패 · "+db+": "+ExplorerRepository.SafeError(ex));}
        }
        var table=new DataTable();
        string[] fields=["source_database","model_name","lot_id_name","layer_name","machine_id","panel_no","inspection_count","defect_count","image_count","vms_ng_count","vms_pass_count","lot_id","layer_id"];
        foreach(var field in fields)table.Columns.Add(field,typeof(object));
        foreach(var row in _summaryRows){var dr=table.NewRow();dr["source_database"]=row.Database;foreach(var field in fields.Skip(1))dr[field]=row[field]??DBNull.Value;table.Rows.Add(dr);}
        PanelSummary.ItemsSource=table.DefaultView;
        var lotTable=table.Clone();foreach(var row in _lotSummaryRows){var dr=lotTable.NewRow();dr["source_database"]=row.Database;foreach(var field in fields.Skip(1))dr[field]=row[field]??DBNull.Value;lotTable.Rows.Add(dr);}LotSummary.ItemsSource=lotTable.DefaultView;
        if(_level==BrowseLevel.Lots)
        {
            var counts=_lotSummaryRows.ToDictionary(x=>(x.Database,x.Text("lot_id")));
            foreach(var row in _rows)
            {if(counts.TryGetValue((row.Database,row.Text("lot_id")),out var count)){row.Values["inspection_count"]=count["inspection_count"];row.Values["defect_count"]=count["defect_count"];}}
            BindRows();
        }
        SummaryLabel.Text=$"검색 일치 불량 {defects:N0}건 · LOT {lots:N0}개 · 검사 기록 {inspections:N0}개 · 패널 그룹 {panels:N0}개"+(failures.Count>0?$" · 일부 집계 실패 ({failures.Count} DB)":"");
        ActiveFilterLabel.Text=DescribeFilters();
        Notice.Text=(_demo?"예제: 이름은 회수 자료 기준, 수량·좌표·이미지는 연습용입니다. ":"")+"건수는 검색 전체 · 재검 기록 포함 · 열 정렬은 현재 표시분 기준"+(_summaryRows.Count<panels?$" · 집계 표는 DB별 최대 1,000그룹 표시 (전체 {panels:N0}).":"");
        if(_failedDatabases.Count>0)Notice.Text+=" · 목록 조회 실패 DB 있음: 작업 기록 확인";
    }
    private async void PanelSummary_DoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(_queryCts is not null||ItemsControl.ContainerFromElement(PanelSummary,e.OriginalSource as DependencyObject) is not DataGridRow||PanelSummary.SelectedItem is not DataRowView row)return;
        string S(string field)=>Convert.ToString(row[field])??"";
        if(row["panel_no"] is DBNull||row["machine_id"] is DBNull){Notice.Text="장비·패널 번호가 미기록인 그룹입니다. 검사 이력에서 해당 검사 기록을 선택하세요.";return;}
        var f=_filter with{Model=S("model_name"),Lot=S("lot_id_name"),Layer=S("layer_name"),LotId=S("lot_id"),LayerId=S("layer_id"),Machine=S("machine_id"),MachineExact=true,Panel=Convert.ToInt32(row["panel_no"])};
        await RunAsync(ct=>BeginQueryAsync(f,BrowseLevel.Defects,new[]{S("source_database")},ct));
    }
    private async Task CheckErtAvailabilityAsync(CancellationToken ct)
    {
        foreach(var pair in SelectedRows().Select(x=>(Model:x.Text("model_name"),Layer:x.Text("layer_name"))).Distinct())
        {
            string key=pair.Model+"_Layer"+pair.Layer;
            if(_ertAvailability.ContainsKey(key))continue;
            try{await _repository.GeometryAsync(_profile,pair.Model,pair.Layer,ct);ct.ThrowIfCancellationRequested();_ertAvailability[key]=null;}
            catch(Exception ex) when(ex is not OperationCanceledException){_ertAvailability[key]="모델 자료 확인 필요: "+ExplorerRepository.SafeError(ex);}
        }
    }
    private string DescribeFilters()
    {
        var f=_filter;var parts=new List<string>();
        void Add(string name,object? value){if(value is not null&&Convert.ToString(value)?.Length>0)parts.Add(name+": "+value);}
        Add("장비",f.Machine);
        Add("시작일",f.From?.ToString("yyyy-MM-dd"));Add("종료일",f.UntilExclusive?.AddDays(-1).ToString("yyyy-MM-dd"));Add("AOI",f.DefectCode);Add("VMS 코드",f.VmsCode);Add("VMS 판정",f.VmsState);Add("AI",f.AiCode);Add("원인 코드",f.CauseCode);Add("원인",f.CauseText);Add("최소 크기",f.MinimumSize);Add("최대 크기",f.MaximumSize);
        if(f.Images!=ImageFilter.All)parts.Add(f.Images==ImageFilter.Present?"이미지 있음":"이미지 없음");if(f.LatestOnly)parts.Add("패널별 최신 기록");
        ActiveFilterLabel.Visibility=parts.Count==0?Visibility.Collapsed:Visibility.Visible;
        return parts.Count==0?"":"검색조건 · "+string.Join(" / ",parts);
    }
    private void UpdateActions()
    {
        if(!_experienceReady)return;
        bool busy=_queryCts is not null,ready=_queryDatabases.Count>0&&!busy;
        var selected=SelectedRows();
        void Enable(Button b,bool enabled,string reason){b.IsEnabled=enabled;b.ToolTip=reason;}
        Enable(BackButton,!busy&&_history.Count>0,"이전 검색·폴더로 돌아가기 (Alt+←)");
        Enable(ForwardButton,!busy&&_forward.Count>0,"앞으로 이동 (Alt+→)");
        Enable(OpenInspectionsButton,ready&&selected.Count==1&&_level<BrowseLevel.Inspections,"모델·LOT·레이어 하나를 선택하면 중간 폴더를 거치지 않고 검사 이력을 엽니다.");
        Enable(OpenDefectsButton,ready&&selected.Count==1&&_level<BrowseLevel.Defects,"선택한 범위의 불량을 바로 엽니다. 여러 검사·재검 기록이 포함될 수 있습니다.");
        Enable(CsvButton,ready&&_rows.Count>0,"현재 검색의 "+LevelNames[(int)_level]+" 전체를 CSV로 저장합니다. 화면에 읽힌 행 수로 제한되지 않습니다.");
        CsvButton.Content="CSV · "+LevelNames[(int)_level]+" 전체";
        Enable(JsonButton,ready&&selected.Count==1,"목록에서 기록 하나를 선택하세요.");
        Enable(SelectedImagesButton,ready&&_level==BrowseLevel.Defects&&selected.Any(x=>x["has_image"] is true),"불량 목록에서 이미지가 있는 행을 선택하세요. Ctrl/Shift로 여러 개 선택할 수 있습니다.");
        Enable(AllImagesButton,ready&&_level==BrowseLevel.Defects&&_rows.Count>0,"불량 목록의 검색조건에 맞는 전체 이미지를 저장합니다.");
        string? unavailable=selected.Select(x=>x.Text("model_name")+"_Layer"+x.Text("layer_name")).Select(x=>_ertAvailability.TryGetValue(x,out string? reason)?reason:"모델 자료 확인 중").FirstOrDefault(x=>x is not null);
        bool ert=ready&&_level==BrowseLevel.Inspections&&selected.Count>0&&unavailable is null;
        Enable(ErtButton,ert,unavailable??"검사 이력에서 저장할 검사를 선택하세요. 검사 ID별 전체 불량으로 ERT를 재구성합니다.");
        Enable(ErtPreviewButton,ert&&selected.Count==1,unavailable??"검사 이력에서 검사 하나를 선택하세요.");
        Enable(SaveImageButton,!busy&&_image is {Bytes.Length:>0},"표시 중인 이미지 원본과 열람용 PNG를 저장합니다.");
        Enable(SummaryCsvButton,ready&&(_summaryRows.Count>0||_lotSummaryRows.Count>0),"선택한 탭의 집계 표를 저장합니다. 각 표는 DB별 최대 1,000개입니다.");
        Enable(OpenOutputButton,!busy&&_lastOutput is not null,"최근 내보내기 저장 폴더를 엽니다.");
        RefreshButton.IsEnabled=ready;CancelButton.IsEnabled=busy;SearchButton.IsEnabled=!busy;DatabasesButton.IsEnabled=ready;MoreButton.IsEnabled=ready&&_hasMore;
        BundleButton.IsEnabled=ready&&(_rows.Count>0||_checkedRecords.Count>0);ReviewChecksButton.IsEnabled=ready&&_checkedRecords.Count>0;
        Navigation.IsEnabled=Results.IsEnabled=PanelSummary.IsEnabled=LotSummary.IsEnabled=PathPanel.IsEnabled=!busy;
        foreach(var control in new Control[]{ScopeBox,SearchTargetBox,SearchText,AutoRefreshBox,ModelText,LotText,LayerText,MachineText,PanelText,PcText,DefectText,VmsText,AiText,CauseCodeText,CauseText,MinSizeText,MaxSizeText,FromDate,ToDate,StateBox,ImageFilterBox})control.IsEnabled=!busy;
        UpdateRefreshLabel();
    }
    private void PrepareOutput(string path)
    {
        if(File.Exists(path))throw new IOException("같은 이름의 파일이 있습니다. 새 이름을 선택하세요: "+path);
        string dir=Path.GetDirectoryName(Path.GetFullPath(path))!;Directory.CreateDirectory(dir);
        string probe=Path.Combine(dir,".nas-write-check-"+Guid.NewGuid().ToString("N"));
        using var stream=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose);
    }
    private void SetOutput(string path,string message,bool folder=false)
    {
        _lastOutput=folder?path:Path.GetDirectoryName(Path.GetFullPath(path));
        Notice.Text=message+" · "+path;Log(Notice.Text);UpdateActions();
    }
    private void RecordSnapshot(string db,DateTimeOffset time){_exportSnapshots.Add(new{database=db,capturedAt=time});Log($"추출 스냅샷 · {db} · {time:O}");}
    private async IAsyncEnumerable<ResultRow> SelectedImageRows(IReadOnlyList<ResultRow> selected,[EnumeratorCancellation]CancellationToken ct)
    {
        foreach(var group in selected.GroupBy(x=>x.Database))
        {
            var ids=group.Select(x=>x.Text("defects")).ToHashSet(StringComparer.Ordinal);
            await foreach(var row in _repository.StreamAsync(_profile,group.Key,BrowseLevel.Defects,_filter,true,time=>RecordSnapshot(group.Key,time),ct,ids))yield return row;
        }
    }
    private async IAsyncEnumerable<ResultRow> ProgressRows(IAsyncEnumerable<ResultRow> source,[EnumeratorCancellation]CancellationToken ct)
    {
        long count=0;await foreach(var row in source.WithCancellation(ct)){if(++count%250==0)Status.Text=$"CSV 저장 중 · {count:N0}행 · 취소 가능";yield return row;}
    }
    private void OpenOutput_Click(object sender,RoutedEventArgs e)
    {
        if(_lastOutput is null)return;
        try{Process.Start(new ProcessStartInfo{FileName=_lastOutput,UseShellExecute=true});}
        catch(Exception ex){Notice.Text="저장 폴더 열기 실패: "+ex.Message;}
    }
    private async void SummaryCsv_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog{Filter="CSV 파일|*.csv",FileName=$"패널집계_{DateTime.Now:yyyyMMdd_HHmmss}.csv"};
        if(ShowModal(()=>dialog.ShowDialog(this))!=true)return;
        var rows=(SummaryTabs.SelectedIndex==1?_lotSummaryRows:_summaryRows).ToArray();
        await RunAsync(async ct=>{PrepareOutput(dialog.FileName);await ExportService.CsvAsync(dialog.FileName,ExportService.Enumerate(rows),ct);SetOutput(dialog.FileName,$"패널 집계 저장 완료 · {rows.Length:N0}그룹");},true);
    }
}
