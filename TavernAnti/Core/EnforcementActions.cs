using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using TavernAnti.Config;

namespace TavernAnti.Core;

internal static class EnforcementActions
{
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
