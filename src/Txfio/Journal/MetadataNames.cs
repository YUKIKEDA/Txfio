namespace Txfio;

internal static class MetadataNames
{
    internal const string FolderName = ".txfio";
    internal const string JournalSearchPattern = "tx-*.journal";

    internal static string FolderPath(string workFolder)
    {
        return System.IO.Path.Combine(workFolder, FolderName);
    }

    internal static string JournalPath(string workFolder, Guid transactionId)
    {
        return System.IO.Path.Combine(
            FolderPath(workFolder),
            "tx-" + transactionId.ToString("D") + ".journal");
    }
}
