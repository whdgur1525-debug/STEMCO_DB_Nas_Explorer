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
        _profile=new(){Host="127.0.0.1",Port=port,Username="nas_test",Password=File.ReadAllText(Environment.GetEnvironmentVariable("NAS_TEST_PASSWORD_FILE")??throw new InvalidDataException("시험 비밀번호 파일 없음")).TrimEnd(),SslMode="Disable"};
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
        _repository=new Aoi.NasExplorer.Data.ExplorerRepository();_demo=false;_databases=[new(database,"격리 복원",true)];ConfigureNavigation();ConnectionLabel.Text="현장 회수 자료 · 로컬 격리 DB";
        Directory.CreateDirectory(folder);var checks=new List<object>();int failed=0;
        async Task Check(string name,Func<Task> test){try{await test();checks.Add(new{name,status="passed"});}catch(Exception ex){failed++;checks.Add(new{name,status="failed",error=ex.Message});}}
        await Check("actual LOT metadata displayed",async()=>{await BeginQueryAsync(new(),BrowseLevel.Lots,new[]{database},default);if(_rows.Count==0)throw new InvalidDataException("No LOT rows");});
        await Check("actual inspection history displayed",async()=>{await BeginQueryAsync(new(),BrowseLevel.Inspections,new[]{database},default);if(_rows.Count==0)throw new InvalidDataException("No inspections");});
        await Check("actual defect metadata and stored fields displayed",async()=>{await BeginQueryAsync(new(){Images=ImageFilter.Present},BrowseLevel.Defects,new[]{database},default);if(_rows.Count==0)throw new InvalidDataException("No defect images");Results.SelectedIndex=0;var start=DateTime.UtcNow;while(Details.Items.Count==0){if((DateTime.UtcNow-start).TotalSeconds>10)throw new TimeoutException("Detail load timeout");await Task.Delay(10);}});
        await Check("actual image pixels decoded without HALCON",async()=>{var start=DateTime.UtcNow;while(DefectImage.Source is null){if((DateTime.UtcNow-start).TotalSeconds>10)throw new InvalidDataException(ImagePlaceholder.Text);await Task.Delay(10);}});
        await Check("actual image export preserves source bytes",async()=>{var image=_image??throw new InvalidDataException("No image payload");string path=await SaveImageAsync(image,Path.Combine(folder,"image-sample"),default);var expected=ImageBytes.Normalize(image.Bytes,image.Width,image.Height,image.Channels);if(!(await File.ReadAllBytesAsync(path)).SequenceEqual(expected))throw new InvalidDataException("Byte mismatch");});
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
        await Check("demo LOTs and DB scope",async()=>{await ShowDemoAsync();Assert(_rows.Count==12&&_queryDatabases.Single()=="예제_DB");Assert(ConnectionLabel.Text.Contains("예제"));});
        await Check("folder search keeps the displayed model LOT and exact inspection",async()=>
        {
            var context=new SearchFilter{Model="PI-DEMO-01",Lot="LOT-20261104-001",Layer="L1",LotId="1",LayerId="1",InspectionId="1"};
            await BeginQueryAsync(context,BrowseLevel.Defects,new[]{"예제_DB"},default);SearchText.Text="PIN";
            var query=ReadFilter();Assert(query.Model==context.Model&&query.Lot==context.Lot&&query.Layer==context.Layer&&query.InspectionId=="1");
            Assert(ScopeBox.SelectedItem is Scope{Database:"예제_DB"});
        });
        await Check("changing model removes incompatible physical row IDs",async()=>
        {
            await BeginQueryAsync(new(){Model="PI-DEMO-01",Lot="LOT-20261104-001",Layer="L1",LotId="1",LayerId="1",InspectionId="1"},BrowseLevel.Defects,new[]{"예제_DB"},default);
            ModelText.Text="another model";var query=ReadFilter();Assert(query.LotId.Length==0&&query.LayerId.Length==0&&query.InspectionId.Length==0);
        });
        await Check("clear conditions clears hidden folder identifiers",async()=>
        {
            await BeginQueryAsync(new(){Model="PI-DEMO-01",LotId="1",LayerId="1",InspectionId="1"},BrowseLevel.Defects,new[]{"예제_DB"},default);
            Clear_Click(new Button(),new System.Windows.RoutedEventArgs());var query=ReadFilter();Assert(query.Model.Length==0&&query.LotId.Length==0&&query.LayerId.Length==0&&query.InspectionId.Length==0);Assert(_filter.InspectionId=="1");
        });
        await Check("model to LOT to layer to inspection to defects",async()=>
        {
            await BeginQueryAsync(new(),BrowseLevel.Models,new[]{"예제_DB"},default);
            foreach(var level in new[]{BrowseLevel.Models,BrowseLevel.Lots,BrowseLevel.Layers,BrowseLevel.Inspections})
            {Assert(_level==level);var next=Child(_rows[0],level,_filter);await BeginQueryAsync(next.Filter,next.Level,new[]{"예제_DB"},default);}
            Assert(_level==BrowseLevel.Defects&&_rows.Count>0);
        });
        await Check("main columns prioritize model and panel",()=>{Assert(Results.Columns[0].SortMemberPath=="model_name");Assert(Results.Columns[3].SortMemberPath=="panel_no");return Task.CompletedTask;});
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
        await Check("exact inspection filter used for ERT data",()=>
        {var row=new ResultRow{Values=new(){["inspection"]=17}};var child=Child(row,BrowseLevel.Inspections,new(){LatestOnly=true});Assert(child.Filter.InspectionId=="17"&&!child.Filter.LatestOnly);return Task.CompletedTask;});
        await Check("CSV formula and original JSON differ intentionally",()=>{Assert(ExportService.CsvCell("=cmd").Contains("'=cmd"));Assert(ExportService.Json(new(){Values=new(){["text"]="=cmd"}}).Contains("=cmd"));return Task.CompletedTask;});
        await Check("tree lazy loads first model level",async()=>
        {ConfigureNavigation();var item=(TreeViewItem)Navigation.Items[0];item.Items.Clear();var node=(Nav)item.Tag;await PopulateNodeAsync(item,node,null,default);Assert(item.Items.Count==3);});
        await File.WriteAllTextAsync(Path.Combine(folder,"ui-results.json"),JsonSerializer.Serialize(new{failed,checks},new JsonSerializerOptions{WriteIndented=true}));
        return failed;
    }
}
