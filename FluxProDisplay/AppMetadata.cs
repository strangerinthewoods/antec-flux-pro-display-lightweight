using System.Reflection;

namespace FluxProDisplay;

internal static class AppMetadata
{
    public const string Name = "Antec Flux Pro Display Service";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "Dev";

        // strip SourceRevisionId metadata, e.g. "2.1.1+abc1234"
        var plus = informational.IndexOf('+');
        var cleaned = plus >= 0 ? informational[..plus] : informational;

        // default version when not overridden at build time
        return cleaned == "1.0.0" ? "Dev" : cleaned;
    }
}
