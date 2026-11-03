using System.Reflection;
using Alta.Networking;
using HarmonyLib;
using TavernAnti.Config;

namespace TavernAnti.Core;

internal static class EnforcementActions
{
    private static readonly FieldInfo IpAddressField = AccessTools.Field(typeof(Connection), "IpAddress");

    public static void Kick(Connection connection, string who, string reason, string last = null)
    {
        try
        {
            if (connection == null)
            {
                TavernAntiLogger.Warn($"Wanted to kick {who} but no connection was available");
                return;
            }

            TavernAntiLogger.Warn($"Kicking {who}: {reason}");
            DiscordWebhook.Post(last == null ? $"Kicked {who}: {reason}" : $"Kicked {who}: {reason}. Last: {last}");
            connection.Disconnect($"TavernAnti: {reason}");
        }
        catch (System.Exception e)
        {
            TavernAntiLogger.Error($"Error while kicking player: {e}");
        }
    }

    private static void Disconnect(Connection connection, string who, string reason)
    {
        if (connection == null) return;
        TavernAntiLogger.Warn($"Kicking {who}: {reason}");
        connection.Disconnect($"TavernAnti: {reason}");
    }

    public static void Ban(Connection connection, string login, string reason, TrustedUserStore userStore, bool joining, string last = null)
    {
        try
        {
            var ip = connection != null ? IpAddressField?.GetValue(connection) as string : null;
            var who = login ?? "(unknown login)";

            if (login == null) TavernAntiLogger.Warn($"Banning a player whose login is unknown: IP only ({ip})");
            TavernAntiLogger.Warn($"Banning {who} ({ip}): {reason}");

            DiscordWebhook.Post($"Banned {who}{(joining ? " at the door" : "")}: {reason}. Last: {last ?? "?"}");
            if (userStore != null) userStore.AppendBan(login, ip);
            else TavernAntiLogger.Error($"TrustedUserStore missing; {who} is kicked but NOT banned");

            if (!joining) Disconnect(connection, who, reason);
        }
        catch (System.Exception e)
        {
            TavernAntiLogger.Error($"Error while banning player: {e}");
        }
    }
}
