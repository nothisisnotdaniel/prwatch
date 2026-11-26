using System.Reflection;

namespace PRWatch;

/// <summary>PRWatch's values for RoomKit's core: names, port, release repository and version. PRWatch has no
/// factory module and no job format.</summary>
internal static class PrWatchApp
{
    /// <summary>The app's options. The version is this assembly's informational version (<c>dev</c> without
    /// <c>-p:Version</c>), never RoomKit's.</summary>
    public static RoomKitOptions Options { get; } = new()
    {
        AppName = "PRWatch",
        ShortName = "PRWatch",
        DataFolderName = "PRWatch",
        SolutionFileName = "PRWatch.slnx",
        DefaultPort = 8101,
        ReleaseRepository = "nothisisnotdaniel/prwatch",
        InformationalVersion = typeof(PrWatchApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    };

    /// <summary>Maps PRWatch's own endpoints; none yet (the watcher, runner and UI add theirs).</summary>
    public static void Map(RoomRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
    }
}
