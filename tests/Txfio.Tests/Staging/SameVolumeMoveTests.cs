using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class SameVolumeMoveTests
{
    /// <summary>
    /// A file move within the same volume does not leave the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a file exists.</para>
    /// <para>When: MoveFile moves it within the same directory.</para>
    /// <para>Then: the source does not exist, and the destination has the content.</para>
    /// </remarks>
    [Fact]
    public async Task MoveFile_DoesNotLeaveSourceOnSameVolume()
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
    /// A directory move within the same volume does not leave the source.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with one file.</para>
    /// <para>When: MoveDirectory moves it under the same parent.</para>
    /// <para>Then: the source does not exist, and the destination has the file.</para>
    /// </remarks>
    [Fact]
    public async Task MoveDirectory_DoesNotLeaveSourceOnSameVolume()
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
    /// A file move to another volume does not copy.
    /// </summary>
    /// <remarks>
    /// <para>Given: a temporary directory, and an empty directory on another fixed volume.</para>
    /// <para>When: MoveFile moves to that volume.</para>
    /// <para>Then: IOException, the source remains, and the destination file does not exist.</para>
    /// </remarks>
    [OtherFixedVolumeFact]
    public async Task MoveFile_DoesNotCopyToAnotherVolume()
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
    /// A directory move to another volume does not copy.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory with one file, and an empty directory on another fixed volume.</para>
    /// <para>When: MoveDirectory moves to that volume.</para>
    /// <para>Then: IOException, the source file remains, and the destination directory does not exist.</para>
    /// </remarks>
    [OtherFixedVolumeFact]
    public async Task MoveDirectory_DoesNotCopyToAnotherVolume()
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
                Skip = "Windows only: another volume";
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
                Skip = "No other fixed volume";
            }
        }
    }
}
