using System.Globalization;

namespace Txfio.Tests.Stress;

/// <summary>
/// The content of files that random sequences write. The length is chosen from bands, and the bytes come from the random source.
/// </summary>
internal static class StressContent
{
    /// <summary>
    /// The default length limit. The several-MB band goes up to here.
    /// </summary>
    public const int DefaultMaxBytes = 4 * 1024 * 1024;

    private const int FewBytesMax = 32;

    private const int TensOfKilobytesMin = 16 * 1024;

    private const int TensOfKilobytesMax = 64 * 1024;

    private const int FewMegabytesMin = 1024 * 1024;

    /// <summary>
    /// Chooses a length from the bands up to the limit, and makes bytes of that length.
    /// </summary>
    /// <param name="random">The random source that decides the length and content.</param>
    /// <param name="maxBytes">The length limit. 0 or more.</param>
    /// <returns>The content. Length 0 when empty.</returns>
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
    /// Text used by string sequences. With the default limit it is empty, a few characters, or up to tens of KB; several MB is used only when the limit is raised above the default.
    /// </summary>
    /// <param name="random">The random source that decides the length and content.</param>
    /// <param name="maxBytes">The length limit. 0 or more.</param>
    /// <returns>An ASCII string.</returns>
    public static string CreateText(Random random, int maxBytes)
    {
        int limit = maxBytes > DefaultMaxBytes ? maxBytes : Math.Min(maxBytes, TensOfKilobytesMax);
        int length = Length(random, limit);
        int salt = random.Next(26);
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + ((salt + i) % 26));
        }

        return new string(chars);
    }

    /// <summary>
    /// The length shown in failure messages.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The number of bytes.</returns>
    public static string Describe(byte[] content)
    {
        return content.Length.ToString(CultureInfo.InvariantCulture) + " bytes";
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
