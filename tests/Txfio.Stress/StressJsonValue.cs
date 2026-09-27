namespace Txfio.Tests.Stress;

/// <summary>
/// A small value that JSON sequences round-trip.
/// </summary>
/// <param name="Id">A number.</param>
/// <param name="Name">A short string.</param>
/// <param name="Flag">A Boolean.</param>
internal sealed record StressJsonValue(int Id, string Name, bool Flag);
