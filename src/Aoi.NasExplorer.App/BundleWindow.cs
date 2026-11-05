using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aoi.NasExplorer.Core;
using Aoi.NasExplorer.Export;
using Microsoft.Win32;

namespace Aoi.NasExplorer.App;

internal enum BundleMode { Checked, Query, Panel }
internal sealed class BundleWindow : Window
{
    public string Folder=>_folder.Text.Trim();
    public BundleMode Mode=>_panel.IsChecked==true?BundleMode.Panel:_checked.IsChecked==true?BundleMode.Checked:BundleMode.Query;
    public BundleOptions Options=>new(_ert.IsChecked==true,_images.IsChecked==true,_counts.IsChecked==true,_csv.IsChecked==true,_layout.SelectedIndex==1,_handler.IsChecked==true,_history.IsChecked==true,_barResult.IsChecked==true,_defectPoint.IsChecked==true);
    private readonly RadioButton _checked,_query,_panel;
    private readonly CheckBox _ert=new(){Content="ERT · 최신 Data Transfer 양식",IsChecked=true},_images=new(){Content="불량 이미지 · 패널 번호 폴더 / 원본 이름 유지",IsChecked=true},_counts=new(){Content="불량 건수 CSV · 패널별 불량 수 / 바별 검출 수",ToolTip="패널별불량수.csv와 바별검출수.csv를 추가합니다. NO_DB Defect Point 파일과는 별도의 통계 자료입니다."},_csv=new(){Content="불량 상세 CSV · 불량목록.csv"};
    private readonly CheckBox _barResult=new(){Content="Bar_result · NO_DB 폐기 바 목록 (.txt)",ToolTip="Bar_result_<LOT>.txt. DB 폐기 마스크가 기록된 경우 생성하며, 미기록을 정상 0으로 만들지 않습니다."};
    private readonly CheckBox _defectPoint=new(){Content="Defect Point · NO_DB 패널별 불량 집계 (.txt)",ToolTip="Defect Point_<Model>_<LOT>_<Layer>_<레이어>_<공정>_<장비>.txt. 선택 검사 DB 불량 행수를 집계하며 ERT 파일 저장 여부와 무관합니다. DB에 없는 장비 원본 카운터·판정은 빈칸으로 남깁니다. 과거 검사 포함 여부에 따라 Total 범위가 달라집니다. 해상도는 현재 모델 기준이며 Handler T/T 미기록은 별도 스캔시간으로 대체하지 않습니다."};
    private readonly TextBox _folder=new();
    private readonly CheckBox _handler=new(){Content="기타 NO_DB 파일 · 패널 폐기 마스크 / 펀치 증거",ToolTip="미기록 값은 0으로 만들지 않습니다. 과거 RETEST.INI의 검사 당시 표식은 현재 Layer 정보만으로 복원하지 않습니다."};
    private readonly CheckBox _history=new(){Content="과거 검사도 함께 저장 (_검사이력 폴더 추가)",ToolTip="기본은 장비·Model·LOT·Layer·패널별 최신 검사입니다. 특정 검사 Index를 직접 체크한 경우에는 그 검사 기록을 그대로 저장합니다."};
    private readonly ComboBox _layout=new(){ItemsSource=new[]{"장비 / Model / LotID / Layer (STEMCO)","장비 / Model / Layer / LotID (SEMCO)"},SelectedIndex=0};
    public BundleWindow(int checkedCount,BrowseLevel level,string? panel,string querySummary,int? checkedPanels=null,bool preferPanel=false)
    {
        Title="검사 결과 내보내기";Width=680;MaxHeight=SystemParameters.WorkArea.Height-60;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(20)};Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        body.Children.Add(new TextBlock{Text="무엇을 내보낼까요?",FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
        _checked=new RadioButton{Content=level==BrowseLevel.Defects?$"체크한 불량 {checkedCount:N0}개가 속한 패널 {checkedPanels??checkedCount:N0}개 전체":$"체크한 {level switch{BrowseLevel.Models=>"Model",BrowseLevel.Lots=>"LotID",BrowseLevel.Layers=>"Layer",_=>"패널 검사"}} {checkedCount:N0}개",IsEnabled=checkedCount>0,GroupName="scope",Margin=new Thickness(4)};
        _query=new RadioButton{Content="현재 검색 결과 전체 (화면에 아직 읽지 않은 항목 포함)",GroupName="scope",Margin=new Thickness(4)};
        _panel=new RadioButton{Content="현재 열람 중인 불량 패널 전체"+(panel is not null?$" (검사 Index {panel})":""),IsEnabled=panel is not null,GroupName="scope",Margin=new Thickness(4)};
        body.Children.Add(_checked);body.Children.Add(_query);if(level==BrowseLevel.Defects)body.Children.Add(_panel);
        var warning=new TextBlock{Text="전체 검색 범위입니다. LOT·Layer가 많으면 이미지 용량과 처리 시간이 커질 수 있습니다. 다음 화면에서 실제 대상 수와 이름을 확인합니다.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkOrange,Margin=new Thickness(4,5,4,8),Visibility=Visibility.Collapsed};body.Children.Add(warning);
        _query.Checked+=(_,_)=>warning.Visibility=Visibility.Visible;_query.Unchecked+=(_,_)=>warning.Visibility=Visibility.Collapsed;
        if(preferPanel&&panel is not null)_panel.IsChecked=true;else if(checkedCount>0)_checked.IsChecked=true;else if(panel is not null)_panel.IsChecked=true;else _query.IsChecked=true;
        body.Children.Add(new TextBlock{Text="현재 검색 집계: "+querySummary,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,FontSize=11,Margin=new Thickness(4,2,4,8)});
        body.Children.Add(new TextBlock{Text="기본 저장: 패널별 최신 ERT + 이미지",FontWeight=FontWeights.SemiBold,Margin=new Thickness(4,4,4,2)});
        foreach(var item in new[]{_ert,_images,_barResult,_defectPoint})body.Children.Add(item);
        var extra=new Expander{Header="추가 자료 / 과거 검사",Margin=new Thickness(4)};var extraBody=new StackPanel();extra.Content=extraBody;foreach(var item in new[]{_counts,_csv,_handler,_history})extraBody.Children.Add(item);body.Children.Add(extra);
        body.Children.Add(new TextBlock{Text="폴더 예: EQ_09 / p2411-004d101 / LOT / L1-T-SHORT3.1-0200 / 005.ERT, 5 / 이미지.png\n작업 내역과 원본 추적 정보는 이 PC의 작업 기록에 따로 보관합니다.",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.SlateGray,Margin=new Thickness(4,6,4,8)});
        body.Children.Add(new TextBlock{Text="폴더 구성",Margin=new Thickness(4,4,0,2)});body.Children.Add(_layout);
        var pick=new DockPanel();var browse=new Button{Content="저장 위치…"};DockPanel.SetDock(browse,Dock.Right);pick.Children.Add(browse);pick.Children.Add(_folder);body.Children.Add(pick);
        browse.Click+=(_,_)=>{var dialog=new OpenFolderDialog{Title="검사 결과 저장 위치"};if(dialog.ShowDialog(this)==true)_folder.Text=dialog.FolderName;};
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Firebrick};body.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};body.Children.Add(buttons);
        buttons.Children.Add(new Button{Content="취소",IsCancel=true});var save=new Button{Content="대상 확인 →",IsDefault=true,Background=Brushes.RoyalBlue,Foreground=Brushes.White};buttons.Children.Add(save);
        save.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(Folder)){error.Text="저장 위치를 선택하세요.";return;}if(!Options.Ert&&!Options.Images&&!Options.Counts&&!Options.DefectCsv&&!Options.HandlerFiles&&!Options.BarResult&&!Options.DefectPoint){error.Text="저장할 자료를 하나 이상 선택하세요.";return;}DialogResult=true;};
    }
}

internal sealed class BundleConfirmWindow : Window
{
    internal CheckBox Acknowledgement {get;}
    internal Button StartButton {get;}
    internal string Description {get;}
    public BundleConfirmWindow(long panels,BundleMode mode,string description,string folder,BundleOptions options)
    {
        Title="내보낼 대상 확인";Width=720;Height=Math.Min(640,SystemParameters.WorkArea.Height-60);MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;Description=description;
        var body=new DockPanel{Margin=new Thickness(22)};Content=body;
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);body.Children.Add(header);
        header.Children.Add(new TextBlock{Text=$"검사 기록 {panels:N0}개를 내보냅니다",FontSize=22,FontWeight=FontWeights.SemiBold});
        bool broad=mode==BundleMode.Query||panels>=100||mode==BundleMode.Checked&&panels>1;
        header.Children.Add(new TextBlock{Text=broad?"선택 범위가 넓습니다. 아래 Model·LOT·Layer와 검사 수를 확인하세요. 이미지가 많으면 용량과 시간이 크게 늘어납니다.":"아래 선택 범위를 확인한 뒤 시작하세요.",TextWrapping=TextWrapping.Wrap,Foreground=broad?Brushes.DarkOrange:Brushes.SlateGray,Margin=new Thickness(0,10,0,10)});
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);body.Children.Add(footer);
        footer.Children.Add(new TextBlock{Text="저장 자료: "+string.Join(" + ",new[]{options.Ert?"ERT":null,options.Images?"이미지":null,options.Counts?"불량 건수 CSV":null,options.DefectCsv?"불량 상세 CSV":null,options.HandlerFiles?"기타 NO_DB 파일":null,options.BarResult?"Bar_result":null,options.DefectPoint?"Defect Point":null}.Where(x=>x is not null)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,4)});
        if(options.DefectPoint)footer.Children.Add(new TextBlock{Text="Defect Point: NO_DB 16열 양식 · DB에 없는 장비 원본 카운터와 판정은 빈칸입니다.",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.SlateGray});
        footer.Children.Add(new TextBlock{Text="저장 위치: "+folder,TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.SlateGray});
        footer.Children.Add(new TextBlock{Text="대상 검사 목록은 지금 고정되며, 각 패널은 읽는 시점의 자료로 저장합니다. 저장 중 새 검사는 다음 반출에 포함됩니다.",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.SlateGray,Margin=new Thickness(0,7,0,4)});
        Acknowledgement=new CheckBox{Content="선택 범위와 데이터 양을 확인했습니다",Visibility=broad?Visibility.Visible:Visibility.Collapsed,Margin=new Thickness(0,8,0,2)};footer.Children.Add(Acknowledgement);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};footer.Children.Add(buttons);buttons.Children.Add(new Button{Content="취소",IsCancel=true});
        StartButton=new Button{Content="내보내기 시작",IsDefault=!broad,IsEnabled=panels>0&&!broad,Background=Brushes.RoyalBlue,Foreground=Brushes.White};buttons.Children.Add(StartButton);
        Acknowledgement.Checked+=(_,_)=>StartButton.IsEnabled=panels>0;Acknowledgement.Unchecked+=(_,_)=>StartButton.IsEnabled=!broad&&panels>0;StartButton.Click+=(_,_)=>DialogResult=true;
        body.Children.Add(new TextBox{Text=description,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(243,247,253)),Margin=new Thickness(0),Padding=new Thickness(10)});
    }
}
