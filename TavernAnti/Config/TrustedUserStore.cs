using System;
using System.Linq;
using TavernLib.Backend.Api;
using TavernLib.Backend.Server.Configs;
using TavernLib.Services;

namespace TavernAnti.Config;

/// <summary>
/// Trust checks against TavernLib's own live user store, not a separate TavernAnti-owned copy.
/// TavernLib.dll now has Roles (per-user) and UserIds (on blacklist) added to UserConfig - see
/// TavernLib\Backend\Server\Configs\UserConfigFile.cs - so both plugins share one schema, one
/// file, and (via TavernManager.UserConfig) one live in-memory instance: TavernAnti reads and
/// writes the exact same UserConfig object TavernLib's own AuthManager uses, rather than each
/// independently reading/writing the file and risking a lost update between them.
///
/// This makes TavernLib a hard runtime requirement for these features, not just a
/// same-file cooperation: if TavernManager isn't registered (TavernLib not installed, or not
/// running in server mode), both methods fail closed - IsOperator returns false, AppendBan logs
/// and does nothing.
/// </summary>
public class TrustedUserStore : IService
{
    // The role string in a user's "roles" array that grants TavernAnti's elevated trust
    // (running networked console commands, claiming a developer identity-token role).
    private const string TrustedRole = "owner";

    public void AppendBan(string username, string ip)
    {
        try
        {
            var userConfig = GetUserConfig();
            if (userConfig == null) return;

            var blacklist = userConfig.LastRead.Blacklist;

            if (!string.IsNullOrWhiteSpace(username) && !blacklist.Usernames.Any(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase)))
                blacklist.Usernames.Add(username);

            if (!string.IsNullOrWhiteSpace(ip) && !blacklist.Ips.Any(i => string.Equals(i, ip, StringComparison.OrdinalIgnoreCase)))
                blacklist.Ips.Add(ip);

            userConfig.WriteToFile();

            TavernAntiLogger.Warn($"Appended ban for username='{username}' ip='{ip}' to shared blacklist");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Failed to append ban for username='{username}' ip='{ip}': {e}");
        }
    }

    /// <summary>
    /// Permission gate for networked commands (CommandPermissionPatch) and claimed
    /// identity-token roles (IdentityTokenClaimGuardPatch/DeveloperClaimGuardPatch): a user is
    /// trusted if their UserConfig.User.Roles contains TrustedRole ("owner"). Reusing TavernLib's
    /// own trust store instead of a separate TavernAnti-owned allow-list means there's one place
    /// server owners manage who's trusted, not two. Fail-closed: any lookup failure (missing
    /// user, TavernLib not available) returns false.
    /// </summary>
    public bool IsOperator(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;

        try
        {
            var userConfig = GetUserConfig();
            if (userConfig == null) return false;

            // TavernLib keys the users map by lowercased username (see AuthManager.cs -
            // payload.Username.ToLower()) - match that convention for the lookup.
            return userConfig.LastRead.Users.TryGetValue(username.ToLowerInvariant(), out var user) && user.HasRole(TrustedRole);
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Failed to check trusted role for username='{username}': {e}");
            return false;
        }
    }

    private static UserConfigFile GetUserConfig()
    {
        var apiManager = TavernServices.GetService<TavernManager>();
        if (apiManager?.UserConfig != null) return apiManager.UserConfig;

        TavernAntiLogger.Error("TavernLib's TavernManager/UserConfig isn't available - is TavernLib installed and running in server mode?");
        return null;
    }
}
