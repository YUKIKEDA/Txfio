using System.Text.Json;

namespace Txfio.Tests.Support;

internal sealed class LeftoverMoveFiles
{
    private LeftoverMoveFiles(string journalPath, string sourcePath, string destPath)
    {
        JournalPath = journalPath;
        SourcePath = sourcePath;
        DestPath = destPath;
    }

    internal string JournalPath { get; }

    internal string SourcePath { get; }

    internal string DestPath { get; }

    internal static async Task<LeftoverMoveFiles> WriteMoveAsync(
        string workFolder,
        bool committing,
        string sourceName,
        string destName,
        string content,
        bool alreadyMoved = false)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string sourcePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, sourceName));
        string destPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, destName));
        if (alreadyMoved)
        {
            await File.WriteAllTextAsync(destPath, content);
        }
        else
        {
            await File.WriteAllTextAsync(sourcePath, content);
        }

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"Move\",\"path\":" + JsonSerializer.Serialize(sourcePath) +
            ",\"newPath\":" + JsonSerializer.Serialize(destPath) + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return new LeftoverMoveFiles(journalPath, sourcePath, destPath);
    }
}
