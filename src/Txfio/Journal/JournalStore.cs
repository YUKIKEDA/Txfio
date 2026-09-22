using System.Text.Json;

namespace Txfio;

internal static class JournalStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    internal static async Task WriteNewAsync(string journalPath, Guid transactionId, CancellationToken cancellationToken)
    {
        JournalDocument document = new JournalDocument(CurrentVersion, transactionId, committing: false);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

        FileStream stream = new FileStream(
            journalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task<JournalDocument?> TryReadAsync(string journalPath, CancellationToken cancellationToken)
    {
        try
        {
            byte[] payload = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JournalDocument>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static Task DeleteAsync(string journalPath)
    {
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }

        return Task.CompletedTask;
    }
}
