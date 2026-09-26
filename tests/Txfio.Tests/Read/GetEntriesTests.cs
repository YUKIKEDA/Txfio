using Txfio.Tests.Support;

namespace Txfio.Tests.Read;

public sealed class GetEntriesTests
{
    /// <summary>
    /// 直下の一覧は、コミット後の姿を返す
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt、b.txt、ディレクトリ d がある</para>
    /// <para>手順: a.txt を Delete、c.txt を Add、b.txt を e.txt へ Move してから、ワークフォルダ自身の直下を一覧する</para>
    /// <para>期待: c.txt、d、e.txt の 3 件であり、d だけディレクトリである</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_コミット後の姿の直下を返すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "b.txt"), "b");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteAsync("a.txt");
        await tx.WriteAllTextAsync("c.txt", "c");
        await tx.MoveAsync("b.txt", "e.txt");

        IReadOnlyList<DirectoryEntry> entries = await tx.GetEntriesAsync(".");

        Assert.Equal(new[] { "c.txt", "d", "e.txt" }, entries.Select(entry => System.IO.Path.GetFileName(entry.Path)).ToArray());
        Assert.Equal(new[] { false, true, false }, entries.Select(entry => entry.IsDirectory).ToArray());
    }

    /// <summary>
    /// ディレクトリ Move の移動先は移動元の中身を返し、移動元は無い
    /// </summary>
    /// <remarks>
    /// <para>前提: d/x.txt がある</para>
    /// <para>手順: Move(d→e) してから e と d を一覧する</para>
    /// <para>期待: e は e/x.txt の 1 件、d は ExternalConflictException</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_ディレクトリMoveの移動先は移動元の中身を返すこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "d"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "d", "x.txt"), "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.MoveAsync("d", "e");

        IReadOnlyList<DirectoryEntry> entries = await tx.GetEntriesAsync("e");

        Assert.Equal(System.IO.Path.Combine(work.Path, "e", "x.txt"), Assert.Single(entries).Path);
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.GetEntriesAsync("d"));
    }

    /// <summary>
    /// ファイルと、DeleteTree の対象は一覧できない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt と t/x.txt がある</para>
    /// <para>手順: t を DeleteTree してから、a.txt と t を一覧する</para>
    /// <para>期待: a.txt は UnsupportedOperationException、t は ExternalConflictException</para>
    /// </remarks>
    [Fact]
    public async Task GetEntriesAsync_ファイルと消えるディレクトリは一覧できないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "a.txt"), "a");
        Directory.CreateDirectory(System.IO.Path.Combine(work.Path, "t"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(work.Path, "t", "x.txt"), "x");
        await using ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path);
        await tx.DeleteTreeAsync("t");

        await Assert.ThrowsAsync<UnsupportedOperationException>(() => tx.GetEntriesAsync("a.txt"));
        await Assert.ThrowsAsync<ExternalConflictException>(() => tx.GetEntriesAsync("t"));
    }
}
