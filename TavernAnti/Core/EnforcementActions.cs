using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using TavernAnti.Config;

namespace TavernAnti.Core;

internal static class EnforcementActions
{
    // Connection.IpAddress is a private field, inaccessible via normal dot-syntax against
    // the raw (non-publicized) Root.Township.dll - read via reflection instead.
    private static readonly FieldInfo IpAddressField = AccessTools.Field(typeof(Connection), "IpAddress");

    public static void Kick(IPlayer player, string who, string reason)
    {
        try
        {
            var connection = player?.ConnectionToRemotePlayer;
            if (connection == null)
            {
                TavernAntiLogger.Warn($"Wanted to kick {who} but no connection was available");
                return;
            }

            TavernAntiLogger.Warn($"Kicking {who}: {reason}");
            connection.Disconnect($"TavernAnti: {reason}");
        }
        catch (System.Exception e)
        {
            TavernAntiLogger.Error($"Error while kicking player: {e}");
        }
    }

    /// <summary>Bans the login (never the client-rewritable player.UserInfo name) and the IP.</summary>
    public static void Ban(IPlayer player, string login, string reason, TrustedUserStore userStore)
    {
        try
        {
            var connection = player?.ConnectionToRemotePlayer;
            var ip = connection != null ? IpAddressField?.GetValue(connection) as string : null;
            var who = login ?? "(unknown login)";

            if (login == null) TavernAntiLogger.Warn($"Banning a player whose login is unknown: IP only ({ip})");
            TavernAntiLogger.Warn($"Banning {who} ({ip}): {reason}");

            if (userStore != null) userStore.AppendBan(login, ip);
            else TavernAntiLogger.Error($"TrustedUserStore missing; {who} is kicked but NOT banned");

            Kick(player, who, reason);
        }
        catch (System.Exception e)
        {
            TavernAntiLogger.Error($"Error while banning player: {e}");
        }
    }
}
