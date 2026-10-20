using System.Reflection;

namespace Scriblucent.Services;

public static class AppVersionInfo
{
    public static string DisplayVersion { get; } =
        (typeof(AppVersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppVersionInfo).Assembly.GetName().Version?.ToString()
            ?? "0.0.0").Split('+')[0];
}
