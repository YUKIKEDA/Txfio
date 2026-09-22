using Txfio.Tests.Support;

namespace Txfio.Tests;

public sealed class TempDirectoryTests
{
    /// <summary>
    /// 一時ディレクトリは作成でき、破棄すると消える
    /// </summary>
    /// <remarks>
    /// <para>前提: なし</para>
    /// <para>手順: Create したあと DisposeAsync する</para>
    /// <para>期待: 作成直後はディレクトリが存在し、破棄後は存在しない</para>
    /// </remarks>
    [Fact]
    public async Task Createすると一意のディレクトリができ破棄で消えること()
    {
        string path;
        await using (TempDirectory dir = TempDirectory.Create())
        {
            path = dir.Path;
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }
}
