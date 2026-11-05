using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private async Task RunUsabilityChecksAsync(Func<string,Func<Task>,Task> check)
    {
        static void Assert(bool value){if(!value)throw new InvalidOperationException("usability assertion failed");}
        await check("search help follows mode and demo OR search matches the database contract",async()=>
        {
            await ShowDemoAsync();SearchTargetBox.SelectedIndex=1;Assert(SearchHint.Text.Contains("모델 이름")&&Convert.ToString(SearchText.ToolTip)!.Contains("p2411"));
            SearchTargetBox.SelectedIndex=2;Assert(SearchHint.Text.Contains("모델명 또는 LOT명")&&SearchHint.Text.Contains("상세 검색"));
            SearchText.Text="p2411-004d101 agsd";await SearchGlobalAsync();Assert(_rows.Count==0);
            SearchText.Text="agsd";await SearchGlobalAsync();Assert(_rows.Count==1);
            SearchTargetBox.SelectedIndex=0;Assert(SearchHint.Text.Contains("LOT 이름"));
        });
        await check("refresh preserves current-list sort and checked and viewed IDs through sorted DataView",async()=>
        {
            await ShowDemoAsync();var column=Results.Columns.Single(x=>x.SortMemberPath=="lot_id");
            var view=System.Windows.Data.CollectionViewSource.GetDefaultView(Results.ItemsSource);
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("lot_id",System.ComponentModel.ListSortDirection.Ascending));column.SortDirection=System.ComponentModel.ListSortDirection.Ascending;
            int index=_rows.FindIndex(x=>x.Text("lot_id")=="2");_table.Rows[index]["__checked"]=true;SelectResultAtSourceIndex(index);
            await RefreshCurrentAsync(false);
            Assert(Convert.ToInt32(((DataRowView)Results.Items[0])["lot_id"])==1&&Selected()!.Text("lot_id")=="2");
            Assert(_checkedRecords.Count==1&&_checkedRecords.Values.Single().Text("lot_id")=="2"&&Results.Columns.Single(x=>x.SortMemberPath=="lot_id").SortDirection==System.ComponentModel.ListSortDirection.Ascending);
        });
        await check("export shows panel identity and whole-panel preview separately from LOT search totals",async()=>
        {
            await ShowDemoAsync();SearchText.Text="agsd";await SearchGlobalAsync();Results.SelectedIndex=0;await OpenSelectedAsync(BrowseLevel.Defects);Results.SelectedIndex=0;
            var start=DateTime.UtcNow;while(_detailLoading){if((DateTime.UtcNow-start).TotalSeconds>5)throw new TimeoutException();await Task.Delay(10);}
            var preview=(await _repository.QueryAsync(_profile,"예제_DB",BrowseLevel.Inspections,new(){InspectionId=CurrentPanelId()!},1,null,default)).Rows.Single();
            var dialog=CreateBundleWindow(false,preview);Assert(dialog.Mode==BundleMode.Panel&&dialog.SelectionDescription.Contains("불량 전체 12건")&&dialog.SelectionDescription.Contains("패널 번호: 6")&&dialog.SelectionDescription.Contains("검사 Index: 6"));
            Assert(SummaryLabel.Text.Contains("72"));var body=(StackPanel)((ScrollViewer)dialog.Content).Content;
            body.Children.OfType<RadioButton>().Single(x=>Convert.ToString(x.Content)!.StartsWith("현재 검색 결과")).IsChecked=true;
            Assert(dialog.Mode==BundleMode.Query&&dialog.SelectionDescription.Contains("아직 읽지 않은")&&!dialog.SelectionDescription.Contains("불량 전체 12건"));dialog.Close();
        });
        await check("known ERT issue is visible and images stay available",async()=>
        {
            var preview=(await _repository.QueryAsync(_profile,"예제_DB",BrowseLevel.Inspections,new(){InspectionId=CurrentPanelId()!},1,null,default)).Rows.Single();
            string key=preview.Text("model_name")+"_Layer"+preview.Text("layer_name");_ertAvailability[key]="합성 시험: 모델 없음";
            var dialog=CreateBundleWindow(false,preview);var body=(StackPanel)((ScrollViewer)dialog.Content).Content;
            Assert(body.Children.OfType<TextBlock>().Any(x=>x.Text.Contains("합성 시험: 모델 없음")&&x.Visibility==Visibility.Visible));
            Assert(dialog.Options.Images&&dialog.Options.Ert);body.Children.OfType<CheckBox>().Single(x=>Convert.ToString(x.Content)!.StartsWith("ERT")).IsChecked=false;
            Assert(dialog.Options.Images&&!dialog.Options.Ert&&!body.Children.OfType<TextBlock>().Any(x=>x.Text.Contains("합성 시험: 모델 없음")&&x.Visibility==Visibility.Visible));dialog.Close();_ertAvailability.Remove(key);
        });
        await check("checkbox file names render literal underscores and matching accessible names",async()=>
        {
            var dialog=new BundleWindow(1,BrowseLevel.Layers,null,"Layer 1개"){Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};dialog.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();
            var body=(StackPanel)((ScrollViewer)dialog.Content).Content;var bar=body.Children.OfType<CheckBox>().Single(x=>Convert.ToString(x.Content)!.StartsWith("Bar_result"));
            Assert(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(bar)!.GetName()==Convert.ToString(bar.Content));
            Assert(VisualDescendants(bar).OfType<TextBlock>().Any(x=>x.Text.StartsWith("Bar_result")&&x.Text.Contains("NO_DB")));dialog.Close();
        });
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in VisualDescendants(child))yield return nested;}
    }
}
