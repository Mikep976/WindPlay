using System.Text.Json;

namespace WindPlay.App.Configuration;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ReceiverSettings Load()
    {
        AppPaths.EnsureDataDirectory();
        try
        {
            var file = new FileInfo(AppPaths.SettingsFile);
            if (!file.Exists || file.Length is <= 0 or > 64 * 1024)
                return new ReceiverSettings();

            ReceiverSettings? settings = JsonSerializer.Deserialize<ReceiverSettings>(
                File.ReadAllText(file.FullName),
                JsonOptions);
            return Validate(settings ?? new ReceiverSettings());
        }
        catch (Exception) when (File.Exists(AppPaths.SettingsFile))
        {
            return new ReceiverSettings();
        }
    }

    public static ReceiverSettings Save(ReceiverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppPaths.EnsureDataDirectory();
        ReceiverSettings validated = Validate(settings);
        string temporaryFile = AppPaths.SettingsFile + ".new";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(validated, JsonOptions));
        File.Move(temporaryFile, AppPaths.SettingsFile, overwrite: true);
        return validated;
    }

    private static ReceiverSettings Validate(ReceiverSettings settings)
    {
        string name = settings.ReceiverName.Trim();
        if (name.Length is 0 or > 64 || name.Any(char.IsControl))
            name = "WindPlay";

        return settings with
        {
            Version = 1,
            ReceiverName = name,
            MaximumConnections = Math.Clamp(settings.MaximumConnections, 1, 16),
        };
    }
}
