using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;

namespace TavernAnti.Core;

internal static class Logins
{
    private static readonly Dictionary<Connection, string> ByConnection = new();

    internal static event Action<Connection> Disconnected;

    internal static void Remember(Connection connection, string login)
    {
        if (connection == null || string.IsNullOrEmpty(login)) return;
        lock (ByConnection) ByConnection[connection] = login;
        Watch(connection);
    }

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

    internal static string Of(Connection connection)
    {
        if (connection == null) return null;
        lock (ByConnection)
            if (ByConnection.TryGetValue(connection, out var login)) return login;
        return connection.UserInfo?.Username;
    }

    internal static string Of(IPlayer player) => Of(player?.ConnectionToRemotePlayer);

    internal static string NameOf(IPlayer player) => Of(player) ?? "(unknown login)";
}

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

    internal static bool IsStaff(string login) => !KeyGuard.Gating && RoleOf(login) != null;

    internal static bool IsStaff(IPlayer player) => RoleOf(player) != null;

    internal static string RoleOf(IPlayer player)
    {
        var connection = player?.ConnectionToRemotePlayer;
        if (connection == null) return null;
        var role = CachedRole(connection);
        if (role == null || KeyGuard.Vouches(connection)) return role;
        KeyGuard.Withheld(connection, role);
        return null;
    }

    private static string CachedRole(Connection connection)
    {
        var now = DateTime.UtcNow;
        lock (Cache)
            if (Cache.TryGetValue(connection, out var hit) && hit.Until > now) return hit.Role;

        var role = RoleOf(Logins.Of(connection));
        lock (Cache) Cache[connection] = (role, now + CacheFor);
        return role;
    }

    internal static void Forget(Connection connection)
    {
        lock (Cache) Cache.Remove(connection);
    }
}
