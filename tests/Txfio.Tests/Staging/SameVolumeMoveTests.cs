using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class SameVolumeMoveTests
{
    /// <summary>
    /// 同じボリュームのファイル移動は元を残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルがある</para>
    /// <para>手順: 同じディレクトリへ MoveFile する</para>
    /// <para>期待: 移動元は無く、移動先に内容がある</para>
    /// </remarks>
    [Fact]
    public async Task MoveFile_同じボリュームでは元を残さないこと()
    {
        await using TempDirectory directory = TempDirectory.Create();
        string source = System.IO.Path.Combine(directory.Path, "a.txt");
        string destination = System.IO.Path.Combine(directory.Path, "b.txt");
        await File.WriteAllTextAsync(source, "hello");

        SameVolumeMove.MoveFile(source, destination);

        Assert.False(File.Exists(source));
        Assert.Equal("hello", await File.ReadAllTextAsync(destination));
    }

    /// <summary>
    /// 同じボリュームのディレクトリ移動は元を残さない
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルを1つ持つディレクトリがある</para>
    /// <para>手順: 同じ親の下へ MoveDirectory する</para>
    /// <para>期待: 移動元は無く、移動先にそのファイルがある</para>
    /// </remarks>
    [Fact]
    public async Task MoveDirectory_同じボリュームでは元を残さないこと()
    {
        await using TempDirectory directory = TempDirectory.Create();
        string source = System.IO.Path.Combine(directory.Path, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "hello");
        string destination = System.IO.Path.Combine(directory.Path, "dest");

        SameVolumeMove.MoveDirectory(source, destination);

        Assert.False(Directory.Exists(source));
        Assert.Equal("hello", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "a.txt")));
    }

    /// <summary>
    /// 別ボリュームのファイル移動はコピーしない
    /// </summary>
    /// <remarks>
    /// <para>前提: 一時ディレクトリと、別の固定ボリューム上の空ディレクトリがある</para>
    /// <para>手順: そのボリュームへ MoveFile する</para>
    /// <para>期待: IOException になり、移動元は残り、移動先のファイルは無い</para>
    /// </remarks>
    [OtherFixedVolumeFact]
    public async Task MoveFile_別ボリュームではコピーしないこと()
    {
        await using TempDirectory directory = TempDirectory.Create();
        string? other = CreateOtherVolumeDirectory();
        Assert.NotNull(other);
        try
        {
            string source = System.IO.Path.Combine(directory.Path, "a.txt");
            await File.WriteAllTextAsync(source, "secret");
            string destination = System.IO.Path.Combine(other, "a.txt");

            Assert.Throws<IOException>(() => SameVolumeMove.MoveFile(source, destination));

            Assert.Equal("secret", await File.ReadAllTextAsync(source));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(other))
            {
                Directory.Delete(other, recursive: true);
            }
        }
    }

    /// <summary>
    /// 別ボリュームのディレクトリ移動はコピーしない
    /// </summary>
    /// <remarks>
    /// <para>前提: ファイルを1つ持つディレクトリと、別の固定ボリューム上の空ディレクトリがある</para>
    /// <para>手順: そのボリュームへ MoveDirectory する</para>
    /// <para>期待: IOException になり、移動元のファイルは残り、移動先のディレクトリは無い</para>
    /// </remarks>
    [OtherFixedVolumeFact]
    public async Task MoveDirectory_別ボリュームではコピーしないこと()
    {
        await using TempDirectory directory = TempDirectory.Create();
        string? other = CreateOtherVolumeDirectory();
        Assert.NotNull(other);
        try
        {
            string source = System.IO.Path.Combine(directory.Path, "src");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(System.IO.Path.Combine(source, "a.txt"), "secret");
            string destination = System.IO.Path.Combine(other, "dest");

            Assert.Throws<IOException>(() => SameVolumeMove.MoveDirectory(source, destination));

            Assert.Equal("secret", await File.ReadAllTextAsync(System.IO.Path.Combine(source, "a.txt")));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(other))
            {
                Directory.Delete(other, recursive: true);
            }
        }
    }

    private static string? CreateOtherVolumeDirectory()
    {
        string? current = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath());
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            if (string.Equals(drive.RootDirectory.FullName, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = System.IO.Path.Combine(drive.RootDirectory.FullName, "txfio-tests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    private sealed class OtherFixedVolumeFactAttribute : FactAttribute
    {
        public OtherFixedVolumeFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows 専用: 別ボリューム";
                return;
            }

            string? current = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath());
            bool found = false;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady
                    && drive.DriveType == DriveType.Fixed
                    && !string.Equals(drive.RootDirectory.FullName, current, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Skip = "別の固定ボリュームが無い";
            }
        }
    }
}
