using System.Text.Json;

namespace IphoneMover;

/// <summary>Remembers the last choices in %AppData%\IphoneMover\settings.json.</summary>
internal static class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IphoneMover", "settings.json");

    internal sealed class Data
    {
        public string? PcFolder { get; set; }       // destination folder on the PC
        public string? DeviceId { get; set; }       // WPD device id of the phone
        public string? DeviceName { get; set; }     // fallback when the id changed (other USB port)
        public bool FolderView { get; set; } = true; // left side: folders (true) or files (false)
        public string? PhoneFolder { get; set; }    // folder filter in file view; null = all folders
    }

    public static Data Current { get; } = Load();

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();

            // Version 1.0 stored only the destination folder in last-folder.txt.
            string old = Path.Combine(Path.GetDirectoryName(FilePath)!, "last-folder.txt");
            if (File.Exists(old))
                return new Data { PcFolder = File.ReadAllText(old).Trim() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new Data();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
