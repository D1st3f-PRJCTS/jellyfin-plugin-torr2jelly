using System.Collections.Generic;

namespace Jellyfin.Plugin.Torr2Jelly.Configuration;

public class CategoryMapping
{
    public string Name { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Type { get; set; } = "movie"; // "movie" or "show"
    public List<int> ProwlarrCategories { get; set; } = new();
    public bool IsDefault { get; set; }

    public CategoryMapping() { }

    public CategoryMapping(string name, string folder, string type, List<int> prowlarrCategories, bool isDefault = false)
    {
        Name = name;
        Folder = folder;
        Type = type;
        ProwlarrCategories = prowlarrCategories;
        IsDefault = isDefault;
    }
}
