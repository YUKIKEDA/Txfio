using System.Globalization;

namespace Txfio.Tests.Stress;

/// <summary>
/// ランダム操作列が書くファイルの中身。長さは帯から選び、バイトは乱数から決まる
/// </summary>
internal static class StressContent
{
    /// <summary>
    /// 既定の長さの上限。数 MB の帯はここまで
    /// </summary>
    public const int DefaultMaxBytes = 4 * 1024 * 1024;

    private const int FewBytesMax = 32;

    private const int TensOfKilobytesMin = 16 * 1024;

    private const int TensOfKilobytesMax = 64 * 1024;

    private const int FewMegabytesMin = 1024 * 1024;

    /// <summary>
    /// 上限までの帯から長さを選び、その長さのバイト列を作る
    /// </summary>
    /// <param name="random">長さと中身を決める乱数</param>
    /// <param name="maxBytes">長さの上限。0 以上</param>
    /// <returns>作った中身。空のときは長さ 0</returns>
    public static byte[] Create(Random random, int maxBytes)
    {
        int length = Length(random, maxBytes);
        int salt = random.Next();
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(salt + i);
        }

        return bytes;
    }

    /// <summary>
    /// 失敗メッセージに出す長さ
    /// </summary>
    /// <param name="content">中身</param>
    /// <returns>バイト数</returns>
    public static string Describe(byte[] content)
    {
        return content.Length.ToString(CultureInfo.InvariantCulture) + " バイト";
    }

    private static int Length(Random random, int maxBytes)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        List<int> bands = new List<int>();
        bands.Add(0);
        if (maxBytes >= 1)
        {
            bands.Add(1);
        }

        if (maxBytes >= TensOfKilobytesMin)
        {
            bands.Add(2);
        }

        if (maxBytes >= FewMegabytesMin)
        {
            bands.Add(3);
        }

        switch (bands[random.Next(bands.Count)])
        {
            case 0:
                return 0;
            case 1:
                return random.Next(1, Math.Min(FewBytesMax, maxBytes) + 1);
            case 2:
                return random.Next(TensOfKilobytesMin, Math.Min(TensOfKilobytesMax, maxBytes) + 1);
            default:
                int upper = maxBytes == int.MaxValue ? maxBytes : maxBytes + 1;
                return random.Next(FewMegabytesMin, upper);
        }
    }
}
