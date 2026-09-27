using System.Diagnostics;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class ReparsePathTests
{
    /// <summary>
    /// An Add through a junction that points outside cannot write.
    /// </summary>
    /// <remarks>
    /// <para>Given: a junction in the work folder points to a directory outside.</para>
    /// <para>When: AddAsync is called on a path under it.</para>
    /// <para>Then: InvalidOperationException, the outside is empty, and there is no .txnew.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task AddAsync_CannotWriteThroughJunctionPointingOutside()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string link = System.IO.Path.Combine(work.Path, "link");
        CreateJunction(link, outside.Path);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("secret");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tx.AddAsync(System.IO.Path.Combine("link", "a.txt"), content));

            Assert.Contains("A reparse point cannot be used", error.Message, StringComparison.Ordinal);
            Assert.Contains(link, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
            Assert.Empty(Directory.GetFiles(work.Path, "*.txnew", SearchOption.AllDirectories));
            Assert.Empty(tx.GetPendingChanges());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// An Add through a junction that points inside cannot write either.
    /// </summary>
    /// <remarks>
    /// <para>Given: a junction in the work folder points to a directory in the same work folder.</para>
    /// <para>When: AddAsync is called on a path under it.</para>
    /// <para>Then: InvalidOperationException, and the directory it points to has no file.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task AddAsync_CannotWriteThroughJunctionPointingInside()
    {
        await using TempDirectory work = TempDirectory.Create();
        string real = System.IO.Path.Combine(work.Path, "real");
        Directory.CreateDirectory(real);
        string link = System.IO.Path.Combine(work.Path, "link");
        CreateJunction(link, real);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("secret");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tx.AddAsync(System.IO.Path.Combine("link", "a.txt"), content));

            Assert.Contains("A reparse point cannot be used", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(real));
            Assert.Empty(tx.GetPendingChanges());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// It is rejected even for a path that does not exist beyond the junction.
    /// </summary>
    /// <remarks>
    /// <para>Given: a junction in the work folder points to a directory outside.</para>
    /// <para>When: AddAsync is called on a path under a directory that does not exist beyond the junction.</para>
    /// <para>Then: InvalidOperationException, and there is no directory or file outside.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task AddAsync_RejectsMissingPathBeyondJunction()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        string link = System.IO.Path.Combine(work.Path, "link");
        CreateJunction(link, outside.Path);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("secret");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tx.AddAsync(System.IO.Path.Combine("link", "missing", "a.txt"), content));

            Assert.Contains("A reparse point cannot be used", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
            Assert.Empty(tx.GetPendingChanges());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// Even if the work folder itself is a junction, paths directly under it can be written.
    /// </summary>
    /// <remarks>
    /// <para>Given: a junction to a real directory.</para>
    /// <para>When: the junction is used as the work folder, and AddAsync is called directly under it.</para>
    /// <para>Then: one pending Add, the real directory has the .txnew, and the target file does not exist.</para>
    /// </remarks>
    [WindowsFact("Junctions (mklink /J)")]
    public async Task BeginAsync_CanAddUnderWorkFolderThatIsJunction()
    {
        await using TempDirectory parent = TempDirectory.Create();
        string real = System.IO.Path.Combine(parent.Path, "real");
        Directory.CreateDirectory(real);
        string link = System.IO.Path.Combine(parent.Path, "link");
        CreateJunction(link, real);
        try
        {
            await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(link);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("hello");

            await tx.AddAsync("a.txt", content);

            Assert.False(File.Exists(System.IO.Path.Combine(real, "a.txt")));
            Assert.Single(Directory.GetFiles(real, "*.txnew"));
            Assert.Equal(PendingChangeKind.Add, Assert.Single(tx.GetPendingChanges()).Kind);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// A path passed outside the work folder stays ArgumentException.
    /// </summary>
    /// <remarks>
    /// <para>Given: a work folder, and a directory outside it.</para>
    /// <para>When: AddAsync is called with an absolute path outside.</para>
    /// <para>Then: ArgumentException, and there is no file outside.</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_PassedPathOutsideWorkFolderThrowsArgumentException()
    {
        await using TempDirectory work = TempDirectory.Create();
        await using TempDirectory outside = TempDirectory.Create();
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await using MemoryStream content = LeftoverAddFiles.Utf8Stream("secret");
        string outsideFile = System.IO.Path.Combine(outside.Path, "a.txt");

        await Assert.ThrowsAsync<ArgumentException>(() => tx.AddAsync(outsideFile, content));

        Assert.False(File.Exists(outsideFile));
        Assert.Empty(tx.GetPendingChanges());
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        using Process process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
