using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Export;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    internal async Task<int> RunStressUiChecksAsync(string folder)
    {
        var korea=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
        if(korea.Date==new DateTime(2026,11,4)&&korea.Hour<22)throw new InvalidOperationException("대량 UI 시험은 22시 이후에만 실행합니다.");
        if(!int.TryParse(Environment.GetEnvironmentVariable("NAS_TEST_PORT"),out int port)||port is <55000 or >55999)throw new InvalidDataException("격리 포트 오류");
        _profile=new(){Host="127.0.0.1",Port=port,Username=Environment.GetEnvironmentVariable("NAS_RECOVERED_USER")??"nas_test",Password=File.ReadAllText(Environment.GetEnvironmentVariable("NAS_TEST_PASSWORD_FILE")??throw new InvalidDataException("시험 비밀번호 파일 없음")).TrimEnd(),SslMode="Disable"};
        _repository=new Aoi.NasExplorer.Data.ExplorerRepository();_demo=false;_databases=[new("aoi_20990102","합성 100만 행",true)];ConfigureNavigation();
        var clock=System.Diagnostics.Stopwatch.StartNew();long peak=0;int maxRows=0;Directory.CreateDirectory(folder);
        await BeginQueryAsync(new(),BrowseLevel.Defects,new[]{"aoi_20990102"},default);
        for(int n=0;n<80;n++)
        {
            await LoadPageAsync(default);await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            peak=Math.Max(peak,Environment.WorkingSet);maxRows=Math.Max(maxRows,_rows.Count);
        }
        bool passed=maxRows<=5000&&peak<800L*1024*1024;
        await File.WriteAllTextAsync(Path.Combine(folder,"stress-ui-results.json"),JsonSerializer.Serialize(new{status=passed?"passed":"failed",pageLoads=81,maxVisibleRows=maxRows,peakWorkingSetBytes=peak,seconds=clock.Elapsed.TotalSeconds},new JsonSerializerOptions{WriteIndented=true}));
        return passed?0:1;
    }
    internal async Task<int> RunRecoveredUiChecksAsync(string folder)
    {
        var korea=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
        if(korea.Date==new DateTime(2026,11,4)&&korea.Hour<22)throw new InvalidOperationException("회수 DB 시험은 22시 이후에만 실행합니다.");
        string database=Environment.GetEnvironmentVariable("NAS_RECOVERED_DATABASE")??throw new InvalidDataException("회수 DB 이름 없음");
        if(!database.StartsWith("nas_recovered_",StringComparison.Ordinal))throw new InvalidDataException("격리 복원 DB만 시험할 수 있습니다.");
        if(!int.TryParse(Environment.GetEnvironmentVariable("NAS_TEST_PORT"),out int port)||port is <55000 or >55999)throw new InvalidDataException("격리 시험 포트 오류");
        _profile=new(){Host="127.0.0.1",Port=port,Username="nas_test",Password=File.ReadAllText(Environment.GetEnvironmentVariable("NAS_TEST_PASSWORD_FILE")??throw new InvalidDataException("시험 비밀번호 파일 없음")).TrimEnd(),SslMode="Disable"};
        _profile=_profile with{Username=Environment.GetEnvironmentVariable("NAS_RECOVERED_USER")??"nas_test"};
        _repository=new Aoi.NasExplorer.Data.ExplorerRepository();_demo=false;_databases=[new(database,"격리 복원",true)];ConfigureNavigation();ConnectionLabel.Text="현장 회수 자료 · 로컬 격리 DB";
        Directory.CreateDirectory(folder);var checks=new List<object>();int failed=0;
        async Task Check(string name,Func<Task> test){try{await test();checks.Add(new{name,status="passed"});}catch(Exception ex){failed++;checks.Add(new{name,status="failed",error=ex.Message});}}
        await Check("actual LOT metadata displayed",async()=>{await BeginQueryAsync(new(),BrowseLevel.Lots,new[]{database},default);if(_rows.Count==0)throw new InvalidDataException("No LOT rows");});
        await Check("actual all-scope count distinguishes visible rows and total defects",()=>{if(!SummaryLabel.Text.Contains("257,902")||_summaryRows.Sum(x=>x.Long("defect_count")??0)!=257902)throw new InvalidDataException(SummaryLabel.Text);return Task.CompletedTask;});
        await Check("actual LOT search and direct defects agree with recovered counts",async()=>
        {
            SearchTargetBox.SelectedIndex=0;SearchText.Text="agsd";await SearchGlobalAsync();
            if(_rows.Count!=1||!SummaryLabel.Text.Contains("82,836"))throw new InvalidDataException(SummaryLabel.Text);
            Results.SelectedIndex=0;await OpenSelectedAsync(BrowseLevel.Defects);
            if(_level!=BrowseLevel.Defects||_rows.Count!=250||!SummaryLabel.Text.Contains("82,836"))throw new InvalidDataException(SummaryLabel.Text);
        });
        await Check("actual inspection history displayed",async()=>{await BeginQueryAsync(new(),BrowseLevel.Inspections,new[]{database},default);if(_rows.Count==0)throw new InvalidDataException("No inspections");});
        await Check("ERT without actual model data is disabled with a reason",async()=>
        {
            Results.SelectedIndex=0;var start=DateTime.UtcNow;
            while(!_ertAvailability.ContainsKey(Selected()!.Text("model_name")+"_Layer"+Selected()!.Text("layer_name"))){if((DateTime.UtcNow-start).TotalSeconds>10)throw new TimeoutException("Model availability timeout");await Task.Delay(10);}
            if(ErtButton.IsEnabled||!Convert.ToString(ErtButton.ToolTip)!.Contains("모델 자료"))throw new InvalidDataException("ERT eligibility was not explained");
        });
        await Check("actual defect metadata and stored fields displayed",async()=>{await BeginQueryAsync(new(){Images=ImageFilter.Present},BrowseLevel.Defects,new[]{database},default);if(_rows.Count==0||!SummaryLabel.Text.Contains("257,902"))throw new InvalidDataException("Defect-filtered count failed: "+SummaryLabel.Text);Results.SelectedIndex=0;var start=DateTime.UtcNow;while(Details.Items.Count==0){if((DateTime.UtcNow-start).TotalSeconds>10)throw new TimeoutException("Detail load timeout");await Task.Delay(10);}});
        await Check("actual image pixels decoded without HALCON",async()=>{var start=DateTime.UtcNow;while(DefectImage.Source is null){if((DateTime.UtcNow-start).TotalSeconds>10)throw new InvalidDataException(ImagePlaceholder.Text);await Task.Delay(10);}});
        await Check("actual selected-image workflow preserves bytes and reports snapshot success",async()=>
        {
            var image=_image??throw new InvalidDataException("No image payload");var selected=SelectedRows();string output=Path.Combine(folder,"selected-image-flow");
            await RunAsync(ct=>ExportImagesCoreAsync(output,SelectedImageRows(selected,ct),ct,true),true);
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output,"결과.json")));
            if(json.RootElement.GetProperty("saved").GetInt32()!=1||json.RootElement.GetProperty("snapshots").GetArrayLength()!=1)throw new InvalidDataException("Missing success report");
            string path=Directory.GetFiles(output,"*.raw",SearchOption.AllDirectories).Single();var expected=ImageBytes.Normalize(image.Bytes,image.Width,image.Height,image.Channels);
            if(!(await File.ReadAllBytesAsync(path)).SequenceEqual(expected))throw new InvalidDataException("Byte mismatch");
        });
        await Check("actual Handler-named image and whole-panel count bundle",async()=>
        {
            var selected=Selected()??throw new InvalidDataException("No selected defect");var image=_image??throw new InvalidDataException("No image");
            string saved=await SaveHandlerImageAsync(selected,image,Path.Combine(folder,"handler-named-image"),default);
            if(Path.GetFileName(saved)!=selected.Text("source_file_name")+".png")throw new InvalidDataException("Original Vision image name changed");
            var child=Child(selected,BrowseLevel.Inspections,_filter);
            await BeginQueryAsync(child.Filter,BrowseLevel.Defects,new[]{database},default);Results.SelectedIndex=0;
            var inspection=(await _repository.QueryAsync(_profile,database,BrowseLevel.Inspections,new(){InspectionId=_filter.InspectionId},1,null,default)).Rows.Single();
            string root=Path.Combine(folder,"actual-panel-bundle");await RunAsync(ct=>RunBundleAsync(root,CreateBundleScopes(BundleMode.Panel,CurrentPanelId()),new(false,false,true,true),ct),true);
            using var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"내보내기결과.json")));
            if(result.RootElement.GetProperty("panels").GetInt64()!=1||result.RootElement.GetProperty("defects").GetInt64()!=inspection.Long("stored_defect_count")||result.RootElement.GetProperty("failures").GetInt64()!=0)throw new InvalidDataException("Panel bundle count mismatch");
            if(Results.Columns.Any(x=>x.SortMemberPath is "pc_no" or "model_name" or "lot_id_name" or "layer_name"))throw new InvalidDataException("Fixed scope columns were duplicated");
            var start=DateTime.UtcNow;while(DefectImage.Source is null){if((DateTime.UtcNow-start).TotalSeconds>10)throw new TimeoutException("Panel image");await Task.Delay(10);}
        });
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);UpdateLayout();
        var content=(System.Windows.FrameworkElement)Content;var capture=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);capture.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(capture));using(var stream=File.Create(Path.Combine(folder,"회수DB_화면.png")))png.Save(stream);
        await File.WriteAllTextAsync(Path.Combine(folder,"recovered-ui-results.json"),JsonSerializer.Serialize(new{failed,checks},new JsonSerializerOptions{WriteIndented=true}));
        return failed;
    }
    internal async Task<int> RunUiChecksAsync(string folder)
    {
        Directory.CreateDirectory(folder);var checks=new List<object>();int failed=0;
        async Task Check(string name,Func<Task> action)
        {try{await action();checks.Add(new{name,status="passed"});}catch(Exception ex){failed++;checks.Add(new{name,status="failed",error=ex.Message});}}
        static void Assert(bool value){if(!value)throw new InvalidOperationException("assertion failed");}
        async Task Until(Func<bool> condition)
        {var start=DateTime.UtcNow;while(!condition()){if((DateTime.UtcNow-start).TotalSeconds>5)throw new TimeoutException("UI state did not settle");await Task.Delay(10);}}
        await Check("demo LOTs and DB scope",async()=>{await ShowDemoAsync();Assert(_rows.Count==15&&_queryDatabases.Single()=="예제_DB");Assert(ConnectionLabel.Text.Contains("예제"));});
        await Check("local start never connects a remote saved host",async()=>
        {
            var prior=_profile;_profile=prior with{Host="remote.invalid"};
            try{await ConnectLocalProfileAsync();Assert(Notice.Text.Contains("이 PC의 DB"));Assert(_demo);}
            finally{_profile=prior;}
        });
        await Check("folder search keeps the displayed model LOT and exact inspection",async()=>
        {
            var context=new SearchFilter{Model="p2411-004d101",Lot="agsd",Layer="L1",LotId="1",LayerId="1",InspectionId="1"};
            await BeginQueryAsync(context,BrowseLevel.Defects,new[]{"예제_DB"},default);SearchText.Text="PIN";
            var query=ReadFilter();Assert(query.Model==context.Model&&query.Lot==context.Lot&&query.Layer==context.Layer&&query.InspectionId=="1");
            Assert(ScopeBox.SelectedItem is Scope{Database:"예제_DB"});
        });
        await Check("changing model removes incompatible physical row IDs",async()=>
        {
            await BeginQueryAsync(new(){Model="p2411-004d101",Lot="agsd",Layer="L1",LotId="1",LayerId="1",InspectionId="1"},BrowseLevel.Defects,new[]{"예제_DB"},default);
            ModelText.Text="another model";var query=ReadFilter();Assert(query.LotId.Length==0&&query.LayerId.Length==0&&query.InspectionId.Length==0);
        });
        await Check("clear conditions clears hidden folder identifiers",async()=>
        {
            await BeginQueryAsync(new(){Model="p2411-004d101",LotId="1",LayerId="1",InspectionId="1"},BrowseLevel.Defects,new[]{"예제_DB"},default);
            Clear_Click(new Button(),new System.Windows.RoutedEventArgs());var query=ReadFilter();Assert(query.Model.Length==0&&query.LotId.Length==0&&query.LayerId.Length==0&&query.InspectionId.Length==0);Assert(_filter.InspectionId=="1");
        });
        await Check("model to LOT to layer to inspection to defects",async()=>
        {
            await BeginQueryAsync(new(),BrowseLevel.Models,new[]{"예제_DB"},default);
            foreach(var level in new[]{BrowseLevel.Models,BrowseLevel.Lots,BrowseLevel.Layers,BrowseLevel.Inspections})
            {Assert(_level==level);var next=Child(_rows[0],level,_filter);await BeginQueryAsync(next.Filter,next.Level,new[]{"예제_DB"},default);}
            Assert(_level==BrowseLevel.Defects&&_rows.Count>0);
        });
        await Check("main columns prioritize model and panel",()=>{Assert(Results.Columns[0] is DataGridTemplateColumn);Assert(!Results.Columns.Any(x=>x.SortMemberPath=="pc_no"));Assert(!Results.Columns.Any(x=>x.SortMemberPath=="model_name"||x.SortMemberPath=="lot_id_name"||x.SortMemberPath=="layer_name"));return Task.CompletedTask;});
        await Check("automatic widths are not persisted before layout completes",async()=>
        {
            _view.ColumnWidths.Clear();await BeginQueryAsync(new(),BrowseLevel.Defects,new[]{"예제_DB"},default);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);UpdateLayout();
            var time=Results.Columns.Single(x=>x.SortMemberPath=="inspection_time");
            var sample=new TextBlock{Text=ResultRow.ValueText(_rows[0]["inspection_time"]),FontFamily=Results.FontFamily,FontSize=Results.FontSize};sample.Measure(new System.Windows.Size(double.PositiveInfinity,double.PositiveInfinity));
            double required=sample.DesiredSize.Width+4;
            if(!time.Width.IsAuto||time.ActualWidth+1<required)throw new InvalidOperationException($"Expected Auto fitting {required}; actual={time.Width.UnitType}/{time.ActualWidth}");
            RememberColumns();if(_view.ColumnWidths.ContainsKey("inspection_time"))throw new InvalidOperationException("Auto width was persisted");
            time.Width=new DataGridLength(180);UpdateLayout();RememberColumns();if(_view.ColumnWidths["inspection_time"]!=180)throw new InvalidOperationException($"Manual width was not retained: {time.ActualWidth}");
            time.Width=DataGridLength.Auto;RememberColumns();if(_view.ColumnWidths.ContainsKey("inspection_time"))throw new InvalidOperationException("Returning to Auto did not clear the persisted width");
        });
        await Check("selection shows raw fields and decoded image",async()=>{Results.SelectedIndex=0;await Until(()=>_image is not null&&DefectImage.Source is not null);Assert(Details.Items.Count>20);Assert(DefectImage.Source is BitmapSource{PixelWidth:128,PixelHeight:128});});
        await Check("rapid selection retains newest row",async()=>{Results.SelectedIndex=3;Results.SelectedIndex=8;await Until(()=>_image?.DefectId==_rows[8].Text("defects"));Assert(DetailSource.Text.Contains(_rows[8].Text("defects")));});
        await Check("zoom is bounded and original scale works",()=>{SetZoom(100);Assert(ImageScale.ScaleX==24);SetZoom(0.0001);Assert(ImageScale.ScaleX==0.05);SetZoom(1);Assert(ImageScale.ScaleX==1);return Task.CompletedTask;});
        await Check("unknown raw dimensions never guessed",()=>{try{BitmapDecoderService.Decode(new(new byte[16],0,0,1,"db","1"));throw new Exception("Expected invalid dimensions");}catch(InvalidDataException){}return Task.CompletedTask;});
        await Check("RGB raw channel order matches source contract",()=>{var image=BitmapDecoderService.Decode(new(new byte[]{255,0,0},1,1,3,"db","1"));Assert(image.Format==PixelFormats.Rgb24);byte[] bytes=new byte[3];image.CopyPixels(bytes,3,0);Assert(bytes[0]==255&&bytes[1]==0&&bytes[2]==0);return Task.CompletedTask;});
        await Check("raw export retains bytes metadata and PNG viewing copy",async()=>
        {var payload=await _repository.ImageAsync(_profile,new("demo","예제_DB","defects","1"),default);string path=await SaveImageAsync(payload!,Path.Combine(folder,"raw-export"),default);Assert(File.Exists(path)&&File.Exists(path+".json")&&File.Exists(path+".png"));Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(payload!.Bytes));});
        await Check("encoded PNG round trip does not require HALCON",()=>
        {var source=BitmapSource.Create(2,2,96,96,PixelFormats.Gray8,null,new byte[]{0,64,128,255},2);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var stream=new MemoryStream();encoder.Save(stream);var decoded=BitmapDecoderService.Decode(new(stream.ToArray(),0,0,1,"db","1"));Assert(decoded.PixelWidth==2&&decoded.PixelHeight==2);return Task.CompletedTask;});
        await Check("decoded cache obeys byte budget",async()=>
        {ClearImages();for(int i=0;i<40;i++)CachedBitmap(new(new byte[1024*1024],1024,1024,1,"db",i.ToString()));Assert(_cacheBytes<=32*1024*1024);Assert(_imageCache.Count<=32);await Task.CompletedTask;});
        await Check("same-ID same-size image updates replace cached pixels",()=>
        {
            ClearImages();CachedBitmap(new(new byte[]{1,2,3,4},2,2,1,"db","same"));
            var changed=CachedBitmap(new(new byte[]{5,6,7,8},2,2,1,"db","same"));byte[] pixels=new byte[4];changed.CopyPixels(pixels,2,0);Assert(pixels[0]==5);return Task.CompletedTask;
        });
        await Check("exact inspection filter used for ERT data",()=>
        {var row=new ResultRow{Values=new(){["inspection"]=17}};var child=Child(row,BrowseLevel.Inspections,new(){LatestOnly=true});Assert(child.Filter.InspectionId=="17"&&!child.Filter.LatestOnly);return Task.CompletedTask;});
        await Check("CSV formula and original JSON differ intentionally",()=>{Assert(ExportService.CsvCell("=cmd").Contains("'=cmd"));Assert(ExportService.Json(new(){Values=new(){["text"]="=cmd"}}).Contains("=cmd"));return Task.CompletedTask;});
        await Check("tree lazy loads first model level",async()=>
        {ConfigureNavigation();var item=(TreeViewItem)Navigation.Items[0];item.Items.Clear();var node=(Nav)item.Tag;await PopulateNodeAsync(item,node,null,default);Assert(item.Items.Count==2);});
        await Check("LOT search starts outside previous exact inspection context",async()=>
        {
            await BeginQueryAsync(new(){Model="p2411-004d101",Lot="agsd",Layer="L1",LotId="1",LayerId="1",InspectionId="1"},BrowseLevel.Defects,new[]{"예제_DB"},default);
            SearchText.Text="DB_TEST_260511";SearchTargetBox.SelectedIndex=0;
            await SearchGlobalAsync();Assert(_level==BrowseLevel.Lots&&_rows.Count==1);Assert(_filter.Target==SearchTarget.Lot&&_filter.InspectionId==""&&_filter.Model==""&&_filter.LotId=="");
        });
        await Check("back and forward restore filters and path without adding refresh history",async()=>
        {
            await ShowDemoAsync();await BeginQueryAsync(new(){Text="agsd",Target=SearchTarget.Lot},BrowseLevel.Lots,new[]{"예제_DB"},default);
            var child=Child(_rows[0],_level,_filter);await BeginQueryAsync(child.Filter,child.Level,new[]{"예제_DB"},default);
            Assert(_trail.Count==2);await NavigateHistoryAsync(false);Assert(_level==BrowseLevel.Lots&&_filter.Text=="agsd"&&ForwardButton.IsEnabled);
            await NavigateHistoryAsync(true);Assert(_level==BrowseLevel.Layers&&_filter.LotId=="1"&&_trail.Count==2);
            int history=_history.Count;await RefreshCurrentAsync();Assert(_history.Count==history&&_level==BrowseLevel.Layers);
        });
        await Check("direct defect shortcut bypasses intermediate folders and retains selected LOT",async()=>
        {
            await ShowDemoAsync();Results.SelectedIndex=_rows.FindIndex(x=>x.Text("lot_id_name")=="agsd");await OpenSelectedAsync(BrowseLevel.Defects);
            Assert(_level==BrowseLevel.Defects&&_filter.LotId=="1"&&_filter.InspectionId=="");
            Assert(SummaryLabel.Text.Contains("72")&&_summaryRows.Count==6);
        });
        await Check("export eligibility explains folder and selection requirements",async()=>
        {
            await ShowDemoAsync();UpdateActions();Assert(CsvButton.IsEnabled&&!SelectedImagesButton.IsEnabled&&!ErtButton.IsEnabled&&!JsonButton.IsEnabled);
            await BeginQueryAsync(new(),BrowseLevel.Defects,new[]{"예제_DB"},default);Results.SelectedIndex=0;await Until(()=>_image is not null);UpdateActions();
            Assert(SelectedImagesButton.IsEnabled&&JsonButton.IsEnabled&&AllImagesButton.IsEnabled&&!ErtButton.IsEnabled);
            await BeginQueryAsync(new(),BrowseLevel.Inspections,new[]{"예제_DB"},default);Results.SelectedIndex=0;await Until(()=>ErtButton.IsEnabled);Assert(ErtPreviewButton.IsEnabled&&!AllImagesButton.IsEnabled);
        });
        await Check("export pauses navigation and refresh then restores them",async()=>
        {
            _view.RefreshSeconds=60;var prior=_lastRefresh;bool executed=false;
            await RunAsync(async ct=>
            {
                executed=true;Assert(!BackButton.IsEnabled&&!ForwardButton.IsEnabled&&!Navigation.IsEnabled&&!SearchButton.IsEnabled&&!Results.IsEnabled&&!CsvButton.IsEnabled&&CancelButton.IsEnabled);
                Assert(RefreshLabel.Text.Contains("일시정지"));await AutoRefreshTickAsync();Assert(_lastRefresh==prior);
            },true);
            Assert(executed&&Navigation.IsEnabled&&SearchButton.IsEnabled&&!CancelButton.IsEnabled);_view.RefreshSeconds=0;
        });
        await Check("automatic refresh runs when due and preserves pending search edits",async()=>
        {
            await ShowDemoAsync();_view.RefreshSeconds=60;_nextAutoRefreshAt=DateTimeOffset.Now.AddSeconds(-1);var prior=_lastRefresh;int history=_history.Count;
            await Task.Delay(20);await AutoRefreshTickAsync(true);Assert(_lastRefresh>prior&&_history.Count==history);
            SearchText.Text="pending search";_nextAutoRefreshAt=DateTimeOffset.Now.AddSeconds(-1);prior=_lastRefresh;
            await AutoRefreshTickAsync(true);Assert(_lastRefresh==prior&&SearchText.Text=="pending search");_view.RefreshSeconds=0;
        });
        await Check("output collisions and unwritable parent are diagnosed before export",()=>
        {
            string target=Path.Combine(folder,"occupied.txt");File.WriteAllText(target,"keep");
            try{PrepareOutput(target);throw new Exception("Expected collision");}catch(IOException){}
            Assert(File.ReadAllText(target)=="keep");
            try{PrepareOutput(Path.Combine(target,"blocked.csv"));throw new Exception("Expected invalid parent");}catch(IOException){}
            return Task.CompletedTask;
        });
        await Check("ERT workflow writes one selected inspection and cancellation report",async()=>
        {
            await ShowDemoAsync();await BeginQueryAsync(new(),BrowseLevel.Inspections,new[]{"예제_DB"},default);Results.SelectedIndex=0;
            string root=Path.Combine(folder,"ert-flow");await RunAsync(ct=>ExportErtCoreAsync(root,SelectedRows(),ct),true);
            using var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"결과.json")));Assert(result.RootElement.GetProperty("saved").GetInt32()==1);
            Assert(Directory.GetFiles(root,"*.ert",SearchOption.AllDirectories).Length==1);
            using var cts=new CancellationTokenSource();cts.Cancel();string cancelled=Path.Combine(folder,"ert-cancel");
            try{await ExportErtCoreAsync(cancelled,SelectedRows(),cts.Token);throw new Exception("Expected cancel");}catch(OperationCanceledException){}
            using var cancellation=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cancelled,"결과.json")));Assert(cancellation.RootElement.GetProperty("status").GetString()=="cancelled");
        });
        await Check("checkbox selection, visible select-all and clearing are explicit",async()=>
        {
            await ShowDemoAsync();SetVisibleChecks(true);Assert(_checkedRecords.Count==15&&CheckedLabel.Text.Contains("15"));
            ApplyCheckedReview(Array.Empty<string>());Assert(_checkedRecords.Count==0);
            _table.Rows[0]["__checked"]=true;Assert(_checkedRecords.Count==1&&BundleButton.IsEnabled);
            var scopes=CreateBundleScopes(BundleMode.Checked,null);Assert(scopes.Count==1&&scopes[0].Filter.LotId==_rows[0].Text("lot_id"));
        });
        await Check("full Layer profile and resolution remain distinct in context",async()=>
        {
            foreach(string layer in new[]{"L1-SHORT1.0-0150","L1-T-SHORT2.0-0200","L2-B-SHORT3.5-0250"})
            {var row=new ResultRow{Values=new(){["layer_name"]=layer}};EnrichLayer(row);Assert(row.Text("layer_resolution_code")==layer[^4..]&&row.Text("layer_recipe").Length>0);}
            await BeginQueryAsync(new(){Model="p2411-004d101",Lot="agsd",Layer="L1-T-SHORT3.1-0200",LotId="1",LayerId="1",InspectionId="1",Panel=1},BrowseLevel.Defects,new[]{"예제_DB"},default);
            Assert(PathPanel.Children.OfType<Button>().Any(x=>Convert.ToString(x.Content)!.Contains("Layer (L1-T-SHORT3.1-0200)")));
            Assert(!Results.Columns.Any(x=>x.SortMemberPath is "model_name" or "lot_id_name" or "layer_name" or "pc_no"));
        });
        await Check("central bundle exports one whole panel with ERT original-name PNGs and counts",async()=>
        {
            Results.SelectedIndex=0;var scopes=CreateBundleScopes(BundleMode.Panel,CurrentPanelId());string root=Path.Combine(folder,"central-panel");
            await RunAsync(ct=>RunBundleAsync(root,scopes,new(true,true,true,true),ct),true);
            using var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"내보내기결과.json")));
            Assert(result.RootElement.GetProperty("panels").GetInt64()==1&&result.RootElement.GetProperty("defects").GetInt64()==12&&result.RootElement.GetProperty("images").GetInt64()==12&&result.RootElement.GetProperty("failures").GetInt64()==0);
            string layer=Path.Combine(root,"EQ_09","p2411-004d101","agsd","L1-T-SHORT3.1-0200");Assert(File.Exists(Path.Combine(layer,"001.ERT")));
            Assert(Directory.GetFiles(Path.Combine(layer,"1"),"agsd_1_L1-T-SHORT3.1-0200_*.png").Length==12);
        });
        await Check("central LOT bundle includes every panel and refresh protects checks",async()=>
        {
            await ShowDemoAsync();SearchText.Text="agsd";SearchTargetBox.SelectedIndex=0;await SearchGlobalAsync();SetVisibleChecks(true);
            _view.RefreshSeconds=60;_nextAutoRefreshAt=DateTimeOffset.Now.AddSeconds(-1);var previous=_lastRefresh;await AutoRefreshTickAsync(true);Assert(_lastRefresh==previous&&_checkedRecords.Count==1);_view.RefreshSeconds=0;
            string root=Path.Combine(folder,"central-lot");await RunAsync(ct=>RunBundleAsync(root,CreateBundleScopes(BundleMode.Checked,null),new(true,false,true,false),ct),true);
            using var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"내보내기결과.json")));Assert(result.RootElement.GetProperty("panels").GetInt64()==6&&result.RootElement.GetProperty("defects").GetInt64()==72&&result.RootElement.GetProperty("ertFiles").GetInt64()==6);
        });
        await Check("new controls describe scope and export without favorite clutter",()=>
        {
            Assert(Navigation.Items.Cast<TreeViewItem>().All(x=>!Convert.ToString(x.Header)!.Contains("즐겨찾기")));
            Assert(Convert.ToString(Results.Columns.Single(x=>x.SortMemberPath=="lot_id").Header)=="Index");
            var dialog=new BundleWindow(1,BrowseLevel.Lots,null,"LOT 1개 · 패널 전체");Assert(dialog.Mode==BundleMode.Checked&&dialog.Options.Images&&dialog.Options.Counts);
            var body=(System.Windows.FrameworkElement)dialog.Content;dialog.Content=null;body.Margin=new System.Windows.Thickness(0);
            var frame=new Border{Child=body,Background=System.Windows.Media.Brushes.White,Padding=new System.Windows.Thickness(22)};frame.Measure(new System.Windows.Size(650,double.PositiveInfinity));frame.Arrange(new System.Windows.Rect(0,0,650,frame.DesiredSize.Height));frame.UpdateLayout();
            var bitmap=new RenderTargetBitmap(650,(int)Math.Ceiling(frame.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(frame);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(folder,"내보내기창.png")))encoder.Save(stream);dialog.Close();return Task.CompletedTask;
        });
        await Check("checked image export writes only checked defects and resets stale image details",async()=>
        {
            await ShowDemoAsync(true);await Until(()=>_image is not null);
            var selected=_rows.Take(2).ToArray();string root=Path.Combine(folder,"checked-images-only");
            await RunAsync(ct=>SaveCheckedImagesAsync(root,selected,ct),true);
            Assert(Directory.GetFiles(root,"*.png",SearchOption.AllDirectories).Length==2);
            using var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"결과.json")));
            Assert(result.RootElement.GetProperty("saved").GetInt64()==2&&result.RootElement.GetProperty("selected").GetInt32()==2);
            await ShowDemoAsync();Assert(!ImageInfo.Text.Contains("16384")&&!ImageInfo.Text.Contains("16,384"));
            UpdateResultContextMenu();Assert(!Results.ContextMenu.Items.OfType<MenuItem>().Single(x=>Convert.ToString(x.Tag)=="ert").IsEnabled);
        });
        await Check("coordinate columns keep raw precision and checkbox stays fixed on horizontal scroll",async()=>
        {
            await ShowDemoAsync(true);_rows[0].Values["cad_x"]=151.38186645507812;BindRows();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);UpdateLayout();
            foreach(string name in new[]{"raw_x","raw_y","cad_x","cad_y","ert_cad_x","ert_cad_y","unit_x","unit_y","source_axis_x","source_axis_y"})Assert(Results.Columns.Any(x=>x.SortMemberPath==name));
            Assert(_rows[0].Number("cad_x")==151.38186645507812&&_rows[0].Long("ert_cad_x")==151&&Results.FrozenColumnCount==1&&Results.Columns[0].IsFrozen&&!Results.Columns[0].CanUserReorder);
            _table.Rows[0]["__checked"]=true;Assert(_checkHeader!.IsChecked is null);SetVisibleChecks(true);Assert(_checkHeader.IsChecked==true);SetVisibleChecks(false);Assert(_checkHeader.IsChecked==false);
            static IEnumerable<System.Windows.DependencyObject> Walk(System.Windows.DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var item in Walk(child))yield return item;}}
            var scroll=Walk(Results).OfType<ScrollViewer>().First(x=>x.ScrollableWidth>0);double left=_checkHeader.TransformToAncestor(Results).Transform(new System.Windows.Point()).X;
            scroll.ScrollToRightEnd();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);UpdateLayout();
            Assert(scroll.HorizontalOffset>0&&Math.Abs(_checkHeader.TransformToAncestor(Results).Transform(new System.Windows.Point()).X-left)<1);
        });
        await Check("edited panel and exact hierarchy filters are not pinned to the prior inspection",async()=>
        {
            await ShowDemoAsync();
            await BeginQueryAsync(new(){Model="p2411-004d101",Lot="agsd",Layer="L1-T-SHORT3.1-0200",LotId="1",LayerId="1",InspectionId="1",Panel=1,Machine="EQ_09",MachineExact=true},BrowseLevel.Defects,new[]{"예제_DB"},default);
            PanelText.Text="2";Assert(ReadFilter().InspectionId==""&&CreateSearchFilter().Panel==2);
            ModelText.Text="p2412-022d601";LotText.Text="ATMAET202TEST";LayerText.Text="L2-B-SHORT3.1-0200";
            var search=CreateSearchFilter();Assert(search.Model=="p2412-022d601"&&search.Lot=="ATMAET202TEST"&&search.Layer=="L2-B-SHORT3.1-0200"&&search.InspectionId=="");
        });
        await Check("refresh preserves checks and reviewed previous-page removals update export scope",async()=>
        {
            await ShowDemoAsync();_table.Rows[0]["__checked"]=true;string key=_checkedRecords.Keys.Single();
            await RefreshCurrentAsync();Assert(_checkedRecords.ContainsKey(key)&&(bool)_table.Rows[0]["__checked"]);
            var prior=new ResultRow{Database="예제_DB",Values=new(){["lot_id"]=9001,["model_name"]="p2411-004d101",["lot_id_name"]="이전구간LOT"}};
            string previous=CheckKey(prior);_checkedRecords[previous]=prior;
            var dialog=new CheckedItemsWindow(_checkedRecords,_rows.Select(CheckKey).ToHashSet(StringComparer.Ordinal),_level);
            Assert(dialog.Entries.Count==2&&dialog.Entries.Single(x=>x.Key==previous).Position=="이전 표시 구간");
            dialog.Remove(new[]{previous});Assert(_checkedRecords.Count==2);
            ApplyCheckedReview(dialog.RetainedKeys);Assert(_checkedRecords.Count==1&&(bool)_table.Rows[0]["__checked"]&&CreateBundleScopes(BundleMode.Checked,null).Count==1);dialog.Close();
            await BeginQueryAsync(new(){Text="agsd",Target=SearchTarget.Lot},BrowseLevel.Lots,new[]{"예제_DB"},default);
            Assert(_checkedRecords.Count==0&&Notice.Text.Contains("이전 체크 1개 해제"));
        });
        await Check("minimum window keeps result rows selectable while advanced filters are open",async()=>
        {
            await ShowDemoAsync();double width=Width,height=Height;Width=1100;Height=720;DetailSearchExpander.IsExpanded=true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);UpdateLayout();
            double required=Results.ColumnHeaderHeight+Results.RowHeight+System.Windows.SystemParameters.HorizontalScrollBarHeight;
            if(Results.ActualHeight<required)throw new InvalidOperationException($"Minimum window needs a full row and scrollbar: {Results.ActualHeight:N1}px < {required:N1}px");
            var visual=(System.Windows.FrameworkElement)Content;var bitmap=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(folder,"최소창_필터펼침.png")))png.Save(stream);
            Width=width;Height=height;DetailSearchExpander.IsExpanded=false;
        });
        await Check("modal dialog scope defers idle pause and resets inactivity on return",()=>
        {
            var refreshed=_lastRefresh;_view.RefreshSeconds=60;_nextAutoRefreshAt=DateTimeOffset.Now.AddSeconds(-1);
            ShowModal(()=>{_lastUserInteraction=DateTimeOffset.Now.AddMinutes(-11);Assert(!EnterIdleIfDueAsync().GetAwaiter().GetResult()&&!_idleLocked);Assert(AutoRefreshTickAsync(true).IsCompletedSuccessfully&&_lastRefresh==refreshed);return null;});
            _view.RefreshSeconds=0;ScheduleAutoRefresh();
            Assert(DateTimeOffset.Now-_lastUserInteraction<TimeSpan.FromSeconds(1));return Task.CompletedTask;
        });
        await Check("idle pause disables automatic reads and resumes only through explicit refresh",async()=>
        {
            _lastUserInteraction=DateTimeOffset.Now.AddMinutes(-11);_view.RefreshSeconds=60;await AutoRefreshTickAsync(true);
            Assert(_idleLocked&&IdleOverlay.Visibility==System.Windows.Visibility.Visible&&_view.RefreshSeconds==0&&AutoRefreshBox.SelectedIndex==0);
            bool ran=false;await RunAsync(_=>{ran=true;return Task.CompletedTask;});Assert(!ran);
            await ResumeSessionAsync();Assert(!_idleLocked&&IdleOverlay.Visibility==System.Windows.Visibility.Collapsed&&_view.RefreshSeconds==0);
        });
        await Check("refresh cadence begins after completion with at least one minute and jitter",async()=>
        {
            _view.RefreshSeconds=60;await RefreshCurrentAsync();
            Assert(_nextAutoRefreshAt>=_lastRefresh!.Value.AddSeconds(60)&&_nextAutoRefreshAt<=DateTimeOffset.Now.AddSeconds(76));
            _view.RefreshSeconds=0;ScheduleAutoRefresh();
        });
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);UpdateLayout();
        var visual=(System.Windows.FrameworkElement)Content;var screen=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);screen.Render(visual);var screenshot=new PngBitmapEncoder();screenshot.Frames.Add(BitmapFrame.Create(screen));
        using(var file=File.Create(Path.Combine(folder,"예제_새화면.png")))screenshot.Save(file);
        await File.WriteAllTextAsync(Path.Combine(folder,"ui-results.json"),JsonSerializer.Serialize(new{failed,checks},new JsonSerializerOptions{WriteIndented=true}));
        return failed;
    }
}
