namespace SpawnDev.WebTorrent.Demo;

/// <summary>
/// Build timestamp printed to browser console on app startup.
/// Reads the BuildTimestamp metadata injected by MSBuild at compile time.
/// </summary>
internal static class BuildTimestamp
{
    public static readonly string Value = GetBuildTimestamp();

    private static string GetBuildTimestamp()
    {
        try
        {
            var assembly = typeof(BuildTimestamp).Assembly;
            var metadata = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>();
            foreach (var meta in metadata)
            {
                if (meta.Key == "BuildTimestamp")
                    return meta.Value ?? "unknown";
            }
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }
}
