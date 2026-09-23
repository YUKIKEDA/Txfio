using System.Globalization;
using System.Text.Json;

namespace Txfio.Tests.Support;

internal sealed class LeftoverAttachFiles
{
    private LeftoverAttachFiles(string journalPath, string targetPath)
    {
        JournalPath = journalPath;
        TargetPath = targetPath;
    }

    internal string JournalPath { get; }

    internal string TargetPath { get; }

    internal static async Task<LeftoverAttachFiles> WriteAttachAsync(
        string workFolder,
        bool committing,
        string fileName,
        string content)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string targetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, fileName));
        await File.WriteAllTextAsync(targetPath, content);
        FileInfo info = new FileInfo(targetPath);
        string state = "{\"exists\":true,\"length\":" + info.Length.ToString(CultureInfo.InvariantCulture) +
            ",\"lastWriteTimeUtc\":" + JsonSerializer.Serialize(info.LastWriteTimeUtc) + "}";
        string after = committing ? ",\"after\":" + state : string.Empty;

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"Attach\",\"path\":" + JsonSerializer.Serialize(targetPath) +
            ",\"before\":" + state + after + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return new LeftoverAttachFiles(journalPath, targetPath);
    }
}
