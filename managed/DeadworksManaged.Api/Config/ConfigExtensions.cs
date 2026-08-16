namespace DeadworksManaged.Api;

public static class ConfigExtensions
{
    public static bool ReloadConfig(this IDeadworksPlugin plugin)
    {
        if (ConfigResolver.ReloadConfig == null)
            throw new InvalidOperationException("Config system not initialized.");
        return ConfigResolver.ReloadConfig(plugin);
    }

    public static string? GetConfigPath(this IDeadworksPlugin plugin)
    {
        if (ConfigResolver.GetConfigPath == null)
            throw new InvalidOperationException("Config system not initialized.");
        return ConfigResolver.GetConfigPath(plugin);
    }

    /// <summary>
    /// The plugin's writable data folder — a folder named after the plugin
    /// next to its DLL (<c>managed\plugins\&lt;Name&gt;\</c>) — created on
    /// demand. Put files the plugin reads or writes at runtime here — UI
    /// folders, saved state. Plugins load from memory, so
    /// <c>Assembly.Location</c> is empty and probing for a folder by hand is
    /// always wrong; the host owns this answer.
    /// </summary>
    public static string GetDataDirectory(this IDeadworksPlugin plugin)
    {
        if (ConfigResolver.GetDataDirectory == null)
            throw new InvalidOperationException("Config system not initialized.");
        string dir = ConfigResolver.GetDataDirectory(plugin);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
