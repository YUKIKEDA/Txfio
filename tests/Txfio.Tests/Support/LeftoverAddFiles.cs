using System.Text;
using System.Text.Json;

namespace Txfio.Tests.Support;

internal sealed class LeftoverAddFiles
{
    private LeftoverAddFiles(string journalPath, string stagingPath, string targetPath)
    {
        JournalPath = journalPath;
        StagingPath = stagingPath;
        TargetPath = targetPath;
    }

    internal string JournalPath { get; }

    internal string StagingPath { get; }

    internal string TargetPath { get; }

    internal static async Task<LeftoverAddFiles> WriteAddAsync(
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
        string? directory = System.IO.Path.GetDirectoryName(targetPath);
        string stagingPath = System.IO.Path.Combine(
            directory!,
            System.IO.Path.GetFileName(targetPath) + "." + transactionId.ToString("D") + ".txnew");
        await File.WriteAllTextAsync(stagingPath, content);

        string states = string.Empty;
        if (committing)
        {
            states = ",\"before\":" + SnapshotJson.Absent + ",\"after\":" + SnapshotJson.File(stagingPath);
        }

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"Add\",\"path\":" + JsonSerializer.Serialize(targetPath) +
            ",\"stagingPath\":" + JsonSerializer.Serialize(stagingPath) + states + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return new LeftoverAddFiles(journalPath, stagingPath, targetPath);
    }

    internal static MemoryStream Utf8Stream(string text)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }
}
