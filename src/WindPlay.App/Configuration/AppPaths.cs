namespace WindPlay.App.Configuration;

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindPlay");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string IdentityFile => Path.Combine(DataDirectory, "identity.dat");

    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
