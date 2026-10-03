using System.Text.RegularExpressions;
using IphoneMover.Wpd;

namespace IphoneMover.Core;

/// <summary>
/// iOS stores one photo "asset" as several files: IMG_1234.HEIC + IMG_1234.MOV (Live Photo),
/// IMG_E1234.HEIC (edited), IMG_1234.AAE (edit data). Deleting one file can make iOS remove
/// the whole asset, so these files are always moved together.
/// </summary>
internal static partial class AssetGrouping
{
    [GeneratedRegex(@"^([A-Za-z]+)_[EO]?(\d{3,})$")]
    private static partial Regex AppleName();

    public static string GroupKey(string folder, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        var m = AppleName().Match(stem);
        if (m.Success)
            stem = m.Groups[1].Value + "_" + m.Groups[2].Value;
        return (folder + "|" + stem).ToUpperInvariant();
    }

    /// <summary>
    /// Returns the selected files grouped by asset, with all related files from
    /// <paramref name="allFiles"/> added to each group.
    /// </summary>
    public static List<List<DeviceFile>> Expand(IEnumerable<DeviceFile> selected, IEnumerable<DeviceFile> allFiles)
    {
        var byKey = allFiles
            .GroupBy(f => GroupKey(f.Folder, f.Name))
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var seen = new HashSet<string>();
        var result = new List<List<DeviceFile>>();
        foreach (var f in selected)
        {
            string key = GroupKey(f.Folder, f.Name);
            if (!seen.Add(key))
                continue;
            result.Add(byKey.TryGetValue(key, out var group) ? group : [f]);
        }
        return result;
    }
}
