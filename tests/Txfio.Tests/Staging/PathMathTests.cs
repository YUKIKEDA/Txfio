namespace Txfio.Tests.Staging;

public sealed class PathMathTests
{
    /// <summary>
    /// "Under" excludes the directory itself, and paths that only share a name prefix.
    /// </summary>
    /// <remarks>
    /// <para>Given: a directory root/a.</para>
    /// <para>When: root/a, root/a/b, and root/ab are checked with IsUnder and IsEqualOrUnder.</para>
    /// <para>Then: root/a matches only IsEqualOrUnder, root/a/b matches both, and root/ab matches neither.</para>
    /// </remarks>
    [Fact]
    public void IsUnder_ExcludesItselfAndPrefixOnlyPaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "root", "a");
        string child = Path.Combine(directory, "b");
        string sibling = directory + "b";

        Assert.False(PathMath.IsUnder(directory, directory));
        Assert.True(PathMath.IsEqualOrUnder(directory, directory));
        Assert.True(PathMath.IsUnder(directory, child));
        Assert.True(PathMath.IsEqualOrUnder(directory, child));
        Assert.False(PathMath.IsUnder(directory, sibling));
        Assert.False(PathMath.IsEqualOrUnder(directory, sibling));
    }

    /// <summary>
    /// Moves the directory itself and paths under it to the same position under another directory.
    /// </summary>
    /// <remarks>
    /// <para>Given: two directories, from and to (from ends with a separator).</para>
    /// <para>When: from itself and from/x/y.txt are passed to Rebase.</para>
    /// <para>Then: to and to/x/y.txt.</para>
    /// </remarks>
    [Fact]
    public void Rebase_MovesItselfAndChildrenToDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "root");
        string from = Path.Combine(root, "from");
        string to = Path.Combine(root, "to");

        Assert.Equal(to, PathMath.Rebase(from + Path.DirectorySeparatorChar, to, from + Path.DirectorySeparatorChar));
        Assert.Equal(
            Path.Combine(to, "x", "y.txt"),
            PathMath.Rebase(from + Path.DirectorySeparatorChar, to, Path.Combine(from, "x", "y.txt")));
    }

    /// <summary>
    /// Sorts deepest first, so that children come before their parents.
    /// </summary>
    /// <remarks>
    /// <para>Given: four paths, a, a/b, a/b/c, and a/d (shallowest first).</para>
    /// <para>When: SortDeepestFirst is called.</para>
    /// <para>Then: the order is a/b/c, a/d, a/b, a.</para>
    /// </remarks>
    [Fact]
    public void SortDeepestFirst_PutsChildrenBeforeParents()
    {
        string a = Path.Combine(Path.GetTempPath(), "a");
        string ab = Path.Combine(a, "b");
        string abc = Path.Combine(ab, "c");
        string ad = Path.Combine(a, "d");
        List<string> paths = new List<string> { a, ab, abc, ad };

        PathMath.SortDeepestFirst(paths);

        Assert.Equal(new[] { abc, ad, ab, a }, paths);
    }
}
