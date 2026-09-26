using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class DisposeCleanupTests
{
    /// <summary>
    /// 後片付けの途中で失敗しても本体の例外が届く
    /// </summary>
    /// <remarks>
    /// <para>前提: Add が 2 件と空の CreateDirectory があり、先の .txnew を共有なしで開いている</para>
    /// <para>手順: その状態で InvalidOperationException を出して Dispose する</para>
    /// <para>期待: 届く例外は InvalidOperationException であり、後の .txnew と空ディレクトリは消え、開いている .txnew とジャーナルは残る</para>
    /// </remarks>
    [WindowsFact("開いたファイルは削除できない")]
    public async Task DisposeAsync_後片付けが失敗しても本体の例外が届くこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        FileStream? hold = null;
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path);
                await using MemoryStream first = LeftoverAddFiles.Utf8Stream("a");
                await transaction.AddAsync("a.txt", first);
                await transaction.CreateDirectoryAsync("empty");
                await using MemoryStream second = LeftoverAddFiles.Utf8Stream("b");
                await transaction.AddAsync("b.txt", second);
                string locked = Assert.Single(
                    Directory.GetFiles(work.Path, "*.txnew"),
                    path => System.IO.Path.GetFileName(path).StartsWith("a.txt.", StringComparison.OrdinalIgnoreCase));
                hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
                throw new InvalidOperationException("元の失敗");
            });

            Assert.Equal("元の失敗", error.Message);
            Assert.NotNull(hold);
            Assert.True(File.Exists(hold.Name));
            Assert.DoesNotContain(
                Directory.GetFiles(work.Path, "*.txnew"),
                path => System.IO.Path.GetFileName(path).StartsWith("b.txt.", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(System.IO.Path.Combine(work.Path, "empty")));
            Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
            RecoveryRequiredException required = await Assert.ThrowsAsync<RecoveryRequiredException>(
                () => global::Txfio.Txfio.BeginAsync(work.Path));
            Assert.Equal(work.Path, required.Path, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (hold is not null)
            {
                await hold.DisposeAsync();
            }
        }

        Assert.Equal(RecoverResult.RolledBack, (await global::Txfio.Txfio.RecoverAsync(work.Path)).Result);
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
    }

    /// <summary>
    /// 後片付けが成功すると本体の例外が届き、ジャーナルは消える
    /// </summary>
    /// <remarks>
    /// <para>前提: Add が 1 件ある</para>
    /// <para>手順: InvalidOperationException を出して Dispose する</para>
    /// <para>期待: 届く例外は InvalidOperationException であり、.txnew とジャーナルは無い</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_後片付けが成功すると本体の例外が届きジャーナルは消えること()
    {
        await using TempDirectory work = TempDirectory.Create();
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using ITransaction transaction = await global::Txfio.Txfio.BeginAsync(work.Path);
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("a");
            await transaction.AddAsync("a.txt", content);
            throw new InvalidOperationException("元の失敗");
        });

        Assert.Equal("元の失敗", error.Message);
        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(work.Path, ".txfio"), "tx-*.journal"));
    }

    /// <summary>
    /// 破棄は操作の .txnew に .prev を付けた退避を消し、操作に無い退避は探さない
    /// </summary>
    /// <remarks>
    /// <para>前提: a.txt の Add があり、その .txnew.prev と、操作に無い別フォルダの同じトランザクション ID の .txnew.prev を置く</para>
    /// <para>手順: DisposeAsync する</para>
    /// <para>期待: Add の .txnew と .prev とジャーナルは消え、操作に無い .prev は残る（ワークフォルダを走査しない）</para>
    /// </remarks>
    [Fact]
    public async Task DisposeAsync_操作の退避だけを消してワークフォルダを走査しないこと()
    {
        await using TempDirectory work = TempDirectory.Create();
        string metadata = System.IO.Path.Combine(work.Path, ".txfio");
        string other = System.IO.Path.Combine(work.Path, "other");
        Directory.CreateDirectory(other);
        string backup;
        string unrelated;
        await using (ITransaction tx = await global::Txfio.Txfio.BeginAsync(work.Path))
        {
            await using MemoryStream content = LeftoverAddFiles.Utf8Stream("new");
            await tx.AddAsync("a.txt", content);
            string staging = Assert.Single(Directory.GetFiles(work.Path, "*.txnew"));
            backup = staging + ".prev";
            await File.WriteAllTextAsync(backup, "old");
            string journal = Assert.Single(Directory.GetFiles(metadata, "tx-*.journal"));
            string id = System.IO.Path.GetFileNameWithoutExtension(journal).Substring("tx-".Length);
            unrelated = System.IO.Path.Combine(other, "b.txt." + id + ".txnew.prev");
            await File.WriteAllTextAsync(unrelated, "keep");
        }

        Assert.Empty(Directory.GetFiles(work.Path, "*.txnew"));
        Assert.False(File.Exists(backup));
        Assert.Empty(Directory.GetFiles(metadata, "tx-*.journal"));
        Assert.True(File.Exists(unrelated));
    }
}
