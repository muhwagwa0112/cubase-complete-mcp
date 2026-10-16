namespace CubaseCompleteMcp.Tests;

/// <summary>
/// Locates the repository from the test output directory. CI builds set ContinuousIntegrationBuild, which maps source
/// paths to "/_/", so [CallerFilePath] cannot be used to find files next to the tests.
/// </summary>
public static class RepositoryPaths
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CubaseCompleteMcp.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException($"CubaseCompleteMcp.sln was not found above {AppContext.BaseDirectory}.");
    });

    public static string Root => RootPath.Value;

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);
}
