using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Torr2Jelly.Configuration;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

/// <summary>
/// Applies a (possibly partial) settings payload to the stored configuration. The settings page only sends the
/// fields it has controls for; everything it leaves out keeps its stored value instead of falling back to a default.
/// </summary>
internal static class SettingsMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true
    };

    // Properties the client may not change.
    private static readonly HashSet<string> ReadOnlyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PluginConfiguration.ConfigVersion)
    };

    private static readonly PropertyInfo[] StringProperties = typeof(PluginConfiguration)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite)
        .ToArray();

    /// <summary>
    /// Overlays the top-level properties of <paramref name="incoming"/> (matched case-insensitively, unknown ones
    /// ignored, <c>null</c> values ignored) onto a copy of <paramref name="current"/>, then normalises and validates
    /// the result. <paramref name="current"/> itself is never modified.
    /// </summary>
    public static bool TryMerge(PluginConfiguration current, JsonElement incoming, out PluginConfiguration merged, out string? error)
    {
        merged = current;
        error = null;

        if (incoming.ValueKind != JsonValueKind.Object)
        {
            error = "The settings payload must be a JSON object.";
            return false;
        }

        if (JsonSerializer.SerializeToNode(current, JsonOptions) is not JsonObject node)
        {
            error = "The stored settings could not be read.";
            return false;
        }

        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in node)
        {
            keys[pair.Key] = pair.Key;
        }

        foreach (var property in incoming.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null ||
                ReadOnlyProperties.Contains(property.Name) ||
                !keys.TryGetValue(property.Name, out var key))
            {
                continue;
            }

            node[key] = JsonNode.Parse(property.Value.GetRawText());
        }

        PluginConfiguration? result;
        try
        {
            result = node.Deserialize<PluginConfiguration>(JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid value for '{ex.Path}'.";
            return false;
        }

        if (result == null)
        {
            error = "The settings payload could not be read.";
            return false;
        }

        if (!Normalize(current, result, out error))
        {
            return false;
        }

        merged = result;
        return true;
    }

    private static bool Normalize(PluginConfiguration current, PluginConfiguration config, out string? error)
    {
        error = null;

        config.ConfigVersion = current.ConfigVersion;

        foreach (var property in StringProperties)
        {
            var value = (string?)property.GetValue(config);
            property.SetValue(config, value?.Trim() ?? string.Empty);
        }

        // A blank template would produce nameless files and folders.
        var defaults = new PluginConfiguration();
        if (config.MovieFolderTemplate.Length == 0) config.MovieFolderTemplate = defaults.MovieFolderTemplate;
        if (config.MovieFileTemplate.Length == 0) config.MovieFileTemplate = defaults.MovieFileTemplate;
        if (config.ShowFolderTemplate.Length == 0) config.ShowFolderTemplate = defaults.ShowFolderTemplate;
        if (config.ShowSeasonFolderTemplate.Length == 0) config.ShowSeasonFolderTemplate = defaults.ShowSeasonFolderTemplate;
        if (config.ShowEpisodeFileTemplate.Length == 0) config.ShowEpisodeFileTemplate = defaults.ShowEpisodeFileTemplate;

        var numbers = new (string Name, int Value, int Max)[]
        {
            (nameof(PluginConfiguration.CacheSizeMb), config.CacheSizeMb, int.MaxValue),
            (nameof(PluginConfiguration.PreloadCachePercent), config.PreloadCachePercent, 100),
            (nameof(PluginConfiguration.ReaderReadAHeadPercent), config.ReaderReadAHeadPercent, 100),
            (nameof(PluginConfiguration.TorrentDisconnectTimeout), config.TorrentDisconnectTimeout, int.MaxValue),
            (nameof(PluginConfiguration.AutoSyncIntervalMinutes), config.AutoSyncIntervalMinutes, int.MaxValue)
        };

        foreach (var (name, value, max) in numbers)
        {
            if (value < 0 || value > max)
            {
                error = max == int.MaxValue
                    ? $"{name} must not be negative."
                    : $"{name} must be between 0 and {max}.";
                return false;
            }
        }

        // Only a changed URL is validated: a value that was stored earlier must not block saving something else.
        var urls = new (string Name, string New, string Old)[]
        {
            (nameof(PluginConfiguration.ExternalTorrServerUrl), config.ExternalTorrServerUrl, current.ExternalTorrServerUrl),
            (nameof(PluginConfiguration.TorrServerStreamUrl), config.TorrServerStreamUrl, current.TorrServerStreamUrl),
            (nameof(PluginConfiguration.ProwlarrUrl), config.ProwlarrUrl, current.ProwlarrUrl),
            (nameof(PluginConfiguration.TorznabUrl), config.TorznabUrl, current.TorznabUrl)
        };

        foreach (var (name, newValue, oldValue) in urls)
        {
            if (newValue.Length > 0 &&
                !string.Equals(newValue, oldValue?.Trim(), StringComparison.Ordinal) &&
                !IsHttpUrl(newValue))
            {
                error = $"{name} must be an http:// or https:// URL.";
                return false;
            }
        }

        if (config.ForwardedPortFilePath.Length > 0 &&
            !string.Equals(config.ForwardedPortFilePath, current.ForwardedPortFilePath?.Trim(), StringComparison.Ordinal) &&
            !config.ForwardedPortFilePath
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(System.IO.Path.IsPathRooted))
        {
            error = $"{nameof(PluginConfiguration.ForwardedPortFilePath)} must be an absolute path (several paths may be separated by ';').";
            return false;
        }

        // Lists: a hand-written or older client payload may contain null entries.
        config.DisabledTrackers = (config.DisabledTrackers ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rules = new List<TrackerRule>();
        foreach (var rule in config.TrackerRules ?? new List<TrackerRule>())
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Tracker))
            {
                continue;
            }

            rule.Tracker = rule.Tracker.Trim();
            rule.AllowedTags = (rule.AllowedTags ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .ToList();
            rules.Add(rule);
        }

        config.TrackerRules = rules;

        var categories = new List<CategoryMapping>();
        foreach (var category in config.Categories ?? new List<CategoryMapping>())
        {
            if (category == null)
            {
                continue;
            }

            category.Name = (category.Name ?? string.Empty).Trim();
            category.Folder = (category.Folder ?? string.Empty).Trim();
            if (category.Name.Length == 0 && category.Folder.Length == 0)
            {
                continue;
            }

            if (category.Name.Length == 0 || category.Folder.Length == 0)
            {
                error = "Every category needs both a name and a folder.";
                return false;
            }

            category.Type = string.Equals(category.Type, "show", StringComparison.OrdinalIgnoreCase) ? "show" : "movie";
            category.ProwlarrCategories = (category.ProwlarrCategories ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            categories.Add(category);
        }

        config.Categories = categories;

        config.DeduplicateCategories();
        config.NormalizeTrackerRules();
        return true;
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               !string.IsNullOrEmpty(uri.Host);
    }
}
