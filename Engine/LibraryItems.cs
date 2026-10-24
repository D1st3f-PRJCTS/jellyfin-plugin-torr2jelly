using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

internal static class LibraryItems
{
    // Jellyfin changed GetItemList's return type from List to IReadOnlyList.
    // Bind the loaded interface by parameter types, so the plugin does not emit
    // a call that requires the old return type to exist at runtime.
    private static readonly MethodInfo? GetItems = typeof(ILibraryManager).GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) });

    internal static IEnumerable<BaseItem> Query(ILibraryManager library, InternalItemsQuery query)
    {
        var method = GetItems ?? throw new MissingMethodException("Jellyfin library item query is unavailable.");
        return method.Invoke(library, new object[] { query }) as IEnumerable<BaseItem> ?? Enumerable.Empty<BaseItem>();
    }
}
