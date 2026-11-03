using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.Networking.Servers;
using HarmonyLib;
using TavernAnti.Config;
using TavernLib.Services;

namespace TavernAnti.Core;

internal static class JoinAnswer
{
    private static Type _type;
    private static MethodInfo _deny, _success, _fromResult, _then;
    private static bool _looked;

    internal static bool Ready()
    {
        if (_looked) return _fromResult != null;
        _looked = true;
        _type = typeof(ServerPlayerConnectionHandlerOld).GetNestedType("PlayerJoinResult", BindingFlags.NonPublic | BindingFlags.Public);
        _deny = _type?.GetMethod("CreateDeniedResult", BindingFlags.Public | BindingFlags.Static);
        _success = _type?.GetMethod("CreateSuccessResult", BindingFlags.Public | BindingFlags.Static);
        var from = typeof(Task).GetMethod("FromResult");
        var then = typeof(JoinAnswer).GetMethod(nameof(Then), BindingFlags.NonPublic | BindingFlags.Static);
        if (_type == null || _deny == null || _success == null || from == null || then == null)
        {
            TavernAntiLogger.Error("This game build has no PlayerJoinResult.CreateDeniedResult/CreateSuccessResult: joins cannot be refused or pinned");
            return false;
        }
        _fromResult = from.MakeGenericMethod(_type);
        _then = then.MakeGenericMethod(_type);
        return true;
    }

    internal static object Of(Task task)
    {
        if (task == null || !task.IsCompleted || task.IsFaulted || task.IsCanceled) return null;
        return task.GetType().GetProperty("Result")?.GetValue(task, null);
    }

    internal static bool Succeeded(object answer) => answer?.GetType().GetProperty("IsSuccessful")?.GetValue(answer, null) is true;

    internal static UserInfoAndRole User(object answer) => answer?.GetType().GetProperty("UserInfo")?.GetValue(answer, null) as UserInfoAndRole;

    internal static object Success(UserInfoAndRole user) => Ready() ? _success.Invoke(null, [user]) : null;

    internal static object Denied(string why) => Ready() ? _deny.Invoke(null, [why]) : null;

    internal static object Wrap(object answer) => answer != null && Ready() ? _fromResult.Invoke(null, [answer]) : null;

    internal static object After(Task inner, Func<object, object> map) => Ready() ? _then.Invoke(null, [inner, map]) : null;

    private static Task<T> Then<T>(Task<T> inner, Func<object, object> map)
    {
        return inner.ContinueWith(t =>
        {
            var answer = (object)t.Result;
            return (T)(map(answer) ?? answer);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }
}

internal static class JoinGate
{
    internal const string ForgedId = "Your game asked to join as another player. Update your mods, or join from the launcher.";
    internal const string Unreadable = "This server could not read your login token. Join from the launcher.";
    internal const string RoleClaim = "This server does not accept developer tokens.";
    internal const string Failed = "This server could not check your join. Try again.";

    private const string EssentialsJoinGuard = "tavernessentials.joinguard";

    internal static MethodBase Target;
    private static int _deferLogged;
    private static int _firstLogged;

    internal static object Decide(Connection connection, int playerId, string tokenString, object answer)
    {
        if (answer == null || !JoinAnswer.Succeeded(answer)) return null;
        var user = JoinAnswer.User(answer);
        if (user == null) return null;
        var login = user.Username;

        var config = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
        var tracker = TavernServices.GetService<ViolationTracker>();
        if (config == null || tracker == null)
        {
            Logins.Remember(connection, login);
            return null;
        }

        try
        {
            var verdict = Check(connection, playerId, tokenString, user, config, tracker, out var replaced);
            if (Interlocked.Exchange(ref _firstLogged, 1) == 0)
                TavernAntiLogger.Msg($"Checked the first join ({login}, id {playerId}): {verdict}");
            return replaced;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Join check for {login} (id {playerId}) failed: {e}");
            if (!config.Blocks)
            {
                Logins.Remember(connection, login);
                return null;
            }
            var denied = JoinAnswer.Denied(Failed);
            if (denied == null) Logins.Remember(connection, login);
            else TavernAntiLogger.Warn($"Refused {login}: the join could not be checked");
            return denied;
        }
    }

    private static string Check(Connection connection, int playerId, string tokenString, UserInfoAndRole user, AntiCheatConfig config, ViolationTracker tracker, out object replaced)
    {
        replaced = null;
        var login = user.Username;
        var token = JoinToken.Read(tokenString);
        string refusal = null;

        if (config.JoinGuard && !EssentialsGuarding())
        {
            if (token.Problem != null)
            {
                if (tracker.ReportJoin(connection, login, ViolationType.BadJoinToken, $"join as id {playerId} with a token {token.Problem}"))
                    refusal = Unreadable;
            }
            else if (token.UserId != playerId)
            {
                if (tracker.ReportJoin(connection, login, ViolationType.ForgedJoinId, $"token id {token.UserId} (\"{token.Username}\") asked to join as id {playerId}"))
                    refusal = ForgedId;
            }
        }

        var devGuard = DevGuard.Active(config);
        var role = Staff.RoleOf(login);
        if (refusal == null && devGuard && token.Roles.Count > 0 && role == null)
        {
            if (tracker.ReportJoin(connection, login, ViolationType.ForgedRoleClaim, $"login token claims {string.Join("/", token.Roles.ToArray())} and they are not staff"))
                refusal = RoleClaim;
        }

        if (refusal != null)
        {
            replaced = JoinAnswer.Denied(refusal);
            if (replaced != null)
            {
                TavernAntiLogger.Warn($"Refused {login} (id {playerId}): {refusal}");
                return "refused";
            }
            TavernAntiLogger.Error($"Could not build a refusal, so {login} gets in");
        }

        Logins.Remember(connection, login);
        if (role != null) TavernAntiLogger.Msg($"{login} joined as {role}: exempt from every check");

        if (devGuard && user.MemberStatus != PlayerSubscriberStatus.None)
        {
            replaced = JoinAnswer.Success(new UserInfoAndRole(user.UserInfo, PlayerSubscriberStatus.None));
            if (replaced == null) TavernAntiLogger.Error($"Could not rebuild the join answer; {login} keeps shield {user.MemberStatus}");
            return $"let in, shield {user.MemberStatus} pinned to None";
        }
        return token.Problem == null ? "let in, token id matches" : $"let in, token {token.Problem}";
    }

    private static bool EssentialsGuarding()
    {
        if (Target == null) return false;
        var info = HarmonyLib.Harmony.GetPatchInfo(Target);
        var guarding = info != null && info.Postfixes.Any(p => p.owner == EssentialsJoinGuard);
        if (guarding && Interlocked.Exchange(ref _deferLogged, 1) == 0)
            TavernAntiLogger.Msg("TavernEssentials JoinGuard is checking join ids, so TavernAnti's JoinGuard stands down (set joinguard_mode in TavernEssentials' config)");
        return guarding;
    }
}
