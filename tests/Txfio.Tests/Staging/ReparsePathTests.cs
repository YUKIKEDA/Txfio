using System.Diagnostics;
using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class ReparsePathTests
{
    /// <summary>
    /// 外を指すジャンクション経由の Add は書けない
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダ内のジャンクションが、外のディレクトリを指している</para>
    /// <para>手順: その配下へ AddAsync する</para>
    /// <para>期待: InvalidOperationException になり、外は空で、.txnew も無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task AddAsync_外を指すジャンクション経由では書けないこと()
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

            Assert.Contains("リパースポイントは操作できません", error.Message, StringComparison.Ordinal);
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
    /// 内側を指すジャンクション経由の Add も書けない
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダ内のジャンクションが、同じワークフォルダ内のディレクトリを指している</para>
    /// <para>手順: その配下へ AddAsync する</para>
    /// <para>期待: InvalidOperationException になり、指しているディレクトリにファイルは無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task AddAsync_内側を指すジャンクション経由でも書けないこと()
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

            Assert.Contains("リパースポイントは操作できません", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(real));
            Assert.Empty(tx.GetPendingChanges());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// ジャンクションの先に無いパスでも拒否する
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダ内のジャンクションが、外のディレクトリを指している</para>
    /// <para>手順: ジャンクションの先に無いディレクトリ配下へ AddAsync する</para>
    /// <para>期待: InvalidOperationException になり、外にディレクトリもファイルも無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task AddAsync_ジャンクションの先に無いパスでも拒否すること()
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

            Assert.Contains("リパースポイントは操作できません", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
            Assert.Empty(tx.GetPendingChanges());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// ワークフォルダ自身がジャンクションでも直下には書ける
    /// </summary>
    /// <remarks>
    /// <para>前提: 実ディレクトリへのジャンクションがある</para>
    /// <para>手順: そのジャンクションをワークフォルダにして、直下へ AddAsync する</para>
    /// <para>期待: pending は Add 1 件で、実ディレクトリに .txnew があり、対象ファイルは無い</para>
    /// </remarks>
    [WindowsFact("ジャンクション（mklink /J）")]
    public async Task BeginAsync_ワークフォルダがジャンクションでも直下へAddできること()
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
    /// 渡したパスがワークフォルダの外なら ArgumentException のまま
    /// </summary>
    /// <remarks>
    /// <para>前提: ワークフォルダと、その外のディレクトリがある</para>
    /// <para>手順: 外の絶対パスへ AddAsync する</para>
    /// <para>期待: ArgumentException になり、外にファイルは無い</para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_渡したパスがワークフォルダの外ならArgumentExceptionになること()
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
