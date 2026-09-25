using Txfio.Tests.Support;

namespace Txfio.Tests.Staging;

public sealed class StagingRulesTests
{
    /// <summary>
    /// 同一ルートならボリューム跨ぎとみなさない
    /// </summary>
    /// <remarks>
    /// <para>前提: 両パスのルートが同じである</para>
    /// <para>手順: EnsureSameVolume する</para>
    /// <para>期待: 例外にならない</para>
    /// </remarks>
    [WindowsFact("ドライブ文字のパス")]
    public void EnsureSameVolume_同じルートなら例外にならないこと()
    {
        StagingRules.EnsureSameVolume(@"C:\work\a.txt", @"C:\work\sub\b.txt");
    }

    /// <summary>
    /// ルートが違えばボリューム跨ぎとして失敗する
    /// </summary>
    /// <remarks>
    /// <para>前提: 両パスのルートが異なる</para>
    /// <para>手順: EnsureSameVolume する</para>
    /// <para>期待: UnsupportedOperationException になる</para>
    /// </remarks>
    [WindowsFact("ドライブ文字のパス")]
    public void EnsureSameVolume_ルートが違うとUnsupportedOperationExceptionになること()
    {
        Assert.Throws<UnsupportedOperationException>(() => StagingRules.EnsureSameVolume(@"C:\work\a.txt", @"D:\work\b.txt"));
    }
}
