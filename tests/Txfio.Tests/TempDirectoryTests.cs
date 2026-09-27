using Txfio.Tests.Support;

namespace Txfio.Tests;

public sealed class TempDirectoryTests
{
    /// <summary>
    /// A temporary directory can be created, and is deleted on dispose.
    /// </summary>
    /// <remarks>
    /// <para>Given: nothing.</para>
    /// <para>When: Create, then DisposeAsync.</para>
    /// <para>Then: the directory exists right after creation, and does not after disposal.</para>
    /// </remarks>
    [Fact]
    public async Task Create_MakesUniqueDirectoryThatDisposeDeletes()
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
