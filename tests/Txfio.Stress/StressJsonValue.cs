namespace Txfio.Tests.Stress;

/// <summary>
/// JSON の列が往復する小さな値
/// </summary>
/// <param name="Id">数値</param>
/// <param name="Name">短い文字列</param>
/// <param name="Flag">真偽</param>
internal sealed record StressJsonValue(int Id, string Name, bool Flag);
