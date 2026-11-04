using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

internal sealed class CheckedItemsWindow : Window
{
    internal sealed record Entry(string Key,string Database,string Index,string Model,string Lot,string Layer,string Machine,string Panel,string Inspection,string Defect,string Position)
    {
        public string SearchText=>string.Join(" ",Database,Index,Model,Lot,Layer,Machine,Panel,Inspection,Defect);
    }
    internal ObservableCollection<Entry> Entries {get;}=[];
    internal IReadOnlyList<string> RetainedKeys=>Entries.Select(x=>x.Key).ToArray();
    private readonly DataGrid _grid=new(){AutoGenerateColumns=false,SelectionUnit=DataGridSelectionUnit.FullRow};
    private readonly TextBlock _count=new();
    private readonly ICollectionView _view;
    private bool _batchRemoving;
    private Action _updateCount=()=>{};
    internal CheckedItemsWindow(IReadOnlyDictionary<string,ResultRow> records,IReadOnlySet<string> visible,BrowseLevel level)
    {
        Title="체크한 항목 확인";Width=1050;Height=570;MinWidth=780;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        foreach(var pair in records)
        {
            var row=pair.Value;
            string index=row.Text(level switch{BrowseLevel.Models=>"model_name",BrowseLevel.Lots=>"lot_id",BrowseLevel.Layers=>"layer",BrowseLevel.Inspections=>"inspection",_=>"defects"});
            Entries.Add(new(pair.Key,row.Database,index,row.Text("model_name"),row.Text("lot_id_name"),row.Text("layer_name"),row.Text("machine_id"),row.Text("panel_no"),row.Text("inspection"),row.Text("defects"),visible.Contains(pair.Key)?"현재 목록":"이전 표시 구간"));
        }
        _view=CollectionViewSource.GetDefaultView(Entries);int original=Entries.Count;
        var body=new DockPanel{Margin=new Thickness(18)};Content=body;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);body.Children.Add(top);
        top.Children.Add(new TextBlock{Text="체크한 대상을 확인하고 필요 없는 항목을 해제하세요",FontSize=18,FontWeight=FontWeights.SemiBold});
        top.Children.Add(new TextBlock{Text="이전 표시 구간에서 체크한 항목도 포함합니다. 아래 검색은 이 목록만 좁히며 체크를 해제하지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,3)});
        top.Children.Add(new TextBlock{Text="체크할 때의 목록 정보입니다. 내보내기는 시작할 때 DB에서 다시 읽습니다.",Foreground=System.Windows.Media.Brushes.SlateGray,FontSize=11,TextWrapping=TextWrapping.Wrap});
        var search=new TextBox{ToolTip="Model, LotID, Layer, 패널 번호, 원본 Index 또는 DB 이름으로 찾기",Margin=new Thickness(0,10,0,5)};top.Children.Add(search);
        search.TextChanged+=(_,_)=>{string value=search.Text.Trim();_view.Filter=value.Length==0?null:item=>((Entry)item).SearchText.Contains(value,StringComparison.OrdinalIgnoreCase);};
        top.Children.Add(_count);
        var bottom=new StackPanel{Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);body.Children.Add(bottom);
        var remove=new StackPanel{Orientation=Orientation.Horizontal};bottom.Children.Add(remove);
        var selected=new Button{Content="고른 항목 체크 해제",IsEnabled=false};remove.Children.Add(selected);
        selected.Click+=(_,_)=>Remove(_grid.SelectedItems.Cast<Entry>().Select(x=>x.Key).ToArray());
        _grid.SelectionChanged+=(_,_)=>selected.IsEnabled=_grid.SelectedItems.Count>0;
        var all=new Button{Content="모두 체크 해제"};remove.Children.Add(all);all.Click+=(_,_)=>Entries.Clear();
        bottom.Children.Add(new TextBlock{Text="변경한 체크는 '적용'을 누를 때 반영됩니다. 취소하면 원래 체크를 유지합니다.",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,5,0,0)});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};bottom.Children.Add(actions);
        actions.Children.Add(new Button{Content="취소",IsCancel=true});var apply=new Button{Content="체크 변경 적용",IsDefault=true};actions.Children.Add(apply);apply.Click+=(_,_)=>DialogResult=true;
        void Column(string header,string property,double width)=>_grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new Binding(property),Width=width});
        Column("목록 위치",nameof(Entry.Position),110);if(level is BrowseLevel.Lots or BrowseLevel.Layers)Column("Index",nameof(Entry.Index),75);Column("Model",nameof(Entry.Model),135);
        if(level>=BrowseLevel.Lots)Column("LotID",nameof(Entry.Lot),125);
        if(level>=BrowseLevel.Layers)Column("Layer",nameof(Entry.Layer),170);
        if(level>=BrowseLevel.Inspections){Column("장비",nameof(Entry.Machine),95);Column("패널",nameof(Entry.Panel),65);Column("검사 Index",nameof(Entry.Inspection),95);}
        if(level==BrowseLevel.Defects)Column("불량 Index",nameof(Entry.Defect),95);
        Column("출처 DB",nameof(Entry.Database),190);_grid.ItemsSource=_view;body.Children.Add(_grid);
        void Count()=>_count.Text=$"체크 유지 {Entries.Count:N0}개 / 처음 {original:N0}개 · 이전 표시 구간 {Entries.Count(x=>x.Position=="이전 표시 구간"):N0}개";
        _updateCount=Count;Entries.CollectionChanged+=(_,_)=>{if(!_batchRemoving)Count();};Count();
    }
    internal void Remove(IReadOnlyCollection<string> keys)
    {
        var remove=keys.ToHashSet(StringComparer.Ordinal);
        _batchRemoving=true;
        try{for(int i=Entries.Count-1;i>=0;i--)if(remove.Contains(Entries[i].Key))Entries.RemoveAt(i);}
        finally{_batchRemoving=false;_updateCount();}
    }
}

public partial class MainWindow
{
    private void ReviewChecks_Click(object sender,RoutedEventArgs e)
    {
        if(_idleLocked||_queryCts is not null||_checkedRecords.Count==0)return;
        var dialog=new CheckedItemsWindow(_checkedRecords,_rows.Select(CheckKey).ToHashSet(StringComparer.Ordinal),_level){Owner=this};
        if(ShowModal(()=>dialog.ShowDialog())==true)ApplyCheckedReview(dialog.RetainedKeys);
    }
    private void ApplyCheckedReview(IReadOnlyCollection<string> retainedKeys)
    {
        var retained=retainedKeys.ToHashSet(StringComparer.Ordinal);
        foreach(string key in _checkedRecords.Keys.Where(x=>!retained.Contains(x)).ToArray())_checkedRecords.Remove(key);
        _changingChecks=true;
        try{foreach(System.Data.DataRow row in _table.Rows)row["__checked"]=_checkedRecords.ContainsKey(CheckKey(_rows[(int)row["__index"]]));}
        finally{_changingChecks=false;UpdateCheckedLabel();UpdateActions();}
    }
}
