using System.Runtime.InteropServices;
using System.Text;
using Txfio.Tests.Support;

namespace Txfio.Tests.Lock;

public sealed class ShortNameLockTests
{
    /// <summary>
    /// 短縮名と長い名前は同じロックになる
    /// </summary>
    /// <remarks>
    /// <para>前提: 長い名前のディレクトリがあり、その 8.3 短縮名もある</para>
    /// <para>手順: 一方が長い名前で Add し、もう一方が短縮名で Add する</para>
    /// <para>期待: LockContentionException になり、Path は長い名前のファイルである</para>
    /// </remarks>
    [ShortNameFact]
    public async Task AddAsync_短縮名と長い名前は同じロックになること()
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
    /// 同じトランザクションでは短縮名と長い名前は 1 件になる
    /// </summary>
    /// <remarks>
    /// <para>前提: 長い名前のディレクトリがあり、その 8.3 短縮名もある</para>
    /// <para>手順: 短縮名で Add したあと、長い名前で Add する</para>
    /// <para>期待: pending は 1 件であり、パスは長い名前のファイルである</para>
    /// </remarks>
    [ShortNameFact]
    public async Task AddAsync_同じトランザクションでは短縮名と長い名前が1件になること()
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
    /// 無い中間ディレクトリは短縮名のまま長い親に付く
    /// </summary>
    /// <remarks>
    /// <para>前提: 長い名前のディレクトリがあり、その 8.3 短縮名もあり、短縮名の先にディレクトリは無い</para>
    /// <para>手順: 短縮名の先に無いディレクトリへ Add する</para>
    /// <para>期待: ExternalConflictException の Path は、長い名前の親に渡したまだ無いディレクトリを付けたものである</para>
    /// </remarks>
    [ShortNameFact]
    public async Task AddAsync_無い中間ディレクトリは短縮名のまま長い親に付くこと()
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
    /// 短縮名で開いたワークフォルダは長い名前と同じロックになる
    /// </summary>
    /// <remarks>
    /// <para>前提: 長い名前のディレクトリがあり、その 8.3 短縮名もある</para>
    /// <para>手順: 短縮名で Begin して Add し、長い名前で Begin した側が同じファイルを Add する</para>
    /// <para>期待: LockContentionException になり、pending のパスは長い名前のファイルである</para>
    /// </remarks>
    [ShortNameFact]
    public async Task BeginAsync_短縮名で開いたワークフォルダは長い名前でロックを共有すること()
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
                Skip = "Windows 専用: 8.3 短縮名";
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
                    Skip = "8.3 短縮名が無い";
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
