using Txfio.Tests.Support;

namespace Txfio.Tests.Commit;

public sealed class CommitCopyTests
{
    /// <summary>
    /// A file copy creates the destination at commit and keeps the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a.txt is copied to b.txt.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, and both have the same content.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_FileCopyKeepsBoth()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "a.txt");
        string destination = System.IO.Path.Combine(work.Path, "b.txt");
        await File.WriteAllTextAsync(source, "copied");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CopyAsync("a.txt", "b.txt");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("copied", await File.ReadAllTextAsync(source));
        Assert.Equal("copied", await File.ReadAllTextAsync(destination));
    }

    /// <summary>
    /// A directory copy keeps its contents and empty directories at commit.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with a file and an empty subdirectory is copied.</para>
    /// <para>When: CommitAsync runs.</para>
    /// <para>Then: Succeeded, the destination has the file and the empty directory, and the source remains.</para>
    /// </remarks>
    [Fact]
    public async Task CommitAsync_DirectoryCopyKeepsEmptyDirectories()
    {
        await using TempDirectory work = TempDirectory.Create();
        string source = System.IO.Path.Combine(work.Path, "src");
        string destination = System.IO.Path.Combine(work.Path, "dest");
        Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "copied");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.CopyAsync("src", "dest");

        CommitReport result = await tx.CommitAsync();

        Assert.Equal(CommitResult.Succeeded, result.Result);
        Assert.Equal("copied", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
        Assert.Equal("copied", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.Empty(Directory.GetFiles(destination, "*.txnew", SearchOption.AllDirectories));
    }
}
