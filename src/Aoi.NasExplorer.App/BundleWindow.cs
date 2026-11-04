using System.Windows;
using System.Windows.Controls;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Export;
using Microsoft.Win32;

namespace Aoi.NasExplorer.App;

internal enum BundleMode { Checked, Query, Panel }
internal sealed class BundleWindow : Window
{
    public string Folder=>_folder.Text.Trim();
    public BundleMode Mode=>_panel.IsChecked==true?BundleMode.Panel:_checked.IsChecked==true?BundleMode.Checked:BundleMode.Query;
    public BundleOptions Options=>new(_ert.IsChecked==true,_images.IsChecked==true,_counts.IsChecked==true,_csv.IsChecked==true,_layout.SelectedIndex==1,_handler.IsChecked==true);
    private readonly RadioButton _checked,_query,_panel;
    private readonly CheckBox _ert=new(){Content="ERT · 패널별 검사 결과 (모델 DB 자료 필요)"},_images=new(){Content="불량 이미지 · 패널별 폴더 / 원본 이름 유지",IsChecked=true},_counts=new(){Content="Defect count · 패널별 불량 수 + 바별 검출 수 CSV",IsChecked=true},_csv=new(){Content="불량 상세 목록 CSV · 각 패널의 모든 기록"};
    private readonly TextBox _folder=new();
    private readonly CheckBox _handler=new(){Content="Handler 보조 파일 · DB에 기록된 폐기 바 / 펀치 증거",ToolTip="미기록 값은 0으로 만들지 않습니다. 과거 RETEST.INI·Defect Point 원본은 DB 자료만으로 복원되지 않을 수 있습니다."};
    private readonly ComboBox _layout=new(){ItemsSource=new[]{"Handler 기본: 장비 / Model / LotID / Layer","Handler SEMCO: 장비 / Model / Layer / LotID"},SelectedIndex=0};
    public BundleWindow(int checkedCount,BrowseLevel level,string? panel,string querySummary,int? checkedPanels=null,bool preferPanel=false)
    {
        Title="검사 결과 내보내기";Width=650;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(22)};Content=body;
        body.Children.Add(new TextBlock{Text="저장할 범위와 자료를 선택하세요",FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
        _checked=new RadioButton{Content=level==BrowseLevel.Defects?$"체크한 불량 {checkedCount:N0}개가 속한 패널 {checkedPanels??checkedCount:N0}개 전체":$"체크한 {level switch{BrowseLevel.Models=>"Model",BrowseLevel.Lots=>"LotID",BrowseLevel.Layers=>"Layer",_=>"패널 검사"}} {checkedCount:N0}개 전체",IsEnabled=checkedCount>0,GroupName="scope",Margin=new Thickness(4)};
        _query=new RadioButton{Content="현재 검색 결과 전체의 모든 패널",GroupName="scope",Margin=new Thickness(4)};
        _panel=new RadioButton{Content="현재 열람 중인 불량 패널 전체"+(panel is not null?$" (검사 Index {panel})":""),IsEnabled=panel is not null,GroupName="scope",Margin=new Thickness(4)};
        if(preferPanel&&panel is not null)_panel.IsChecked=true;else if(checkedCount>0)_checked.IsChecked=true;else if(panel is not null)_panel.IsChecked=true;else _query.IsChecked=true;
        body.Children.Add(_checked);body.Children.Add(_query);if(level==BrowseLevel.Defects)body.Children.Add(_panel);
        body.Children.Add(new TextBlock{Text=querySummary,TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.SlateGray,Margin=new Thickness(4,5,4,8)});
        body.Children.Add(new TextBlock{Text="모델·LOT·Layer를 체크하면 날짜·장비·패널 조건과 관계없이 그 폴더의 모든 패널을 저장합니다. 검색 전체는 조건에 맞는 폴더를 먼저 찾습니다. 재검은 별도 기록으로 보존합니다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,0,4,10)});
        foreach(var item in new[]{_ert,_images,_counts,_csv,_handler})body.Children.Add(item);
        body.Children.Add(new TextBlock{Text="내보내기 시작 때 대상 검사 목록을 고정하고 각 패널을 읽는 시점의 자료를 저장합니다. 저장 중 새로 들어온 검사는 다음 내보내기에 포함됩니다.",TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.SlateGray,FontSize=11,Margin=new Thickness(4,6,4,0)});
        body.Children.Add(new TextBlock{Text="폴더 구성",Margin=new Thickness(4,12,0,2)});body.Children.Add(_layout);
        body.Children.Add(new TextBlock{Text="예: Layer/018.ERT, Layer/18/원본이름.png. 같은 패널의 추가 검사는 _검사이력 하위에 보존합니다.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(4,5,4,8)});
        var pick=new DockPanel();var browse=new Button{Content="저장 위치…"};DockPanel.SetDock(browse,Dock.Right);pick.Children.Add(browse);pick.Children.Add(_folder);body.Children.Add(pick);
        browse.Click+=(_,_)=>{var dialog=new OpenFolderDialog{Title="검사 결과 저장 위치"};if(dialog.ShowDialog(this)==true)_folder.Text=dialog.FolderName;};
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.Firebrick};body.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};body.Children.Add(buttons);
        buttons.Children.Add(new Button{Content="취소",IsCancel=true});var save=new Button{Content="↓ 내보내기 시작",IsDefault=true,Background=System.Windows.Media.Brushes.RoyalBlue,Foreground=System.Windows.Media.Brushes.White};buttons.Children.Add(save);
        save.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(Folder)){error.Text="저장 위치를 선택하세요.";return;}if(!Options.Ert&&!Options.Images&&!Options.Counts&&!Options.DefectCsv&&!Options.HandlerFiles){error.Text="저장할 자료를 하나 이상 선택하세요.";return;}DialogResult=true;};
    }
}
