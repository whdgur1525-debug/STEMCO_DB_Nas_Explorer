using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aoi.NasExplorer.Core;

namespace Aoi.NasExplorer.App;

internal sealed record StoredConnection(ConnectionProfile Profile,bool Remember,string? Warning=null);

internal sealed class ProfileStore
{
    private static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synapse","NasExplorer");
    private sealed record Saved(ConnectionProfile Profile,string? Secret);
    public static StoredConnection Load(string? folder=null)
    {
        string path=Path.Combine(folder??Folder,"connection.json");
        if(!File.Exists(path))return new(new(),false);
        var saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))??throw new InvalidDataException("연결 설정이 비어 있습니다.");
        string password="";
        if(saved.Secret is null)return new(saved.Profile with{Password=""},false);
        try
        {
            password=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.Secret),null,DataProtectionScope.CurrentUser));
            return new(saved.Profile with{Password=password},true);
        }
        catch(Exception ex) when(ex is CryptographicException or FormatException)
        {
            return new(saved.Profile with{Password=""},true,"저장한 비밀번호를 이 Windows 계정에서 읽을 수 없습니다. 비밀번호를 다시 입력하세요.");
        }
    }
    public static void Save(ConnectionProfile profile,bool remember,string? folder=null)
    {
        string target=folder??Folder;
        Directory.CreateDirectory(target);
        string? secret=remember?Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(profile.Password),null,DataProtectionScope.CurrentUser)):null;
        string path=Path.Combine(target,"connection.json");
        string temporary=Path.Combine(target,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(new Saved(profile with{Password=""},secret),new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temporary,path,true);
        }
        finally
        {
            if(File.Exists(temporary))File.Delete(temporary);
        }
    }
    public static void AppendLog(string text)
    {
        Directory.CreateDirectory(Folder);
        string path=Path.Combine(Folder,"explorer-"+DateTime.Now.ToString("yyyyMMdd")+".log");
        File.AppendAllText(path,text+Environment.NewLine);
    }
    public static ViewPreferences LoadView()
    {
        string path=Path.Combine(Folder,"view.json");
        return File.Exists(path)?JsonSerializer.Deserialize<ViewPreferences>(File.ReadAllText(path))??new():new();
    }
    public static void SaveView(ViewPreferences view)
    {Directory.CreateDirectory(Folder);File.WriteAllText(Path.Combine(Folder,"view.json"),JsonSerializer.Serialize(view,new JsonSerializerOptions{WriteIndented=true}));}
}

internal sealed record SavedFavorite(string Source,string Label,SearchFilter Filter,BrowseLevel Level,string[] Databases);
internal sealed record ViewPreferences
{
    public double Width {get;set;}=1500;
    public double Height {get;set;}=940;
    public Dictionary<string,double> ColumnWidths {get;set;}=new();
    public List<SavedFavorite> Favorites {get;set;}=[];
    public int RefreshSeconds {get;set;}
}

internal static class BitmapDecoderService
{
    public static BitmapSource Decode(ImagePayload data)
    {
        byte[] b=ImageBytes.Normalize(data.Bytes,data.Width,data.Height,data.Channels);
        long rawLength=(long)data.Width*data.Height*data.Channels;
        bool raw=data.Width>0&&data.Height>0&&rawLength==b.Length;
        if(!raw&&ImageBytes.Extension(b)!=".raw")
        {
            using var memory=new MemoryStream(b,false);
            var decoder=BitmapDecoder.Create(memory,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var frame=decoder.Frames[0];
            if((long)frame.PixelWidth*frame.PixelHeight>64_000_000) throw new InvalidDataException("이미지 크기가 6,400만 픽셀을 초과합니다.");
            frame.Freeze();return frame;
        }
        if(data.Width<=0||data.Height<=0) throw new InvalidDataException("raw 이미지 가로·세로가 미기록입니다. 원본 바이트와 메타데이터를 저장할 수 있습니다.");
        if(rawLength!=b.Length) throw new InvalidDataException("이미지 크기·채널과 실제 바이트 수가 다릅니다.");
        PixelFormat format=data.Channels switch{1=>PixelFormats.Gray8,3=>PixelFormats.Rgb24,_=>throw new InvalidDataException("지원하지 않는 raw 이미지 채널입니다. 원본 바이트를 저장할 수 있습니다.")};
        var source=BitmapSource.Create(data.Width,data.Height,96,96,format,null,b,checked(data.Width*data.Channels));
        source.Freeze();return source;
    }
}

