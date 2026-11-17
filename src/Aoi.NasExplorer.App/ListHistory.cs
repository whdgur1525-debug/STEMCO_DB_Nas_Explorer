using System.ComponentModel;
using System.Windows.Data;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private sealed record ListSnapshot(Location Location,ResultRow[] Rows,KeyValuePair<string,ResultRow>[] Checks,
        string[] FailedDatabases,string[] NonInspectionDatabases,int DatabaseIndex,string? Cursor,bool HasMore,SortDescription[] Sort,
        RowKey? SelectedKey,DateTimeOffset ReadAt,string CountText,SummarySnapshot? Summary);

    private readonly LinkedList<ListSnapshot> _recentLists=new();
    private const int ListHistoryLimit=8;
    private const int CachedSummaryRowLimit=5000;

    private static bool SameLocation(Location left,Location right)=>
        left.Level==right.Level&&left.Filter==right.Filter&&left.AllDatabases==right.AllDatabases
        &&left.Databases.SequenceEqual(right.Databases,StringComparer.Ordinal);

    private void RememberCurrentList()
    {
        if(_lastRefresh is not DateTimeOffset readAt)return;
        var location=new Location(_filter,_level,_queryDatabases.ToArray(),_allDatabases);
        var old=_recentLists.FirstOrDefault(list=>SameLocation(list.Location,location));
        if(old is not null)_recentLists.Remove(old);
        var sort=CollectionViewSource.GetDefaultView(Results.ItemsSource)?.SortDescriptions.ToArray()??[];
        var summary=_displayedSummary;
        if(summary is not null&&summary.Panels.Length+summary.Lots.Length>CachedSummaryRowLimit)summary=null;
        _recentLists.AddFirst(new ListSnapshot(location,_rows.ToArray(),_checkedRecords.ToArray(),_failedDatabases.ToArray(),
            _nonInspectionDatabases.ToArray(),_databaseIndex,_cursor,_hasMore,sort,Selected()?.Key(_profile.Identity,_level),readAt,CountLabel.Text,summary));
        if(_recentLists.Count>ListHistoryLimit)_recentLists.RemoveLast();
    }

    private async Task RestoreOrQueryAsync(NavigationState state,CancellationToken ct)
    {
        var location=state.Current;
        var list=_recentLists.FirstOrDefault(candidate=>SameLocation(candidate.Location,location));
        if(list is null)
        {
            await BeginQueryAsync(location.Filter,location.Level,location.Databases,ct,false,state.Trail,location.AllDatabases);
            RestoreSelection(state.SelectedKey);
            return;
        }
        _recentLists.Remove(list);
        _recentLists.AddFirst(list);
        PrepareList(location.Filter,location.Level,location.Databases,state.Trail,location.AllDatabases);
        _rows.AddRange(list.Rows);
        foreach(var entry in list.Checks)_checkedRecords[entry.Key]=entry.Value;
        _failedDatabases.UnionWith(list.FailedDatabases);
        _nonInspectionDatabases.UnionWith(list.NonInspectionDatabases);
        (_databaseIndex,_cursor,_hasMore)=(list.DatabaseIndex,list.Cursor,list.HasMore);
        _lastRefresh=list.ReadAt;
        if(list.Summary is not null)DisplaySummary(list.Summary);
        else SummaryLabel.Text="이전 목록 · 필요하면 전체 건수 다시 조회";
        BindRows(list.Sort);
        RestoreSelection(state.SelectedKey??list.SelectedKey);
        CountLabel.Text=list.CountText;
        Notice.Text=$"이전 목록 · {list.ReadAt:HH:mm:ss} 조회분 · 최신 결과는 F5";
        if(list.FailedDatabases.Length>0)Notice.Text+=" · 미확인 DB: "+string.Join(", ",list.FailedDatabases);
        UpdateRefreshLabel();
    }

    private void RestoreSelection(RowKey? key)
    {
        if(key is not null)SelectResultAtSourceIndex(_rows.FindIndex(row=>row.Key(_profile.Identity,_level)==key));
    }

    private async Task ReturnToPathAsync(Location location,Location[]? trail)
    {
        if(_idleLocked||_queryCts is not null)return;
        RememberCurrentList();
        _history.Push(CaptureNavigation());
        _forward.Clear();
        await RunAsync(ct=>RestoreOrQueryAsync(new NavigationState(location,trail??[location]),ct));
    }

    private Task ReturnToPathPartAsync(SearchFilter target,BrowseLevel level)
    {
        int ancestor=_trail.FindLastIndex(step=>step.Level==level&&step.Filter.Model==target.Model
            &&step.Filter.Lot==target.Lot&&step.Filter.Layer==target.Layer);
        if(ancestor>=0)return ReturnToPathAsync(_trail[ancestor],_trail.Take(ancestor+1).ToArray());
        return ReturnToPathAsync(new Location(target,level,_queryDatabases.ToArray(),_allDatabases),null);
    }
}
