using System.Globalization;
using System.Text.Json;

namespace Txfio.Tests.Support;

internal static class SnapshotJson
{
    internal const string Absent = "{\"exists\":false}";

    internal const string Directory = "{\"exists\":true}";

    internal static string File(string path)
    {
        FileInfo info = new FileInfo(path);
        return "{\"exists\":true,\"length\":" + info.Length.ToString(CultureInfo.InvariantCulture) +
            ",\"lastWriteTimeUtc\":" + JsonSerializer.Serialize(info.LastWriteTimeUtc) + "}";
    }
}
