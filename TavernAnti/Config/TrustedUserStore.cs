using System;
using System.Linq;
using TavernLib.Backend.Api;
using TavernLib.Backend.Server.Configs;
using TavernLib.Services;

namespace TavernAnti.Config;

/// <summary>
/// Writes bans into TavernLib's own live user store, not a separate TavernAnti-owned copy, so
/// both plugins share one schema, one file, and (via TavernManager.UserConfig) one live
/// in-memory instance: TavernAnti writes the exact same UserConfig object TavernLib's own
/// AuthManager uses, rather than each independently reading/writing the file and risking a lost
/// update between them. Who counts as staff is answered by TavernLib's Roles API (see
/// Core.Staff), not here.
///
/// If TavernManager isn't registered (TavernLib not installed, or not running in server mode),
/// AppendBan logs and does nothing.
/// </summary>
public class TrustedUserStore : IService
{
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

    private static UserConfigFile GetUserConfig()
    {
        var apiManager = TavernServices.GetService<TavernManager>();
        if (apiManager?.UserConfig != null) return apiManager.UserConfig;

        TavernAntiLogger.Error("TavernLib's TavernManager/UserConfig isn't available - is TavernLib installed and running in server mode?");
        return null;
    }
}
