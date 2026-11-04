using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aoi.NasExplorer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool checks=e.Args.Length>=2&&e.Args[0]=="--ui-checks";
        bool recovered=e.Args.Length>=2&&e.Args[0]=="--recovered-ui";
        bool stress=e.Args.Length>=2&&e.Args[0]=="--stress-ui";
        bool smoke=e.Args.Length>=2&&(e.Args[0]=="--smoke"||checks||recovered||stress);
        var window=new MainWindow(smoke);MainWindow=window;
        if(smoke)
        {
            window.Left=-20000;window.Top=-20000;window.WindowStartupLocation=WindowStartupLocation.Manual;window.ShowActivated=false;window.ShowInTaskbar=false;
            window.Show();
            if(stress){int failed=await window.RunStressUiChecksAsync(e.Args[1]);window.Close();Shutdown(failed==0?0:1);return;}
            if(recovered){int failed=await window.RunRecoveredUiChecksAsync(e.Args[1]);window.Close();Shutdown(failed==0?0:1);return;}
            if(checks){int failed=await window.RunUiChecksAsync(e.Args[1]);window.Close();Shutdown(failed==0?0:1);return;}
            await window.ShowDemoAsync(true);await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();var visual=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);using(var file=File.Create(e.Args[1]))encoder.Save(file);
            window.Close();Shutdown();return;
        }
        window.Show();if(e.Args.Contains("--demo"))await window.ShowDemoAsync();
    }
}
