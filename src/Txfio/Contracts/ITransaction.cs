using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Txfio;

/// <summary>
/// The public contract of a transaction.
/// </summary>
/// <remarks>
/// Public members cannot be called concurrently; an overlapping call throws <see cref="InvalidOperationException"/> before it changes any state.
/// </remarks>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>
    /// Stages the content of a new file.
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="content">The content to write (owned by the caller).</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The target already exists and is not the source of a file Move, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the Add targets the source of a directory Move, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task AddAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages new content for an existing file.
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="content">The content to write (owned by the caller).</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The target does not exist, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task UpdateAsync(
        string path,
        Stream content,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules deleting an existing file or directory.
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the operation is scheduled.</returns>
    /// <exception cref="ExternalConflictException">The target does not exist, or the directory has an unexpected direct child.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules deleting a directory and everything under it.
    /// </summary>
    /// <param name="path">The target directory (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the operation is scheduled.</returns>
    /// <exception cref="ExternalConflictException">The target directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a file.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, this transaction has an operation under the directory, the path is under a directory Move, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task DeleteTreeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules moving a file or directory within one volume.
    /// </summary>
    /// <param name="oldPath">The source path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="newPath">The destination path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the operation is scheduled.</returns>
    /// <exception cref="ExternalConflictException">The source does not exist, the destination is occupied and is not the source of another Move, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the source, the destination, or the work folder.</exception>
    /// <exception cref="UnsupportedOperationException">The move crosses volumes.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the move is to the same path (including a difference only in case), the path is already staged by another operation, the move has no free end, the move is into a directory scheduled for deletion, an operation is under the source or destination, a directory is moved under itself, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task MoveAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules moving a file or directory within one volume.
    /// When <paramref name="overwrite"/> is <see langword="true"/>, replaces or swaps the existing file or directory at the destination with the source.
    /// Two files are replaced with one rename; when either is a directory, the destination is moved aside to <c>.txold</c> and then swapped.
    /// </summary>
    /// <remarks>
    /// Replacing a file with a file is one rename at commit (<c>MOVEFILE_REPLACE_EXISTING</c>), and no bytes are copied.
    /// A swap where the source or destination is a directory moves the destination aside to <c>{name}.{txid}.txold</c>, renames the source to the destination, and then deletes <c>.txold</c>.
    /// If this transaction has a file Delete at the destination, that Delete folds into the file replacement.
    /// A DeleteTree at the destination folds into the swap.
    /// After a replacement or a swap, no further operation is allowed on the source or destination.
    /// After a swap, no further operation is allowed under the destination either.
    /// </remarks>
    /// <param name="oldPath">The source path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="newPath">The destination path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="overwrite">When <see langword="true"/>, replaces or swaps the existing file or directory at the destination with the source (<see langword="false"/> is the same as <see cref="MoveAsync(string, string, CancellationToken)"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the operation is scheduled.</returns>
    /// <exception cref="ExternalConflictException">The source does not exist, the destination is occupied while <paramref name="overwrite"/> is <see langword="false"/>, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the source, the destination, or the work folder.</exception>
    /// <exception cref="UnsupportedOperationException">The move crosses volumes.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the move is to the same path, the path is already staged by another operation, an operation targets the source or destination of a replacing or swapping Move, an operation is under the destination of a swap, the move has no free end, the move is into a directory scheduled for deletion, an operation is under the source or destination, a directory is moved under itself, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task MoveAsync(string oldPath, string newPath, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an empty directory when called (normal operations work under it, and its contents can also be written with the plain file API).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the directory is created.</returns>
    /// <exception cref="ExternalConflictException">The target already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, there is an operation under it, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file or directory inside the work folder.
    /// </summary>
    /// <param name="source">The source (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="destination">The destination (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The source does not exist, the destination already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the source, the destination, or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the copy is to the same path or under itself, or the path is a symbolic link, a reparse point, or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task CopyAsync(
        string source,
        string destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file or directory from outside the work folder and adds it.
    /// </summary>
    /// <param name="externalPath">The source outside the work folder.</param>
    /// <param name="targetPath">The destination (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The source does not exist, the destination already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the destination or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the import is under itself, or the path is a symbolic link, a reparse point, or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The source is inside the work folder, or the destination is outside the work folder.</exception>
    Task ImportAsync(
        string externalPath,
        string targetPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file or directory in the work folder to outside the work folder.
    /// </summary>
    /// <param name="path">The source (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="externalPath">The destination outside the work folder.</param>
    /// <param name="progress">Receives copy progress (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the copy is done.</returns>
    /// <exception cref="ExternalConflictException">The source does not exist, the destination is occupied, or the parent directory does not exist.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a symbolic link, a reparse point, or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The source is outside the work folder, or the destination is inside the work folder.</exception>
    Task ExportAsync(
        string path,
        string externalPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a ZIP from a file or directory inside the work folder, and adds it inside the work folder.
    /// </summary>
    /// <param name="source">The input (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="archivePath">The path of the ZIP to create (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <param name="includeBaseDirectory">When the input is a directory, whether to include its name as the root of the entries.</param>
    /// <param name="progress">Receives the uncompressed bytes read (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The input does not exist, the ZIP path already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the input, the ZIP path, or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, there is an operation under the input, the ZIP path is under the input, or the path is a symbolic link, a reparse point, or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task CreateArchiveAsync(
        string source,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a ZIP that contains the given files and directories under the given names, and adds it inside the work folder.
    /// </summary>
    /// <param name="entries">Pairs of what to add and its name inside the ZIP (added in list order).</param>
    /// <param name="archivePath">The path of the ZIP to create (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <param name="progress">Receives the uncompressed bytes read (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException">The sequence, an element, or an element's path is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The input does not exist, the ZIP path already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the input, the ZIP path, or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, an input is already staged, there is an operation under an input, the ZIP path is under an input, or the path is a symbolic link, a reparse point, or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">A path is outside the work folder, or entry names are invalid, duplicated, or shared by a file and a directory.</exception>
    Task CreateArchiveAsync(
        IEnumerable<ArchiveEntrySource> entries,
        string archivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a ZIP outside the work folder from a file or directory inside the work folder.
    /// </summary>
    /// <param name="source">The input (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="externalArchivePath">The path of the ZIP to create outside the work folder.</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <param name="includeBaseDirectory">When the input is a directory, whether to include its name as the root of the entries.</param>
    /// <param name="progress">Receives the uncompressed bytes read (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the export is written.</returns>
    /// <exception cref="ExternalConflictException">The input does not exist, the ZIP path is occupied, or the parent directory does not exist.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a symbolic link, a reparse point, or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The input is outside the work folder, or the ZIP path is inside the work folder.</exception>
    Task ExportArchiveAsync(
        string source,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool includeBaseDirectory = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a ZIP outside the work folder that contains the given files and directories under the given names.
    /// </summary>
    /// <param name="entries">Pairs of what to add and its name inside the ZIP (added in list order).</param>
    /// <param name="externalArchivePath">The path of the ZIP to create outside the work folder.</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <param name="progress">Receives the uncompressed bytes read (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the export is written.</returns>
    /// <exception cref="ArgumentNullException">The sequence, an element, or an element's path is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The input does not exist, the ZIP path is occupied, or the parent directory does not exist.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a symbolic link, a reparse point, or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">An input is outside the work folder, the ZIP path is inside the work folder, or entry names are invalid, duplicated, or shared by a file and a directory.</exception>
    Task ExportArchiveAsync(
        IEnumerable<ArchiveEntrySource> entries,
        string externalArchivePath,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a ZIP inside the work folder into a new directory inside the work folder, and adds each file.
    /// </summary>
    /// <param name="archivePath">The ZIP to extract (relative to the work folder, or absolute inside the work folder; if it is staged, its staged content is read).</param>
    /// <param name="destinationDir">The new destination directory (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="entryNameEncoding">How to read entry names without the UTF-8 flag (the .NET default when <see langword="null"/>).</param>
    /// <param name="maxExtractedBytes">The limit on the total extracted bytes (no limit when <see langword="null"/>; pass one for ZIPs received from outside).</param>
    /// <param name="progress">Receives the extracted bytes and the total size of the entries (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The ZIP does not exist, the destination already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the destination or the work folder.</exception>
    /// <exception cref="UnsupportedOperationException">The ZIP path is a directory.</exception>
    /// <exception cref="InvalidDataException">A name leaves the destination, is not valid on Windows, or ends in <c>.txnew</c>; names are duplicated; a file and a directory share a name; or the declared total extracted size or the bytes actually read exceed <paramref name="maxExtractedBytes"/> (also when the total does not fit in a <see langword="long"/>, a file's Length is negative, or the ZIP itself cannot be read).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExtractedBytes"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, there is an operation under the destination, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task ExtractArchiveAsync(
        string archivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        long? maxExtractedBytes = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a ZIP outside the work folder into a new directory inside the work folder, and adds each file.
    /// </summary>
    /// <param name="externalArchivePath">The ZIP outside the work folder.</param>
    /// <param name="destinationDir">The new destination directory (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="entryNameEncoding">How to read entry names without the UTF-8 flag (the .NET default when <see langword="null"/>).</param>
    /// <param name="maxExtractedBytes">The limit on the total extracted bytes (no limit when <see langword="null"/>; pass one for ZIPs received from outside).</param>
    /// <param name="progress">Receives the extracted bytes and the total size of the entries (nothing is reported when <see langword="null"/>).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The ZIP does not exist, the destination already exists, or the parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the destination or the work folder.</exception>
    /// <exception cref="UnsupportedOperationException">The ZIP path is a directory.</exception>
    /// <exception cref="InvalidDataException">A name leaves the destination, is not valid on Windows, or ends in <c>.txnew</c>; names are duplicated; a file and a directory share a name; or the declared total extracted size or the bytes actually read exceed <paramref name="maxExtractedBytes"/> (also when the total does not fit in a <see langword="long"/>, a file's Length is negative, or the ZIP itself cannot be read).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExtractedBytes"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, there is an operation under the destination, or the path is a reparse point or under the metadata folder.</exception>
    /// <exception cref="ArgumentException">The ZIP path is inside the work folder, or the destination is outside the work folder.</exception>
    Task ImportArchiveAsync(
        string externalArchivePath,
        string destinationDir,
        Encoding? entryNameEncoding = null,
        long? maxExtractedBytes = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the file in the post-commit view.
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The cancellation token, honored only at the start of the call.</param>
    /// <returns>A read stream at position 0 (the caller disposes it).</returns>
    /// <exception cref="ExternalConflictException">The target does not exist.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a directory.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a file or directory exists in the post-commit view.
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The cancellation token, honored only at the start of the call.</param>
    /// <returns><see langword="true"/> if a file or directory exists.</returns>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the files and directories directly under a directory in the post-commit view (not recursive).
    /// </summary>
    /// <remarks>
    /// This transaction's staging files (<c>.txnew</c>), restage backups (<c>.txnew.prev</c>), and swap backups (<c>.txold</c>) are not included (those of other transactions are on disk, so they are visible).
    /// </remarks>
    /// <param name="directoryPath">The directory (relative to the work folder, absolute inside the work folder, or the work folder itself).</param>
    /// <param name="cancellationToken">The cancellation token, honored only at the start of the call.</param>
    /// <returns>One entry per direct child (in lexical order of the path, ignoring case).</returns>
    /// <exception cref="ExternalConflictException">The directory does not exist in the post-commit view.</exception>
    /// <exception cref="UnsupportedOperationException">It is a file in the post-commit view.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<IReadOnlyList<DirectoryEntry>> GetEntriesAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the bytes as a string (the encoding is detected from the BOM).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The string that was read.</returns>
    /// <exception cref="ExternalConflictException">The target does not exist.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a directory.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the bytes as a string with the given encoding (a BOM, if present, takes precedence).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="encoding">The encoding to use when there is no BOM.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The string that was read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The target does not exist.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a directory.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<string> ReadAllTextAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the bytes as an array of lines (the encoding is detected from the BOM).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>An array of lines without line breaks.</returns>
    /// <exception cref="ExternalConflictException">The target does not exist.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a directory.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the bytes as an array of lines with the given encoding (a BOM, if present, takes precedence).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="encoding">The encoding to use when there is no BOM.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>An array of lines without line breaks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The target does not exist.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a directory.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<string[]> ReadAllLinesAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a string (Add if the post-commit view has no file, otherwise Update; the encoding is UTF-8 without a BOM).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The string to write (<see langword="null"/> is written as an empty string).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task WriteAllTextAsync(
        string path,
        string? contents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a string with the given encoding (Add if the post-commit view has no file, otherwise Update).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The string to write (<see langword="null"/> is written as an empty string).</param>
    /// <param name="encoding">The encoding to write with.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task WriteAllTextAsync(
        string path,
        string? contents,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes lines (Add if the post-commit view has no file, otherwise Update; the encoding is UTF-8 without a BOM).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The lines to write (each without its line break).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task WriteAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes lines with the given encoding (Add if the post-commit view has no file, otherwise Update).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The lines to write (each without its line break).</param>
    /// <param name="encoding">The encoding to write with.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> or <paramref name="encoding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task WriteAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a string in UTF-8 without a BOM (Add if the post-commit view has no file, otherwise an Update with the appended content).
    /// </summary>
    /// <remarks>
    /// The existing content is read into memory too, so build large files with the <see cref="Stream"/> APIs.
    /// </remarks>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The string to append (<see langword="null"/> is written as an empty string).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The parent directory does not exist, or the target is a directory.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task AppendAllTextAsync(
        string path,
        string? contents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a string with the given encoding (the appended part gets no BOM; only a new file gets one).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The string to append (<see langword="null"/> is written as an empty string).</param>
    /// <param name="encoding">The encoding of the appended part.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The parent directory does not exist, or the target is a directory.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task AppendAllTextAsync(
        string path,
        string? contents,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends lines in UTF-8 without a BOM (Add if the post-commit view has no file, otherwise an Update with the appended content; a line break is written after each line).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The lines to append (each without its line break).</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The parent directory does not exist, or the target is a directory.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task AppendAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends lines with the given encoding (the appended part gets no BOM; only a new file gets one).
    /// </summary>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="contents">The lines to append (each without its line break).</param>
    /// <param name="encoding">The encoding of the appended part.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> or <paramref name="encoding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExternalConflictException">The parent directory does not exist, or the target is a directory.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task AppendAllLinesAsync(
        string path,
        IEnumerable<string> contents,
        Encoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the bytes as JSON.
    /// </summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="options">When omitted, the <see cref="JsonSerializer"/> defaults.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="JsonException">The content cannot be read as JSON.</exception>
    /// <exception cref="ExternalConflictException">The target does not exist.</exception>
    /// <exception cref="UnsupportedOperationException">The target is a directory.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task<T?> ReadFromJsonAsync<T>(
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a value as JSON (Add if the post-commit view has no file, otherwise Update).
    /// </summary>
    /// <typeparam name="T">The type to write.</typeparam>
    /// <param name="path">The target path (relative to the work folder, or absolute inside the work folder).</param>
    /// <param name="value">The value to write.</param>
    /// <param name="options">When omitted, the <see cref="JsonSerializer"/> defaults.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task that completes when the content is staged.</returns>
    /// <exception cref="ExternalConflictException">The parent directory does not exist.</exception>
    /// <exception cref="LockContentionException">Another transaction holds the target or the work folder.</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, the path is already staged by another operation, the path is a reparse point or under the metadata folder, or the transaction is already committed.</exception>
    /// <exception cref="ArgumentException">The path is outside the work folder.</exception>
    Task WriteAsJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the staged changes to the work folder.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token, honored until the commit starts.</param>
    /// <returns>The overall result, and the operations that were rejected or skipped.</returns>
    /// <exception cref="RecoveryRequiredException">An orphaned journal remains (the transaction stays uncommitted).</exception>
    /// <exception cref="InvalidOperationException">Calls overlap, or the transaction is already committed.</exception>
    Task<CommitReport> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of unfinished operations in the current journal.
    /// </summary>
    /// <returns>The list of operations (may be empty at this point).</returns>
    /// <exception cref="InvalidOperationException">Calls overlap.</exception>
    IReadOnlyList<PendingChange> GetPendingChanges();
}
