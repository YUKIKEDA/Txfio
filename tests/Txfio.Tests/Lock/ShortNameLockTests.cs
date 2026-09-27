using System.Runtime.InteropServices;
using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class ShortNameLockTests
{
    /// <summary>
    /// A short name and a long name map to the same lock.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a long name, which also has an 8.3 short name.</para>
    /// <para>When: one transaction adds with the long name, and the other with the short name.</para>
    /// <para>Then: LockContentionException, and Path is the file with the long name.</para>
    /// </remarks>
    [ShortNameFact]
    public async Task AddAsync_ShortAndLongNamesShareOneLock()
    {
        await using TempDirectory parent = TempDirectory.Create();
        string longDirectory = System.IO.Path.Combine(parent.Path, "Program Files");
        Directory.CreateDirectory(longDirectory);
        string? shortDirectory = QueryShortPath(longDirectory);
        Assert.NotNull(shortDirectory);
        string longFile = System.IO.Path.Combine(longDirectory, "a.txt");
        await using ITransaction first = await global::Txfio.Txfio.BeginAsync(parent.Path);
        await using ITransaction second = await global::Txfio.Txfio.BeginAsync(parent.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await first.AddAsync(longFile, content);

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => second.AddAsync(System.IO.Path.Combine(shortDirectory, "a.txt"), again));

        Assert.Equal(longFile, contention.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// In the same transaction, a short name and a long name become one entry.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a long name, which also has an 8.3 short name.</para>
    /// <para>When: an Add with the short name is followed by an Add with the long name.</para>
    /// <para>Then: there is one pending change, and its path is the file with the long name.</para>
    /// </remarks>
    [ShortNameFact]
    public async Task AddAsync_ShortAndLongNamesBecomeOneEntryInSameTransaction()
    {
        await using TempDirectory parent = TempDirectory.Create();
        string longDirectory = System.IO.Path.Combine(parent.Path, "Program Files");
        Directory.CreateDirectory(longDirectory);
        string? shortDirectory = QueryShortPath(longDirectory);
        Assert.NotNull(shortDirectory);
        string longFile = System.IO.Path.Combine(longDirectory, "a.txt");
        await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(parent.Path);
        await using MemoryStream first = LeftoverAddFiles.Utf8Stream("one");
        await transaction.AddAsync(System.IO.Path.Combine(shortDirectory, "a.txt"), first);
        await using MemoryStream second = LeftoverAddFiles.Utf8Stream("two");
        await transaction.AddAsync(longFile, second);

        PendingChange pending = Assert.Single(transaction.GetPendingChanges());
        Assert.Equal(longFile, pending.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(PendingChangeKind.Add, pending.Kind);
    }

    /// <summary>
    /// A missing intermediate directory is appended to the long parent as the short name given.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a long name, which also has an 8.3 short name, and no directory under the short name.</para>
    /// <para>When: a file is added under a directory that does not exist under the short name.</para>
    /// <para>Then: the Path of ExternalConflictException is the long-name parent with the missing directory as passed appended.</para>
    /// </remarks>
    [ShortNameFact]
    public async Task AddAsync_MissingIntermediateDirectoryIsAppendedToLongParent()
    {
        await using TempDirectory parent = TempDirectory.Create();
        string longDirectory = System.IO.Path.Combine(parent.Path, "Program Files");
        Directory.CreateDirectory(longDirectory);
        string? shortDirectory = QueryShortPath(longDirectory);
        Assert.NotNull(shortDirectory);
        await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(parent.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");

        ExternalConflictException error = await Assert.ThrowsAsync<ExternalConflictException>(
            () => transaction.AddAsync(System.IO.Path.Combine(shortDirectory, "missing", "a.txt"), content));

        Assert.Equal(
            System.IO.Path.Combine(longDirectory, "missing"),
            error.Path,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A work folder opened with its short name shares locks with the long name.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a long name, which also has an 8.3 short name.</para>
    /// <para>When: one side begins with the short name and adds, and the side that began with the long name adds the same file.</para>
    /// <para>Then: LockContentionException, and the pending path is the file with the long name.</para>
    /// </remarks>
    [ShortNameFact]
    public async Task BeginAsync_WorkFolderOpenedWithShortNameSharesLocksWithLongName()
    {
        await using TempDirectory parent = TempDirectory.Create();
        string longWork = System.IO.Path.Combine(parent.Path, "Program Files");
        Directory.CreateDirectory(longWork);
        string? shortWork = QueryShortPath(longWork);
        Assert.NotNull(shortWork);
        await using ITransaction viaShort = await global::Txfio.Txfio.BeginAsync(shortWork);
        await using ITransaction viaLong = await global::Txfio.Txfio.BeginAsync(longWork);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
        await viaShort.AddAsync("a.txt", content);

        await using MemoryStream again = LeftoverAddFiles.Utf8Stream("other");
        LockContentionException contention = await Assert.ThrowsAsync<LockContentionException>(
            () => viaLong.AddAsync("a.txt", again));

        string longFile = System.IO.Path.Combine(longWork, "a.txt");
        Assert.Equal(longFile, contention.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(longFile, Assert.Single(viaShort.GetPendingChanges()).Path, StringComparer.OrdinalIgnoreCase);
    }

    private static string? QueryShortPath(string path)
    {
        var buffer = new StringBuilder(Math.Max(path.Length + 1, 260));
        uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        return buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

    private sealed class ShortNameFactAttribute : FactAttribute
    {
        public ShortNameFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows only: 8.3 short names";
                return;
            }

            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "txfio-83-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string nested = System.IO.Path.Combine(directory, "Program Files");
                Directory.CreateDirectory(nested);
                string? shortPath = QueryShortPath(nested);
                if (shortPath is null
                    || string.Equals(shortPath, nested, StringComparison.OrdinalIgnoreCase))
                {
                    Skip = "No 8.3 short name";
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
