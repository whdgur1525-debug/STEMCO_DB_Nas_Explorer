using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Data;
using Aoi.NasExplorer.Export;
using Microsoft.Win32;

namespace Aoi.NasExplorer.App;

public partial class MainWindow : Window
{
    private ConnectionProfile _profile=new();
    private IExplorerRepository _repository=new ExplorerRepository();
    private List<DatabaseInfo> _databases=[];
    private readonly List<ResultRow> _rows=[];
    private readonly HashSet<string> _failedDatabases=new(StringComparer.Ordinal);
    private readonly Dictionary<string,BitmapSource> _imageCache=new(StringComparer.Ordinal);
    private readonly Queue<string> _imageOrder=new();
    private long _cacheBytes;
    private DataTable _table=new();
    private CancellationTokenSource? _queryCts,_detailCts;
    private SearchFilter _filter=new();
    private BrowseLevel _level=BrowseLevel.Lots;
    private List<string> _queryDatabases=[];
    private int _databaseIndex;
    private string? _cursor;
    private bool _hasMore,_demo,_exporting;
    private readonly bool _smoke;
    private ViewPreferences _view=new();
    private ImagePayload? _image;
    private readonly Stack<(SearchFilter Filter,BrowseLevel Level,string[] Databases)> _history=new();
    private readonly List<(string Label,SearchFilter Filter,BrowseLevel Level,string[] Databases)> _favorites=[];
    private sealed record Scope(string Label,string? Database);
    private sealed record Nav(string Database,BrowseLevel Level,SearchFilter Filter,string Label);
    private static readonly string[] LevelNames=["모델","LOT","레이어","검사 이력","불량"];
    private static readonly Dictionary<string,string> Labels=new()
    {
        ["source_database"]="출처 DB",["model_name"]="모델",["lot_id_name"]="LOT",["layer_name"]="레이어",["machine_id"]="장비",["pc_no"]="PC",["panel_no"]="패널",
        ["lot_time"]="LOT 생성",["lot_status"]="LOT 상태",["inspection_time"]="검사 시각",["inspection_status"]="검사 상태",["vms_status"]="VMS 상태",["retest_count"]="재검 회차",
        ["unit_idx"]="유닛 ID",["unit_x"]="유닛 X",["unit_y"]="유닛 Y",["defect_type"]="AOI 코드",["vms_state"]="VMS 판정",["vms_code"]="VMS 코드",["ai_code"]="AI 코드",
        ["cause_code"]="원인 코드",["cause_key"]="원인",["defect_size"]="측정 크기",["defect_extra"]="보조 측정",["has_image"]="이미지",["image_size"]="이미지 바이트",["handler_tact_sec"]="Handler T/T (s)",["tact_ms"]="스캔 (ms)",
        ["defects"]="불량 ID",["inspection"]="검사 ID",["lot_id"]="LOT ID",["layer"]="레이어 ID",["missing_fields"]="미지원 컬럼"
    };
    public MainWindow(bool smoke=false)
    {
        InitializeComponent();_smoke=smoke;
        if(!smoke)try{_profile=ProfileStore.Load();_view=ProfileStore.LoadView();Width=Math.Clamp(_view.Width,1100,3000);Height=Math.Clamp(_view.Height,720,2000);}catch(Exception ex){Log("연결·화면 설정을 읽지 못했습니다: "+ex.Message);}
    }
    private void Log(string text)
    {
        string line=$"{DateTime.Now:HH:mm:ss}  {text}";
        if(LogBox.Text.Length>60000)LogBox.Text=LogBox.Text[^30000..];
        LogBox.AppendText(line+Environment.NewLine);LogBox.ScrollToEnd();
        if(!_smoke)try{ProfileStore.AppendLog(line);}catch(IOException){Status.Text="작업 로그 파일 저장 실패 · 화면 기록을 확인하세요";}
    }
    private async Task RunAsync(Func<CancellationToken,Task> action,bool export=false)
    {
        if(_exporting){Status.Text="추출 중입니다. 완료하거나 취소한 뒤 이동하세요.";return;}
        _queryCts?.Cancel();var cts=new CancellationTokenSource();_queryCts=cts;_exporting=export;
        Status.Text=export?"추출 중 · 취소 가능":"조회 중 · 취소 가능";MoreButton.IsEnabled=false;
        try{await action(cts.Token);if(ReferenceEquals(_queryCts,cts))Status.Text="완료 · "+CountLabel.Text;}
        catch(OperationCanceledException){if(ReferenceEquals(_queryCts,cts)){Status.Text="취소됨 · 부분 결과는 기록을 확인하세요";Log("작업 취소");}}
        catch(Exception ex){if(ReferenceEquals(_queryCts,cts)){string error=ExplorerRepository.SafeError(ex);Status.Text=error;Notice.Text=error;Log(error);}}
        finally{if(ReferenceEquals(_queryCts,cts)){_exporting=false;MoreButton.IsEnabled=_hasMore;_queryCts=null;}cts.Dispose();}
    }
    private async void Connect_Click(object sender,RoutedEventArgs e)
    {
        if(_exporting)return;
        var dialog=new ConnectionWindow(_profile){Owner=this};if(dialog.ShowDialog()!=true||dialog.Profile is null)return;
        var next=dialog.Profile;
        await RunAsync(async ct=>
        {
            var repo=new ExplorerRepository();var found=await repo.DiscoverAsync(next,ct);ct.ThrowIfCancellationRequested();
            _detailCts?.Cancel();if(_repository is IDisposable previousRepository)previousRepository.Dispose();
            _repository=repo;_profile=next;_demo=false;_databases=found.ToList();_history.Clear();ClearImages();LoadFavorites();
            ConnectionLabel.Text=$"{next.Name} · {next.Host}:{next.Port} · 읽기 전용";
            ProfileStore.Save(next,dialog.Remember);ConfigureNavigation();
            Log($"연결 완료 · 검사 DB {found.Count(x=>x.IsInspection)}개 / 확인 대상 {found.Count}개");
            foreach(var db in found.Where(x=>x.Error is not null))Log($"{db.Name}: {db.Error}");
            await BeginQueryAsync(ReadFilter(),(BrowseLevel)LevelBox.SelectedIndex,SelectedDatabases(),ct,false);
        });
    }
    public async Task ShowDemoAsync(bool defects=false)
    {
        await RunAsync(async ct=>
        {
            _detailCts?.Cancel();if(_repository is IDisposable previousRepository)previousRepository.Dispose();
            _demo=true;_repository=new DemoRepository();_databases=(await _repository.DiscoverAsync(_profile,ct)).ToList();ClearImages();_history.Clear();LoadFavorites();
            ConnectionLabel.Text="예제 데이터 · 실제 NAS 연결 없음";LevelBox.SelectedIndex=defects?4:1;ConfigureNavigation();
            await BeginQueryAsync(new(),defects?BrowseLevel.Defects:BrowseLevel.Lots,SelectedDatabases(),ct,false);
            if(defects&&Results.Items.Count>0)Results.SelectedIndex=0;
        });
    }
    private async void Demo_Click(object sender,RoutedEventArgs e)=>await ShowDemoAsync();
    private void ConfigureNavigation()
    {
        Navigation.Items.Clear();var scopes=new List<Scope>{new("전체 검사 DB",null)};
        foreach(var db in _databases)
        {
            var item=new TreeViewItem{Header=db.Name+"  · "+db.State,ToolTip=db.Error??"",IsEnabled=db.IsInspection};
            if(db.IsInspection){item.Tag=new Nav(db.Name,BrowseLevel.Models,new(),db.Name);item.Items.Add(new TreeViewItem{Header="불러오는 중…"});scopes.Add(new(db.Name,db.Name));}
            Navigation.Items.Add(item);
        }
        ScopeBox.ItemsSource=scopes;ScopeBox.SelectedIndex=0;
        var favorites=new TreeViewItem{Header="★ 즐겨찾기"};
        foreach(var f in _favorites){var child=new TreeViewItem{Header=f.Label};child.Tag=f;favorites.Items.Add(child);}Navigation.Items.Add(favorites);
    }
    private async void Navigation_Expanded(object sender,RoutedEventArgs e)
    {
        if(e.OriginalSource is not TreeViewItem item||item.Tag is not Nav node||item.Items.Count!=1||item.Items[0] is not TreeViewItem stub||stub.Tag is not null)return;
        e.Handled=true;item.Items.Clear();
        try{await PopulateNodeAsync(item,node,null,CancellationToken.None);}catch(Exception ex){item.Items.Add(new TreeViewItem{Header="조회 실패 · 다시 검색",ToolTip=ExplorerRepository.SafeError(ex)});Log(ExplorerRepository.SafeError(ex));}
    }
    private async Task PopulateNodeAsync(TreeViewItem item,Nav node,string? cursor,CancellationToken ct)
    {
        var page=await _repository.QueryAsync(_profile,node.Database,node.Level,node.Filter,100,cursor,ct);
        foreach(var row in page.Rows)
        {
            var next=Child(row,node.Level,node.Filter);
            string label=node.Level switch{BrowseLevel.Models=>row.Text("model_name"),BrowseLevel.Lots=>row.Text("lot_id_name"),BrowseLevel.Layers=>row.Text("layer_name"),_=>$"P{row.Text("panel_no")} / PC{row.Text("pc_no")} · {row.Text("inspection_time")}"};
            var child=new TreeViewItem{Header=label,Tag=new Nav(node.Database,next.Level,next.Filter,label)};
            if(next.Level<BrowseLevel.Defects)child.Items.Add(new TreeViewItem{Header="불러오는 중…"});item.Items.Add(child);
        }
        if(page.HasMore)
        {
            var more=new TreeViewItem{Header="다음 100개…"};
            more.Selected+=async (_,ev)=>{ev.Handled=true;item.Items.Remove(more);try{await PopulateNodeAsync(item,node,page.Cursor,CancellationToken.None);}catch(Exception ex){Log(ExplorerRepository.SafeError(ex));}};
            item.Items.Add(more);
        }
    }
    private async void Navigation_Selected(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if(_exporting)return;
        if(e.NewValue is not TreeViewItem item)return;
        if(item.Tag is Nav n)await RunAsync(ct=>BeginQueryAsync(n.Filter,n.Level,new[]{n.Database},ct));
        else if(item.Tag is ValueTuple<string,SearchFilter,BrowseLevel,string[]> f)await RunAsync(ct=>BeginQueryAsync(f.Item2,f.Item3,f.Item4,ct));
    }
    private static (SearchFilter Filter,BrowseLevel Level) Child(ResultRow row,BrowseLevel level,SearchFilter f)=>level switch
    {
        BrowseLevel.Models=>(f with{Model=row.Text("model_name")},BrowseLevel.Lots),
        BrowseLevel.Lots=>(f with{Model=row.Text("model_name"),Lot=row.Text("lot_id_name"),LotId=row.Text("lot_id")},BrowseLevel.Layers),
        BrowseLevel.Layers=>(f with{Layer=row.Text("layer_name"),LayerId=row.Text("layer")},BrowseLevel.Inspections),
        BrowseLevel.Inspections=>(f with{InspectionId=row.Text("inspection"),Panel=null,Pc=null,LatestOnly=false},BrowseLevel.Defects),
        _=>(f,level)
    };
    private SearchFilter ReadFilter()
    {
        static int? Int(TextBox t)=>string.IsNullOrWhiteSpace(t.Text)?null:int.TryParse(t.Text,out int n)?n:throw new ArgumentException("번호·코드는 정수로 입력하세요.");
        static double? Num(TextBox t)=>string.IsNullOrWhiteSpace(t.Text)?null:double.TryParse(t.Text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double n)?n:throw new ArgumentException("크기는 소수점 숫자로 입력하세요.");
        var f=new SearchFilter{Text=SearchText.Text.Trim(),Model=ModelText.Text.Trim(),Lot=LotText.Text.Trim(),Layer=LayerText.Text.Trim(),Machine=MachineText.Text.Trim(),Panel=Int(PanelText),Pc=Int(PcText),DefectCode=Int(DefectText),VmsCode=Int(VmsText),AiCode=Int(AiText),CauseCode=Int(CauseCodeText),CauseText=CauseText.Text.Trim(),MinimumSize=Num(MinSizeText),MaximumSize=Num(MaxSizeText),From=FromDate.SelectedDate,UntilExclusive=ToDate.SelectedDate?.AddDays(1),VmsState=StateBox.SelectedIndex<=0?null:StateBox.SelectedIndex-1,Images=(ImageFilter)ImageFilterBox.SelectedIndex,LatestOnly=LatestCheck.IsChecked==true};f.Validate();return f;
    }
    private string[] SelectedDatabases()=>ScopeBox.SelectedItem is Scope {Database:not null} scope?new[]{scope.Database}:_databases.Where(x=>x.IsInspection).Select(x=>x.Name).ToArray();
    private async void Search_Click(object sender,RoutedEventArgs e)=>await RunAsync(ct=>BeginQueryAsync(ReadFilter(),(BrowseLevel)Math.Max(0,LevelBox.SelectedIndex),SelectedDatabases(),ct));
    private async void SearchText_KeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;await RunAsync(ct=>BeginQueryAsync(ReadFilter(),(BrowseLevel)Math.Max(0,LevelBox.SelectedIndex),SelectedDatabases(),ct));}}
    private async Task BeginQueryAsync(SearchFilter filter,BrowseLevel level,IEnumerable<string> databases,CancellationToken ct,bool history=true)
    {
        if(history&&_queryDatabases.Count>0)_history.Push((_filter,_level,_queryDatabases.ToArray()));
        _detailCts?.Cancel();_filter=filter;_level=level;LevelBox.SelectedIndex=(int)level;_queryDatabases=databases.ToList();_databaseIndex=0;_cursor=null;_hasMore=_queryDatabases.Count>0;_rows.Clear();_failedDatabases.Clear();ThumbnailPanel.Children.Clear();
        DefectImage.Source=null;_image=null;ImagePlaceholder.Visibility=Visibility.Visible;Details.ItemsSource=null;
        Breadcrumb.Text=$"{(_queryDatabases.Count==1?_queryDatabases[0]:"전체 DB")}  /  {string.Join(" / ",new[]{filter.Model,filter.Lot,filter.Layer}.Where(x=>x.Length>0))}  /  {LevelNames[(int)level]}";
        Notice.Text=_demo?"예제 데이터입니다. 실제 검사 결과가 아닙니다.":"DB별 순차 조회 · 열 정렬은 현재 표시된 목록에 적용 · NULL / 없는 컬럼은 상세에서 구분";
        BindRows();await LoadPageAsync(ct);
    }
    private async Task LoadPageAsync(CancellationToken ct)
    {
        if(_queryDatabases.Count==0){Notice.Text="연결 가능한 검사 DB가 없습니다. NAS 연결과 DB 권한을 확인하세요.";_hasMore=false;return;}
        while(_databaseIndex<_queryDatabases.Count)
        {
            string db=_queryDatabases[_databaseIndex];
            try
            {
                var page=await _repository.QueryAsync(_profile,db,_level,_filter,250,_cursor,ct);ct.ThrowIfCancellationRequested();
                if(_rows.Count>=5000){_rows.Clear();Log("표시 목록 5,000개를 넘겨 다음 구간으로 이동했습니다. 전체 결과는 CSV로 추출할 수 있습니다.");}
                _rows.AddRange(page.Rows);_cursor=page.Cursor;
                if(!page.HasMore){_databaseIndex++;_cursor=null;}
                _hasMore=_databaseIndex<_queryDatabases.Count;BindRows();
                if(page.Rows.Count>0)break;
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            {_failedDatabases.Add(db);Log($"{db} 조회 실패: {ExplorerRepository.SafeError(ex)}");Notice.Text=$"일부 DB 조회 실패: {string.Join(", ",_failedDatabases)} · 작업 기록 확인";_databaseIndex++;_cursor=null;}
        }
        _hasMore=_databaseIndex<_queryDatabases.Count;MoreButton.IsEnabled=_hasMore;
        CountLabel.Text=$"현재 목록 {_rows.Count:N0}개 · {(_hasMore?"추가 결과 있음":"조회 범위 끝")}"+(_failedDatabases.Count>0?$" · 실패 DB {_failedDatabases.Count}개":"");
    }
    private void BindRows()
    {
        RememberColumns();
        _table=new DataTable();_table.Columns.Add("__index",typeof(int));_table.Columns.Add("source_database",typeof(string));
        foreach(string name in _rows.SelectMany(x=>x.Values.Keys).Distinct(StringComparer.Ordinal))_table.Columns.Add(name,typeof(object));
        for(int i=0;i<_rows.Count;i++){var dr=_table.NewRow();dr["__index"]=i;dr["source_database"]=_rows[i].Database;foreach(var pair in _rows[i].Values)dr[pair.Key]=pair.Value??DBNull.Value;_table.Rows.Add(dr);}
        Results.Columns.Clear();
        string[] preferred=["model_name","lot_id_name","layer_name","panel_no","pc_no","machine_id","inspection_time","defect_type","vms_state","vms_code","cause_key","defect_size","has_image","inspection_status","lot_time","lot_status","vms_status","retest_count","handler_tact_sec","tact_ms","unit_idx","unit_x","unit_y","source_database"];
        foreach(DataColumn column in _table.Columns.Cast<DataColumn>().OrderBy(x=>Array.IndexOf(preferred,x.ColumnName) is int index&&index>=0?index:1000+x.Ordinal))
        {
            if(column.ColumnName=="__index")continue;
            var binding=new System.Windows.Data.Binding("["+column.ColumnName+"]"){TargetNullValue="—",Converter=DisplayConverter.Instance};
            var width=_view.ColumnWidths.TryGetValue(column.ColumnName,out double saved)?new DataGridLength(Math.Clamp(saved,65,600)):DataGridLength.Auto;
            Results.Columns.Add(new DataGridTextColumn{Header=Labels.GetValueOrDefault(column.ColumnName,column.ColumnName),Binding=binding,SortMemberPath=column.ColumnName,MinWidth=65,MaxWidth=600,Width=width});
        }
        Results.ItemsSource=_table.DefaultView;
    }
    private ResultRow? Selected()=>Results.SelectedItem is DataRowView r&&ReferenceEquals(r.DataView.Table,_table)&&(int)r["__index"] is int i&&i>=0&&i<_rows.Count?_rows[i]:null;
    private List<ResultRow> SelectedRows()=>Results.SelectedItems.Cast<DataRowView>().Where(r=>ReferenceEquals(r.DataView.Table,_table)&&(int)r["__index"]>=0&&(int)r["__index"]<_rows.Count).Select(r=>_rows[(int)r["__index"]]).ToList();
    private async void More_Click(object sender,RoutedEventArgs e)=>await RunAsync(LoadPageAsync);
    private async void Results_DoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(ItemsControl.ContainerFromElement(Results,e.OriginalSource as DependencyObject) is not DataGridRow||_level==BrowseLevel.Defects||Selected() is not ResultRow row)return;
        var child=Child(row,_level,_filter);await RunAsync(ct=>BeginQueryAsync(child.Filter,child.Level,new[]{row.Database},ct));
    }
    private async void Results_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        _detailCts?.Cancel();var cts=new CancellationTokenSource();_detailCts=cts;
        if(Selected() is not ResultRow row)return;
        _image=null;DefectImage.Source=null;ImagePlaceholder.Visibility=Visibility.Visible;ImagePlaceholder.Text="선택한 기록 읽는 중…";
        try
        {
            var key=row.Key(_profile.Identity,_level);
            var detail=_level==BrowseLevel.Models?row:await _repository.DetailAsync(_profile,key,cts.Token);cts.Token.ThrowIfCancellationRequested();
            var fields=detail.Values.Select(x=>new{Field=x.Key,Value=x.Value is null?"NULL · 값 미기록":ResultRow.ValueText(x.Value)}).ToList();
            if(_level==BrowseLevel.Defects)
                foreach(string optional in new[]{"cause_code","cause_key","defect_size","defect_extra","source_metadata_version","image_width","image_height","image_channels"})
                    if(!detail.Values.ContainsKey(optional))fields.Add(new{Field=optional,Value="컬럼 없음 · 구형 DB"});
            Details.ItemsSource=fields;DetailSource.Text=$"{row.Database} / {key.Table} / {key.Id}\n{(_demo?"예제 데이터":"DB에 저장된 원본 필드 · 이미지 BYTEA 제외")}";
            if(_level!=BrowseLevel.Defects){ImagePlaceholder.Text="불량 목록을 열면 이미지를 볼 수 있습니다";return;}
            var loadedImage=await _repository.ImageAsync(_profile,key,cts.Token);cts.Token.ThrowIfCancellationRequested();_image=loadedImage;
            if(_image is null||_image.Bytes.Length==0){ImagePlaceholder.Text="이 기록에는 저장된 이미지가 없습니다";ImageInfo.Text="이미지 없음 · 원본 상세는 확인 가능";return;}
            var bitmap=CachedBitmap(_image);cts.Token.ThrowIfCancellationRequested();DefectImage.Source=bitmap;ImagePlaceholder.Visibility=Visibility.Collapsed;FitImage();
            ImageInfo.Text=$"{bitmap.PixelWidth} × {bitmap.PixelHeight} · {_image.Bytes.Length:N0} bytes · {_image.Channels}채널";
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(ReferenceEquals(_detailCts,cts)){ImagePlaceholder.Text=ExplorerRepository.SafeError(ex);Log(ExplorerRepository.SafeError(ex));}}
    }
    private BitmapSource CachedBitmap(ImagePayload payload)
    {
        // Cache only decoded pixels. Bytes are owned by the current selection and are not duplicated in this cache.
        string key=$"{_profile.Identity}/{payload.Database}/{payload.DefectId}/{payload.Bytes.Length}";
        if(_imageCache.TryGetValue(key,out var cached))return cached;
        var image=BitmapDecoderService.Decode(payload);long bytes=(long)image.PixelWidth*image.PixelHeight*Math.Max(1,(image.Format.BitsPerPixel+7)/8);
        const long budget=32*1024*1024;
        while(_cacheBytes+bytes>budget&&_imageOrder.Count>0)
        {string oldest=_imageOrder.Dequeue();if(_imageCache.Remove(oldest,out var old))_cacheBytes-=(long)old.PixelWidth*old.PixelHeight*Math.Max(1,(old.Format.BitsPerPixel+7)/8);}
        if(bytes<=budget){_imageCache[key]=image;_imageOrder.Enqueue(key);_cacheBytes+=bytes;}
        return image;
    }
    private void ClearImages(){_imageCache.Clear();_imageOrder.Clear();_cacheBytes=0;_image=null;}
    private void FitImage(){if(DefectImage.Source is BitmapSource b){double zoom=Math.Min(Math.Max(1,ImageScroll.ActualWidth-20)/b.PixelWidth,Math.Max(1,ImageScroll.ActualHeight-20)/b.PixelHeight);SetZoom(Math.Min(8,zoom));}}
    private void SetZoom(double n){ImageScale.ScaleX=ImageScale.ScaleY=Math.Clamp(n,0.05,24);}
    private void Image_MouseWheel(object sender,MouseWheelEventArgs e){SetZoom(ImageScale.ScaleX*(e.Delta>0?1.2:1/1.2));e.Handled=true;}
    private void ZoomOut_Click(object sender,RoutedEventArgs e)=>SetZoom(ImageScale.ScaleX/1.25);
    private void ZoomIn_Click(object sender,RoutedEventArgs e)=>SetZoom(ImageScale.ScaleX*1.25);
    private void Actual_Click(object sender,RoutedEventArgs e)=>SetZoom(1);
    private void Fit_Click(object sender,RoutedEventArgs e)=>FitImage();
    private void Cancel_Click(object sender,RoutedEventArgs e){_queryCts?.Cancel();_detailCts?.Cancel();}
    private async void Back_Click(object sender,RoutedEventArgs e){if(_history.TryPop(out var previous))await RunAsync(ct=>BeginQueryAsync(previous.Filter,previous.Level,previous.Databases,ct,false));}
    private void Favorite_Click(object sender,RoutedEventArgs e)
    {
        if(_queryDatabases.Count==0)return;
        var favorite=new SavedFavorite(_demo?"demo":_profile.Identity,Breadcrumb.Text,_filter,_level,_queryDatabases.ToArray());
        if(!_view.Favorites.Any(x=>x.Source==favorite.Source&&x.Label==favorite.Label&&x.Filter==favorite.Filter))_view.Favorites.Add(favorite);
        LoadFavorites();ConfigureNavigation();SaveView();Log("현재 검색을 즐겨찾기에 저장했습니다.");
    }
    private void LoadFavorites(){_favorites.Clear();foreach(var f in _view.Favorites.Where(x=>x.Source==(_demo?"demo":_profile.Identity)))_favorites.Add((f.Label,f.Filter,f.Level,f.Databases));}
    private void RememberColumns(){foreach(var col in Results.Columns)if(col.ActualWidth>=65&&!string.IsNullOrEmpty(col.SortMemberPath))_view.ColumnWidths[col.SortMemberPath]=col.ActualWidth;}
    private void SaveView(){if(_smoke)return;try{RememberColumns();if(WindowState==WindowState.Normal){_view.Width=Width;_view.Height=Height;}ProfileStore.SaveView(_view);}catch(Exception ex){Log("화면 설정 저장 실패: "+ex.Message);}}
    private void Clear_Click(object sender,RoutedEventArgs e)
    {
        foreach(var t in new[]{SearchText,ModelText,LotText,LayerText,MachineText,PanelText,PcText,DefectText,VmsText,AiText,CauseCodeText,CauseText,MinSizeText,MaxSizeText})t.Clear();
        FromDate.SelectedDate=ToDate.SelectedDate=null;StateBox.SelectedIndex=ImageFilterBox.SelectedIndex=0;LatestCheck.IsChecked=false;
    }
    private string? PickFolder(string title)
    {var dialog=new OpenFolderDialog{Title=title,Multiselect=false};return dialog.ShowDialog(this)==true?dialog.FolderName:null;}
    private async IAsyncEnumerable<ResultRow> AllRows(SearchFilter filter,BrowseLevel level,string[] databases,[EnumeratorCancellation]CancellationToken ct)
    {
        var repo=_repository;var profile=_profile;
        foreach(string db in databases)
        {
            string? cursor=null;bool more;
            do
            {
                var page=await repo.QueryAsync(profile,db,level,filter,500,cursor,ct);
                foreach(var row in page.Rows){ct.ThrowIfCancellationRequested();yield return row;}
                more=page.HasMore;cursor=page.Cursor;
            }while(more);
        }
    }
    private async void ExportCsv_Click(object sender,RoutedEventArgs e)
    {
        if(_queryDatabases.Count==0)return;
        var dialog=new SaveFileDialog{Filter="CSV 파일|*.csv",FileName=$"검사결과_{DateTime.Now:yyyyMMdd_HHmmss}.csv",OverwritePrompt=true};if(dialog.ShowDialog(this)!=true)return;
        if(File.Exists(dialog.FileName)){Notice.Text="기존 파일을 보존하려면 새 파일명을 선택하세요.";return;}
        var f=_filter;var level=_level;var dbs=_queryDatabases.ToArray();string path=dialog.FileName;
        await RunAsync(async ct=>
        {
            var manifest=new{startedAt=DateTimeOffset.Now,source=_demo?"demo":_profile.Identity,databases=dbs,level=level.ToString(),filter=f,consistency="DB별 순차 읽기. 전체 DB 동시 스냅샷 아님."};
            await ExportService.WriteNewAsync(path+".request.json",JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}),ct);
            try{long count=await ExportService.CsvAsync(path,AllRows(f,level,dbs,ct),ct);Log($"CSV 전체 추출 완료 · {count:N0}행 · {path}");await File.WriteAllTextAsync(path+".result.json",JsonSerializer.Serialize(new{status="completed",rows=count,endedAt=DateTimeOffset.Now}),CancellationToken.None);}
            catch(Exception ex){await File.WriteAllTextAsync(path+".result.json",JsonSerializer.Serialize(new{status=ex is OperationCanceledException?"cancelled":"failed",error=ExplorerRepository.SafeError(ex),endedAt=DateTimeOffset.Now}),CancellationToken.None);throw;}
        },true);
    }
    private async void ExportJson_Click(object sender,RoutedEventArgs e)
    {
        if(Selected() is not ResultRow selected)return;
        var dialog=new SaveFileDialog{Filter="JSON 파일|*.json",FileName="원본_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".json"};if(dialog.ShowDialog(this)!=true)return;
        var level=_level;
        await RunAsync(async ct=>{var detail=level==BrowseLevel.Models?selected:await _repository.DetailAsync(_profile,selected.Key(_profile.Identity,level),ct);await ExportService.WriteNewAsync(dialog.FileName,ExportService.Json(detail),ct);Log("원본 JSON 저장: "+dialog.FileName);},true);
    }
    private async void SaveImage_Click(object sender,RoutedEventArgs e)
    {
        if(_image is not ImagePayload image)return;string? folder=PickFolder("선택 이미지 원본 저장");if(folder is null)return;
        await RunAsync(async ct=>{string path=await SaveImageAsync(image,folder,ct);Log("이미지 원본 저장: "+path);},true);
    }
    private static async Task<string> SaveImageAsync(ImagePayload image,string folder,CancellationToken ct)
    {
        byte[] bytes=ImageBytes.Normalize(image.Bytes,image.Width,image.Height,image.Channels);
        bool raw=image.Width>0&&image.Height>0&&(long)image.Width*image.Height*image.Channels==bytes.Length;
        string extension=raw?".raw":ImageBytes.Extension(bytes);
        string path=Path.Combine(folder,ExportService.SafeComponent(image.Database),ExportService.SafeComponent(image.DefectId)+extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536,true))await stream.WriteAsync(bytes,ct);
        await ExportService.WriteNewAsync(path+".json",JsonSerializer.Serialize(new{image.Database,image.DefectId,image.Width,image.Height,image.Channels,bytes=bytes.Length,format=extension}),ct);
        // An additional PNG is a viewing copy; original bytes and dimensions are always kept.
        if(raw)
        {
            try{var bitmap=BitmapDecoderService.Decode(image);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var png=new FileStream(path+".png",FileMode.CreateNew);encoder.Save(png);}
            catch(Exception ex) when(ex is InvalidDataException or NotSupportedException){await File.WriteAllTextAsync(path+".view-error.txt",ex.Message,CancellationToken.None);}
        }
        return path;
    }
    private async void ExportImages_Click(object sender,RoutedEventArgs e)
    {
        if(_level!=BrowseLevel.Defects){Notice.Text="불량 목록에서 추출할 행을 선택하세요.";return;}
        var selected=SelectedRows();if(selected.Count==0)return;string? folder=PickFolder("선택 이미지 저장 폴더");if(folder is null)return;
        string root=Path.Combine(folder,"이미지_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        await RunAsync(ct=>ExportImagesCoreAsync(root,ExportService.Enumerate(selected),ct),true);
    }
    private async void ExportAllImages_Click(object sender,RoutedEventArgs e)
    {
        if(_level!=BrowseLevel.Defects||_queryDatabases.Count==0){Notice.Text="불량 목록에서 검색 범위를 정한 뒤 추출하세요.";return;}
        string? folder=PickFolder("검색 조건에 맞는 전체 이미지 저장");if(folder is null)return;
        string root=Path.Combine(folder,"전체이미지_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        var filter=_filter;var dbs=_queryDatabases.ToArray();
        await RunAsync(async ct=>
        {
            await ExportService.WriteNewAsync(Path.Combine(root,"검색조건.json"),JsonSerializer.Serialize(new{filter,databases=dbs,startedAt=DateTimeOffset.Now,consistency="DB별 순차 읽기"},new JsonSerializerOptions{WriteIndented=true}),ct);
            await ExportImagesCoreAsync(root,AllRows(filter,BrowseLevel.Defects,dbs,ct),ct);
        },true);
    }
    private async Task ExportImagesCoreAsync(string root,IAsyncEnumerable<ResultRow> source,CancellationToken ct)
    {
        Directory.CreateDirectory(root);int ok=0,missing=0,failed=0;string status="running";
        await using var log=new StreamWriter(Path.Combine(root,"처리내역.jsonl"),false,new UTF8Encoding(true)){AutoFlush=true};
        try
        {
            await foreach(var row in source.WithCancellation(ct))
            {
                try{var image=await _repository.ImageAsync(_profile,row.Key(_profile.Identity,BrowseLevel.Defects),ct);string? path=image is null?null:await SaveImageAsync(image,root,ct);if(path is null)missing++;else ok++;await log.WriteLineAsync(JsonSerializer.Serialize(new{row.Database,id=row.Text("defects"),status=path is null?"no_image":"saved",path}));}
                catch(Exception ex) when(ex is not OperationCanceledException){failed++;await log.WriteLineAsync(JsonSerializer.Serialize(new{row.Database,id=row.Text("defects"),status="failed",error=ExplorerRepository.SafeError(ex)}));}
                Status.Text=$"이미지 추출 · 저장 {ok:N0} / 없음 {missing:N0} / 실패 {failed:N0}";
            }
            status=failed==0?"completed":"partial_failure";
        }
        catch(OperationCanceledException){status="cancelled";throw;}
        catch{status="failed";throw;}
        finally{await File.WriteAllTextAsync(Path.Combine(root,"결과.json"),JsonSerializer.Serialize(new{status,saved=ok,missing,failed,endedAt=DateTimeOffset.Now}),CancellationToken.None);Notice.Text=$"이미지 추출 {status} · 저장 {ok} / 없음 {missing} / 실패 {failed}";Log($"이미지 추출 {status} · 저장{ok}/없음{missing}/실패{failed} · {root}");}
    }
    private async void Thumbnails_Click(object sender,RoutedEventArgs e)
    {
        if(_level!=BrowseLevel.Defects)return;var selected=SelectedRows();if(selected.Count==0)selected=_rows.Take(24).ToList();
        await RunAsync(async ct=>
        {
            ThumbnailPanel.Children.Clear();
            foreach(var row in selected.Take(24))
            {
                try
                {
                    var image=await _repository.ImageAsync(_profile,row.Key(_profile.Identity,BrowseLevel.Defects),ct);if(image is null)continue;
                    var stack=new StackPanel{Width=104,Margin=new Thickness(4)};stack.Children.Add(new Image{Source=CachedBitmap(image),Width=96,Height=96,Stretch=Stretch.Uniform});stack.Children.Add(new TextBlock{Text=$"P{row.Text("panel_no")} / {row.Text("defects")}",TextTrimming=TextTrimming.CharacterEllipsis});ThumbnailPanel.Children.Add(stack);
                }
                catch(Exception ex) when(ex is not OperationCanceledException){Log($"썸네일 {row.Text("defects")}: {ExplorerRepository.SafeError(ex)}");}
            }
        });
    }
    private async void ExportErt_Click(object sender,RoutedEventArgs e)
    {
        if(_level!=BrowseLevel.Inspections){Notice.Text="검사 이력 목록에서 재구성할 검사 기록을 선택하세요. 검사 ID별 기록을 보존합니다.";return;}
        var selected=SelectedRows();if(selected.Count==0)return;string? folder=PickFolder("선택 검사 ERT 저장 폴더");if(folder is null)return;
        string root=Path.Combine(folder,"ERT_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]);
        await RunAsync(async ct=>
        {
            Directory.CreateDirectory(root);await using var log=new StreamWriter(Path.Combine(root,"처리내역.jsonl"),false,new UTF8Encoding(true)){AutoFlush=true};int ok=0,failed=0;
            foreach(var inspection in selected)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var detail=await _repository.DetailAsync(_profile,inspection.Key(_profile.Identity,BrowseLevel.Inspections),ct);
                    var geometry=await _repository.GeometryAsync(_profile,inspection.Text("model_name"),ct);
                    string output=Path.Combine(root,ExportService.SafeComponent(inspection.Text("model_name")),ExportService.SafeComponent(inspection.Text("lot_id_name")),ExportService.SafeComponent(inspection.Text("layer_name")),ExportService.SafeComponent(inspection.Database),ExportService.SafeComponent($"{inspection.Text("machine_id")}_PC{inspection.Text("pc_no")}_I{inspection.Text("inspection")}"));
                    DateTime time=inspection["inspection_time"] is DateTime t?t:throw new InvalidDataException("검사 시각 미기록");
                    string path=Path.Combine(output,$"{inspection.Long("panel_no"):D3}.ert");Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    ErtDocument? ert=null;
                    await _repository.StreamInspectionAsync(_profile,inspection.Database,inspection.Text("inspection"),async(count,rows,token)=>
                    {ert=await ErtExporter.WriteStreamingAsync(path,inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),(int)(inspection.Long("panel_no")??0),time,inspection.Text("inspection_status"),(int?)detail.Long("fov_count"),geometry,count,rows,Encoding.GetEncoding(949),token);},ct);
                    if(ert is null)throw new InvalidDataException("ERT 추출 결과가 없습니다.");
                    await ExportService.WriteNewAsync(path+".source.json",JsonSerializer.Serialize(new{source=inspection.Database,inspection=inspection.Text("inspection"),pc=inspection.Text("pc_no"),scope="선택한 inspection_id에 속한 전체 불량. pc_no=0 공용 검사도 원본대로 유지. 다른 검사 ID와 자동 병합하지 않음.",ert.Rows,notes=ert.Notes},new JsonSerializerOptions{WriteIndented=true}),ct);
                    await log.WriteLineAsync(JsonSerializer.Serialize(new{inspection.Database,id=inspection.Text("inspection"),status="saved",path}));ok++;
                }
                catch(Exception ex) when(ex is not OperationCanceledException){failed++;await log.WriteLineAsync(JsonSerializer.Serialize(new{inspection.Database,id=inspection.Text("inspection"),status="failed",error=ExplorerRepository.SafeError(ex)}));}
                Status.Text=$"ERT 추출 · 성공 {ok} / 실패 {failed}";
            }
            Log($"ERT 추출 · 성공 {ok} / 실패 {failed} · {root}");Notice.Text=$"ERT 추출 성공 {ok} / 실패 {failed}. 선택한 검사 ID의 재구성본입니다. 출처 JSON·처리내역 확인.";
        },true);
    }
    private async void PreviewErt_Click(object sender,RoutedEventArgs e)
    {
        if(_level!=BrowseLevel.Inspections||Selected() is not ResultRow inspection){Notice.Text="검사 이력 목록에서 미리 볼 검사를 선택하세요.";return;}
        await RunAsync(async ct=>
        {
            var detail=await _repository.DetailAsync(_profile,inspection.Key(_profile.Identity,BrowseLevel.Inspections),ct);
            var geometry=await _repository.GeometryAsync(_profile,inspection.Text("model_name"),ct);
            var rows=new List<ResultRow>();long total=0;
            await _repository.StreamInspectionAsync(_profile,inspection.Database,inspection.Text("inspection"),async(count,source,token)=>
            {total=count;await foreach(var row in source.WithCancellation(token)){rows.Add(row);if(rows.Count==2000)break;}},ct);
            var ert=ErtExporter.Render("(저장 폴더 미지정)",inspection.Text("model_name"),inspection.Text("lot_id_name"),inspection.Text("layer_name"),(int)(inspection.Long("panel_no")??0),inspection["inspection_time"] is DateTime time?time:throw new InvalidDataException("검사 시각 미기록"),inspection.Text("inspection_status"),(int?)detail.Long("fov_count"),geometry,rows);
            var panel=new DockPanel{Margin=new Thickness(18)};
            var label=new TextBlock{Text=$"{inspection.Database} / 검사 {inspection.Text("inspection")} / PC 값 {inspection.Text("pc_no")}\n전체 {total:N0}행 중 {rows.Count:N0}행 미리보기 (최대 2,000행). ERT 저장은 전체 행을 씁니다.\npc_no=0은 원본의 공용 검사 기록입니다. 다른 검사 ID를 자동 병합하지 않습니다.\n"+string.Join("\n",ert.Notes),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(label,Dock.Top);panel.Children.Add(label);
            panel.Children.Add(new TextBox{Text=ert.Text,IsReadOnly=true,AcceptsReturn=true,AcceptsTab=true,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas"),TextWrapping=TextWrapping.NoWrap});
            new Window{Owner=this,Title="ERT 미리보기 · DB 재구성",Width=1000,Height=700,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner}.Show();
        });
    }
    private void Window_Closing(object? sender,System.ComponentModel.CancelEventArgs e){_queryCts?.Cancel();_detailCts?.Cancel();SaveView();if(_repository is IDisposable disposable)disposable.Dispose();}
}

internal sealed class DisplayConverter : System.Windows.Data.IValueConverter
{
    public static readonly DisplayConverter Instance=new();
    public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>value is DBNull or null?"—":value is bool b?(b?"있음":"없음"):ResultRow.ValueText(value);
    public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
}
