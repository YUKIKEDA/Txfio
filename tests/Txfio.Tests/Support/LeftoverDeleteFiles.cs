using System.Text.Json;

namespace Txfio.Tests.Support;

internal sealed class LeftoverDeleteFiles
{
    private LeftoverDeleteFiles(string journalPath, string targetPath)
    {
        JournalPath = journalPath;
        TargetPath = targetPath;
    }

    internal string JournalPath { get; }

    internal string TargetPath { get; }

    internal static async Task<LeftoverDeleteFiles> WriteDeleteAsync(
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

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"Delete\",\"path\":" + JsonSerializer.Serialize(targetPath) + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return new LeftoverDeleteFiles(journalPath, targetPath);
    }
}
