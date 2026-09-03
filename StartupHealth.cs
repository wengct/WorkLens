using System.Reflection;

namespace WorkLens;

public sealed class StartupHealth
{
    public StartupHealth()
    {
        var assembly = typeof(StartupHealth).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        Version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informationalVersion.Split('+', 2)[0];
    }

    public string Version { get; }
    public bool IsHealthy { get; private set; }

    public void MarkHealthy() => IsHealthy = true;
    public void MarkUnhealthy() => IsHealthy = false;
}
