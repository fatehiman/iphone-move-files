namespace IphoneMover;

/// <summary>Remembers the last destination folder in %AppData%\IphoneMover\last-folder.txt.</summary>
internal static class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IphoneMover", "last-folder.txt");

    private static string? lastFolder;
    private static bool loaded;

    public static string? LastFolder
    {
        get
        {
            if (!loaded)
            {
                loaded = true;
                try
                {
                    string s = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : "";
                    lastFolder = Directory.Exists(s) ? s : null;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return lastFolder;
        }
        set
        {
            loaded = true;
            lastFolder = value;
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, lastFolder ?? "");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
