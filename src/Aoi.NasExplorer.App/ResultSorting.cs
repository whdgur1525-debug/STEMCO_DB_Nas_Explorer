using System.ComponentModel;
using System.Data;
using System.Windows.Controls;
using System.Windows.Data;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private void SelectResultAtSourceIndex(int index)
    {
        Results.SelectedItem=index<0?null:_table.DefaultView.Cast<DataRowView>().FirstOrDefault(row=>(int)row["__index"]==index);
        if(Results.SelectedItem is not null)Results.ScrollIntoView(Results.SelectedItem);
    }
    private sealed record ResultListContext(BrowseLevel Level,SearchFilter Filter,string Databases);
    private ResultListContext? _boundResultsContext;
    private List<SortDescription> _resultSort=[];

    private void RememberResultSort()
    {
        var context=new ResultListContext(_level,_filter,string.Join('\0',_queryDatabases));
        if(_boundResultsContext!=context)_resultSort.Clear();
        else if(_table.Rows.Count>0&&Results.Columns.OfType<DataGridTextColumn>().Any())
        {
            var view=CollectionViewSource.GetDefaultView(Results.ItemsSource);
            _resultSort=view is{CanSort:true}?view.SortDescriptions.ToList():[];
        }
        _boundResultsContext=context;
    }

    private void RestoreResultSort()
    {
        if(_rows.Count==0)return; // The empty binding between a refresh and its first page must retain the saved sort.
        var columns=Results.Columns.Where(x=>x.CanUserSort&&!string.IsNullOrEmpty(x.SortMemberPath)).ToDictionary(x=>x.SortMemberPath);
        _resultSort=_resultSort.Where(x=>columns.ContainsKey(x.PropertyName)).ToList();
        var view=CollectionViewSource.GetDefaultView(Results.ItemsSource);
        if(view is not{CanSort:true})return;
        using(view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach(var sort in _resultSort)
            {
                view.SortDescriptions.Add(sort);columns[sort.PropertyName].SortDirection=sort.Direction;
            }
        }
    }
}
