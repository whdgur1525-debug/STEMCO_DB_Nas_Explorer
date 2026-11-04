using System.Windows;
using System.Windows.Controls;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

internal sealed class ConnectionWindow : Window
{
    public ConnectionProfile? Profile {get;private set;}
    public bool Remember {get;private set;}
    public ConnectionWindow(ConnectionProfile previous)
    {
        Title="NAS DB 연결";Width=510;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(24)}; Content=panel;
        panel.Children.Add(new TextBlock{Text="조회할 NAS를 연결하세요",FontSize=21,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
        panel.Children.Add(new TextBlock{Text="입력한 서버의 검사 DB를 읽기 전용으로 탐색합니다.",Margin=new Thickness(0,0,0,12)});
        TextBox Box(string label,string value)
        { panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(3,6,0,0)});var box=new TextBox{Text=value};panel.Children.Add(box);return box; }
        var name=Box("프로필 이름",previous.Name);var host=Box("NAS IP / 호스트",previous.Host);var port=Box("포트",previous.Port.ToString());
        var user=Box("DB 사용자",previous.Username);
        panel.Children.Add(new TextBlock{Text="비밀번호",Margin=new Thickness(3,6,0,0)});var password=new PasswordBox{Password=previous.Password};panel.Children.Add(password);
        var management=Box("처음 연결할 DB (일반적으로 postgres)",previous.ManagementDatabase);
        var model=Box("모델 DB (일반적으로 aoi_model)",previous.ModelDatabase);
        panel.Children.Add(new TextBlock{Text="TLS 방식",Margin=new Thickness(3,6,0,0)});
        var ssl=new ComboBox{ItemsSource=new[]{"Prefer","Require","VerifyCA","VerifyFull","Disable"},SelectedItem=previous.SslMode};panel.Children.Add(ssl);
        var certificate=Box("CA 인증서 경로 (필요할 때만)",previous.RootCertificate);
        var remember=new CheckBox{Content="이 Windows 계정에 비밀번호 암호화 저장",IsChecked=false};panel.Children.Add(remember);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.Firebrick,Margin=new Thickness(3)};panel.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);
        var cancel=new Button{Content="취소",IsCancel=true};buttons.Children.Add(cancel);var connect=new Button{Content="연결",IsDefault=true};buttons.Children.Add(connect);
        connect.Click+=(_,_)=>
        {
            try
            {
                if(!int.TryParse(port.Text,out var number))throw new ArgumentException("포트를 숫자로 입력하세요.");
                Profile=previous with{Name=name.Text.Trim(),Host=host.Text.Trim(),Port=number,Username=user.Text.Trim(),Password=password.Password,ManagementDatabase=management.Text.Trim(),ModelDatabase=model.Text.Trim(),SslMode=ssl.SelectedItem?.ToString()??"Prefer",RootCertificate=certificate.Text.Trim()};
                Profile.Validate();Remember=remember.IsChecked==true;DialogResult=true;
            }
            catch(Exception ex){error.Text=ex.Message;}
        };
    }
}
