using System.Windows;
using System.Windows.Input;
using Aoi.NasExplorer.Data;

namespace Aoi.NasExplorer.App;

public partial class MainWindow
{
    private DateTimeOffset _lastUserInteraction=DateTimeOffset.Now;
    private DateTimeOffset? _nextAutoRefreshAt;
    private bool _idleLocked;
    private int _modalDepth;
    private static readonly TimeSpan IdleLimit=TimeSpan.FromMinutes(10);
    private void InitializeIdleSession()
    {
        InputManager.Current.PreProcessInput+=AppInput;
    }
    private void AppInput(object sender,PreProcessInputEventArgs args)
    {
        // WPF input from this process, including the nonmodal ERT viewer. Pointer
        // movement alone does not keep a workstation polling indefinitely.
        if(!_idleLocked&&args.StagingItem.Input is KeyEventArgs{IsDown:true} or MouseButtonEventArgs{ButtonState:MouseButtonState.Pressed} or MouseWheelEventArgs)
            _lastUserInteraction=DateTimeOffset.Now;
    }
    private void ScheduleAutoRefresh()=>_nextAutoRefreshAt=_view.RefreshSeconds<=0?null:DateTimeOffset.Now.AddSeconds(Math.Max(60,_view.RefreshSeconds)+Random.Shared.Next(0,16));
    private bool? ShowModal(Func<bool?> show)
    {
        _modalDepth++;
        try{return show();}
        finally{_modalDepth--;_lastUserInteraction=DateTimeOffset.Now;}
    }
    private async Task<bool> EnterIdleIfDueAsync()
    {
        if(_idleLocked)return true;
        // Includes native file/folder pickers; an idle nonmodal ERT viewer is not activity.
        if(_modalDepth>0)return false;
        if(_exporting||DateTimeOffset.Now-_lastUserInteraction<IdleLimit)return false;
        if(_queryCts is not null){_queryCts.Cancel();return true;}
        _idleLocked=true;StopSummary();_detailCts?.Cancel();_view.RefreshSeconds=0;AutoRefreshBox.SelectedIndex=0;_nextAutoRefreshAt=null;SaveView();
        foreach(FrameworkElement child in ((System.Windows.Controls.Panel)Content).Children)if(!ReferenceEquals(child,IdleOverlay))child.IsEnabled=false;
        IdleOverlay.Visibility=Visibility.Visible;ResumeButton.Focus();
        if(_repository is ExplorerRepository repo)await repo.ReleaseIdleConnectionsAsync();
        Log("10분 미사용 · 자동 갱신 중지 · 조회 연결 정리 · 목록 복귀 대기");return true;
    }
    private Task ResumeSessionAsync()
    {
        if(!_idleLocked)return Task.CompletedTask;
        _lastUserInteraction=DateTimeOffset.Now;
        _idleLocked=false;
        IdleOverlay.Visibility=Visibility.Collapsed;
        foreach(FrameworkElement child in ((System.Windows.Controls.Panel)Content).Children)
            if(!ReferenceEquals(child,IdleOverlay))child.IsEnabled=true;
        Notice.Text=_lastRefresh is DateTimeOffset readAt?$"이전 목록 · {readAt:HH:mm:ss} 조회분 · 최신 결과는 F5":"조회가 다시 활성화되었습니다.";
        UpdateActions();
        return Task.CompletedTask;
    }
    private async void Resume_Click(object sender,RoutedEventArgs e)=>await ResumeSessionAsync();
}
