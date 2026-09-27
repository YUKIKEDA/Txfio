using System.Globalization;

namespace Txfio.Tests.Stress;

/// <summary>
/// Reads the size of stress tests from environment variables.
/// </summary>
/// <remarks>
/// The defaults are small enough to finish quickly in an explicit run. Change them with environment variables only to run longer or larger.
/// </remarks>
internal static class StressSettings
{
    /// <summary>
    /// The seed for the random source.
    /// </summary>
    public const string SeedVariable = "TXFIO_STRESS_SEED";

    /// <summary>
    /// The number of random sequences, or the number of transactions per child process.
    /// </summary>
    public const string IterationsVariable = "TXFIO_STRESS_ITERATIONS";

    /// <summary>
    /// The number of child processes run at the same time.
    /// </summary>
    public const string ProcessesVariable = "TXFIO_STRESS_PROCESSES";

    /// <summary>
    /// The number of files child processes compete for.
    /// </summary>
    public const string FilesVariable = "TXFIO_STRESS_FILES";

    /// <summary>
    /// The length limit (bytes) of files random sequences write.
    /// </summary>
    public const string MaxBytesVariable = "TXFIO_STRESS_MAX_BYTES";

    /// <summary>
    /// The seed from the environment variable, or the default when it is not set.
    /// </summary>
    /// <param name="defaultValue">The value when the environment variable is not set.</param>
    /// <returns>The seed.</returns>
    public static int Seed(int defaultValue) => Read(SeedVariable, defaultValue);

    /// <summary>
    /// The count from the environment variable, or the default when it is not set.
    /// </summary>
    /// <param name="defaultValue">The value when the environment variable is not set.</param>
    /// <returns>The count.</returns>
    public static int Iterations(int defaultValue) => Read(IterationsVariable, defaultValue);

    /// <summary>
    /// The process count from the environment variable, or the default when it is not set.
    /// </summary>
    /// <param name="defaultValue">The value when the environment variable is not set.</param>
    /// <returns>The process count.</returns>
    public static int Processes(int defaultValue) => Read(ProcessesVariable, defaultValue);

    /// <summary>
    /// The file count from the environment variable, or the default when it is not set.
    /// </summary>
    /// <param name="defaultValue">The value when the environment variable is not set.</param>
    /// <returns>The file count.</returns>
    public static int Files(int defaultValue) => Read(FilesVariable, defaultValue);

    /// <summary>
    /// The length limit from the environment variable, or the default when it is not set.
    /// </summary>
    /// <param name="defaultValue">The value when the environment variable is not set.</param>
    /// <returns>The number of bytes.</returns>
    public static int MaxBytes(int defaultValue) => Read(MaxBytesVariable, defaultValue);

    private static int Read(string name, int defaultValue)
    {
        string? text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        return int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
