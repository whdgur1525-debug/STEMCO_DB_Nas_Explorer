using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aoi.NasExplorer.Core;

public sealed record ConnectionProfile
{
    public string Name { get; init; } = "NAS";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 5432;
    public string Username { get; init; } = "postgres";
    public string Password { get; init; } = "";
    public string ManagementDatabase { get; init; } = "postgres";
    public string ModelDatabase { get; init; } = "aoi_model";
    public string SslMode { get; init; } = "Prefer";
    public string RootCertificate { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 30;
    public string Identity => $"{Host}:{Port}";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains(',')) throw new ArgumentException("NAS 주소 한 개를 입력하세요.");
        if (Port is < 1 or > 65535) throw new ArgumentException("포트는 1~65535 범위여야 합니다.");
        if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("DB 사용자명을 입력하세요.");
        if (TimeoutSeconds is < 1 or > 600) throw new ArgumentException("조회 제한시간은 1~600초입니다.");
    }
}

public enum BrowseLevel { Models, Lots, Layers, Inspections, Defects }
public enum ImageFilter { All, Present, Missing }
public sealed record SearchFilter
{
    public string Text { get; init; } = "";
    public string Model { get; init; } = "";
    public string Lot { get; init; } = "";
    public string Layer { get; init; } = "";
    public string LotId { get; init; } = "";
    public string LayerId { get; init; } = "";
    public string InspectionId { get; init; } = "";
    public string Machine { get; init; } = "";
    public int? Panel { get; init; }
    public int? Pc { get; init; }
    public int? DefectCode { get; init; }
    public int? VmsCode { get; init; }
    public int? VmsState { get; init; }
    public int? AiCode { get; init; }
    public int? CauseCode { get; init; }
    public string CauseText { get; init; } = "";
    public double? MinimumSize { get; init; }
    public double? MaximumSize { get; init; }
    public DateTime? From { get; init; }
    public DateTime? UntilExclusive { get; init; }
    public ImageFilter Images { get; init; }
    public bool LatestOnly { get; init; }
    public void Validate()
    {
        if (From.HasValue && UntilExclusive.HasValue && From >= UntilExclusive) throw new ArgumentException("검색 시작일이 종료일보다 늦습니다.");
        if (MinimumSize.HasValue && MaximumSize.HasValue && MinimumSize > MaximumSize) throw new ArgumentException("최소 크기가 최대 크기보다 큽니다.");
        if ((MinimumSize.HasValue && !double.IsFinite(MinimumSize.Value)) || (MaximumSize.HasValue && !double.IsFinite(MaximumSize.Value))) throw new ArgumentException("크기는 유한한 숫자여야 합니다.");
    }
}

public sealed record DatabaseInfo(string Name, string State, bool IsInspection, string? Error = null);
public sealed record RowKey(string Server, string Database, string Table, string Id);
public sealed class ResultRow
{
    private static readonly JsonSerializerOptions ArrayJsonOptions=new(){NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
    public string Database { get; init; } = "";
    public Dictionary<string, object?> Values { get; init; } = new(StringComparer.Ordinal);
    public object? this[string name] => Values.GetValueOrDefault(name);
    public string Text(string name) => ValueText(Values.GetValueOrDefault(name));
    public long? Long(string name) => long.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    public double? Number(string name) => double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    public static string ValueText(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        byte[] b => $"[{b.Length:N0} bytes]",
        Array a => JsonSerializer.Serialize(a,ArrayJsonOptions),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };
    public RowKey Key(string server, BrowseLevel level) => new(server, Database,
        level switch { BrowseLevel.Models or BrowseLevel.Lots => "lot_id", BrowseLevel.Layers => "layer", BrowseLevel.Inspections => "inspection", _ => "defects" },
        Text(level switch { BrowseLevel.Models or BrowseLevel.Lots => "lot_id", BrowseLevel.Layers => "layer", BrowseLevel.Inspections => "inspection", _ => "defects" }));
}

public sealed record QueryPage(IReadOnlyList<ResultRow> Rows, bool HasMore, string? Cursor);
public sealed record ImagePayload(byte[] Bytes, int Width, int Height, int Channels, string Database, string DefectId);
public sealed record ModelGeometry(double Resolution, double Left, double Top, double Right, double Bottom,
    double[] BlockX, double[] BlockY, double BlockWidth, double BlockHeight, double[] FrameX,
    double[] UnitY, double UnitWidth, double UnitHeight, double InspectWidth, double InspectHeight)
{
    public bool PunchApply {get;init;}
    public int[] PunchUnits {get;init;}=[];
    public int TotalUnits
    {
        get
        {
            if(UnitWidth<=0||UnitHeight<=0)return 0;
            int count=0;var punches=PunchUnits.ToHashSet();
            for(int x=0;x<FrameX.Length;x++)for(int y=0;y<UnitY.Length;y++)
            {
                if(PunchApply&&punches.Contains(x*UnitY.Length+y+1))continue;
                double px=FrameX[x]-UnitWidth/2,py=UnitY[y]-UnitHeight/2;
                foreach(double bx in BlockX)foreach(double by in BlockY)
                    if(px>=bx&&px<=bx+InspectWidth+(BlockWidth-InspectWidth)/2&&py>=by&&py<=by+InspectHeight+(BlockHeight-InspectHeight)/2)count=checked(count+1);
            }
            return count;
        }
    }
}

public interface IExplorerRepository
{
    Task<IReadOnlyList<DatabaseInfo>> DiscoverAsync(ConnectionProfile profile, CancellationToken ct);
    Task<QueryPage> QueryAsync(ConnectionProfile profile, string database, BrowseLevel level, SearchFilter filter,
        int pageSize, string? cursor, CancellationToken ct);
    Task<ResultRow> DetailAsync(ConnectionProfile profile, RowKey key, CancellationToken ct);
    Task<ImagePayload?> ImageAsync(ConnectionProfile profile, RowKey key, CancellationToken ct);
    Task<ModelGeometry> GeometryAsync(ConnectionProfile profile, string model, CancellationToken ct);
    Task StreamInspectionAsync(ConnectionProfile profile,string database,string inspectionId,
        Func<long,IAsyncEnumerable<ResultRow>,CancellationToken,Task> consume,CancellationToken ct);
}

public static class ImageBytes
{
    public static byte[] Normalize(byte[] bytes, int width, int height, int channels)
    {
        if (width > 0 && height > 0 && channels > 0 && (long)width * height * channels == bytes.Length) return bytes;
        if (bytes.Length > 2 && bytes[0] == '\\' && bytes[1] == 'x')
        {
            try { return Convert.FromHexString(System.Text.Encoding.ASCII.GetString(bytes, 2, bytes.Length - 2)); }
            catch (FormatException) { throw new InvalidDataException("이미지 BYTEA hex 형식이 올바르지 않습니다."); }
        }
        return bytes;
    }
    public static string Extension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return ".png";
        if (b.Length >= 3 && b[0] == 255 && b[1] == 216 && b[2] == 255) return ".jpg";
        if (b.Length >= 4 && ((b[0] == 73 && b[1] == 73 && b[2] is 42 or 43 && b[3] == 0) || (b[0] == 77 && b[1] == 77 && b[2] == 0 && b[3] is 42 or 43))) return ".tif";
        if (b.Length >= 2 && b[0] == 66 && b[1] == 77) return ".bmp";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return ".gif";
        return ".raw";
    }
}
