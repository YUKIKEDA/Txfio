using System.Text.Json;

namespace Txfio.Tests.Support;

internal sealed class LeftoverApplyOrderFiles
{
    private LeftoverApplyOrderFiles(
        string journalPath,
        string sourcePath,
        string destPath,
        string stagingPath)
    {
        JournalPath = journalPath;
        SourcePath = sourcePath;
        DestPath = destPath;
        StagingPath = stagingPath;
    }

    internal string JournalPath { get; }

    internal string SourcePath { get; }

    internal string DestPath { get; }

    internal string StagingPath { get; }

    internal static async Task<LeftoverApplyOrderFiles> WriteUpdateThenMoveAsync(
        string workFolder,
        bool committing)
    {
        string metadata = System.IO.Path.Combine(workFolder, ".txfio");
        Directory.CreateDirectory(metadata);
        Guid transactionId = Guid.NewGuid();
        string journalPath = System.IO.Path.Combine(metadata, "tx-" + transactionId.ToString("D") + ".journal");
        string sourcePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, "a.txt"));
        string destPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workFolder, "b.txt"));
        string stagingPath = destPath + "." + transactionId.ToString("D") + ".txnew";
        await File.WriteAllTextAsync(sourcePath, "moved");
        await File.WriteAllTextAsync(stagingPath, "updated");
        string sourceState = SnapshotJson.File(sourcePath);
        string updatedState = SnapshotJson.File(stagingPath);

        string committingLiteral = committing ? "true" : "false";
        string json = "{\"version\":1,\"transactionId\":\"" + transactionId.ToString("D") +
            "\",\"committing\":" + committingLiteral +
            ",\"operations\":[{\"kind\":\"Update\",\"path\":" + JsonSerializer.Serialize(destPath) +
            ",\"stagingPath\":" + JsonSerializer.Serialize(stagingPath) +
            ",\"before\":" + sourceState +
            ",\"after\":" + updatedState +
            "},{\"kind\":\"Move\",\"path\":" + JsonSerializer.Serialize(sourcePath) +
            ",\"newPath\":" + JsonSerializer.Serialize(destPath) +
            ",\"before\":" + sourceState +
            ",\"after\":" + SnapshotJson.Absent +
            ",\"destBefore\":" + SnapshotJson.Absent +
            ",\"destAfter\":" + sourceState + "}]}";
        await File.WriteAllTextAsync(journalPath, json);
        return new LeftoverApplyOrderFiles(journalPath, sourcePath, destPath, stagingPath);
    }
}
