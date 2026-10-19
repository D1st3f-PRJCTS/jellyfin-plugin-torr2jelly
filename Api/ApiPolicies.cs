namespace Jellyfin.Plugin.Torr2Jelly.Api;

/// <summary>
/// Authorization names of the Jellyfin host. The host's own <c>Policies</c> class lives in Jellyfin.Api, which a
/// plugin does not reference, so the (stable) literals are repeated here.
/// </summary>
internal static class ApiPolicies
{
    /// <summary>Policy that only passes for server administrators.</summary>
    public const string RequiresElevation = "RequiresElevation";

    /// <summary>Role claim value of a server administrator (<c>User.IsInRole</c>).</summary>
    public const string AdministratorRole = "Administrator";
}
