namespace Txfio;

/// <summary>
/// The exception that shows a test stopped a commit partway.
/// </summary>
internal sealed class CrashInjectionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CrashInjectionException"/> class with the point where it stopped.
    /// </summary>
    /// <param name="name">The point where it stopped.</param>
    internal CrashInjectionException(string name)
        : base("Commit stopped partway: " + name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the point where it stopped.
    /// </summary>
    internal string Name { get; }
}
