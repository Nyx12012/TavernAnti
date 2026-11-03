using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.Networking.Servers;
using HarmonyLib;

namespace TavernAnti.Core;

/// <summary>
/// The login each connection joined with, captured from the join answer (same approach as
/// DevGuard). Never read player.UserInfo for identity: the player's own client can rewrite it
/// after join through SyncA.
/// </summary>
internal static class Logins
{
    private static readonly Dictionary<Connection, string> ByConnection = new();

    /// <summary>Raised once per connection when it drops; per-session state hangs off this.</summary>
    internal static event Action<Connection> Disconnected;

    internal static void Remember(Connection connection, string login)
    {
        if (connection == null || string.IsNullOrEmpty(login)) return;
        lock (ByConnection) ByConnection[connection] = login;
        Watch(connection);
    }

    /// <summary>Make sure this connection's state is dropped when it disconnects. Idempotent.</summary>
    internal static void Watch(Connection connection)
    {
        if (connection == null) return;
        connection.Disconnected -= Forget;
        connection.Disconnected += Forget;
    }

    private static void Forget(Connection connection)
    {
        lock (ByConnection) ByConnection.Remove(connection);
        Staff.Forget(connection);
        try { Disconnected?.Invoke(connection); }
        catch (Exception e) { TavernAntiLogger.Error($"Error while clearing state for a disconnect: {e}"); }
    }

    /// <summary>The connection's login, or null if we never saw its join.</summary>
    internal static string Of(Connection connection)
    {
        if (connection == null) return null;
        lock (ByConnection)
            if (ByConnection.TryGetValue(connection, out var login)) return login;
        // Set once by the game from the same join answer; still the login, never the avatar copy.
        return connection.UserInfo?.Username;
    }

    internal static string Of(IPlayer player) => Of(player?.ConnectionToRemotePlayer);

    internal static string NameOf(IPlayer player) => Of(player) ?? "(unknown login)";
}

/// <summary>
/// Staff = admin, moderator or owner in TavernLib's users.json, read through TavernLib's Roles
/// API (1.5.3+) by reflection, like DevGuard. Staff are exempt from every check.
/// </summary>
internal static class Staff
{
    internal static readonly string[] Roles = ["admin", "moderator", "owner"];
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<Connection, (string Role, DateTime Until)> Cache = new();
    private static MethodInfo _api;
    private static bool _looked;

    internal static bool Ready()
    {
        if (!_looked)
        {
            _looked = true;
            var t = AccessTools.TypeByName("TavernLib.ModdingApi.Roles");
            _api = t?.GetMethod("GetRoles", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null);
        }
        return _api != null;
    }

    /// <summary>The staff role this login holds, or null. Fails closed (null) on any error.</summary>
    internal static string RoleOf(string login)
    {
        if (string.IsNullOrEmpty(login) || !Ready()) return null;
        try
        {
            if (_api.Invoke(null, [login]) is not IEnumerable roles) return null;
            foreach (var r in roles)
            {
                var s = r?.ToString().Trim() ?? "";
                foreach (var staff in Roles)
                    if (string.Equals(s, staff, StringComparison.OrdinalIgnoreCase)) return staff;
            }
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"TavernLib role lookup failed for '{login}', treating as not staff: {e}");
        }
        return null;
    }

    internal static bool IsStaff(string login) => RoleOf(login) != null;

    /// <summary>Per-connection, cached for a few seconds: the movement monitor asks every frame.</summary>
    internal static bool IsStaff(IPlayer player)
    {
        var connection = player?.ConnectionToRemotePlayer;
        if (connection == null) return false;

        var now = DateTime.UtcNow;
        lock (Cache)
            if (Cache.TryGetValue(connection, out var hit) && hit.Until > now) return hit.Role != null;

        var role = RoleOf(Logins.Of(connection));
        lock (Cache) Cache[connection] = (role, now + CacheFor);
        return role != null;
    }

    internal static void Forget(Connection connection)
    {
        lock (Cache) Cache.Remove(connection);
    }
}

/// <summary>
/// Captures the login from each successful join answer. Only reads the answer, never changes it,
/// so it sits safely beside DevGuard / TavernEssentials postfixes on the same method.
/// </summary>
[HarmonyPatch(typeof(ServerPlayerConnectionHandlerOld), "CheckIfPlayerIsAllowedCustom")]
public static class JoinLoginPatch
{
    [HarmonyPostfix]
    public static void Postfix(Connection connection, object __result)
    {
        try
        {
            if (__result is not Task task) return;
            if (task.IsCompleted) Capture(connection, task);
            else task.ContinueWith(t => Capture(connection, t));
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Join login capture failed: {e}");
        }
    }

    private static void Capture(Connection connection, Task task)
    {
        try
        {
            if (task.IsFaulted || task.IsCanceled) return;
            var answer = task.GetType().GetProperty("Result")?.GetValue(task, null);
            if (answer == null) return;
            if (answer.GetType().GetProperty("IsSuccessful")?.GetValue(answer, null) is not true) return;
            if (answer.GetType().GetProperty("UserInfo")?.GetValue(answer, null) is not UserInfoAndRole user) return;

            Logins.Remember(connection, user.Username);
            var role = Staff.RoleOf(user.Username);
            if (role != null) TavernAntiLogger.Msg($"{user.Username} joined as {role}: exempt from every check");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Join login capture failed: {e}");
        }
    }
}
