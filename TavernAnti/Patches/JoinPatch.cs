using System;
using System.Reflection;
using System.Threading.Tasks;
using Alta.Networking;
using HarmonyLib;
using TavernAnti.Core;
using TavernLib.Services;
using TavernAnti.Config;

namespace TavernAnti.Patches;

[HarmonyPatch]
public static class JoinPatch
{
    private static bool _logged;

    public static bool Prepare() => TargetMethod() != null;

    public static MethodBase TargetMethod()
    {
        var method = PatchTargets.Resolve("Alta.Networking.Servers.ServerPlayerConnectionHandlerOld", "CheckIfPlayerIsAllowedCustom", null,
            "ServerPlayerConnectionHandlerOld.CheckIfPlayerIsAllowedCustom", "join checks and login capture", ref _logged);
        JoinGate.Target = method;
        return method;
    }

    [HarmonyPostfix]
    public static void Postfix(Connection connection, int playerId, string tokenString, ref object __result)
    {
        if (__result is not Task task) return;
        try
        {
            if (task.IsCompleted)
            {
                var replaced = JoinGate.Decide(connection, playerId, tokenString, JoinAnswer.Of(task));
                var wrapped = JoinAnswer.Wrap(replaced);
                if (wrapped != null) __result = wrapped;
            }
            else
            {
                var next = JoinAnswer.After(task, answer => JoinGate.Decide(connection, playerId, tokenString, answer));
                if (next != null) __result = next;
            }
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Join check failed for id {playerId}: {e}");
            if (TavernServices.GetService<AntiCheatConfigFile>()?.LastRead?.Blocks != true) return;
            var denied = JoinAnswer.Wrap(JoinAnswer.Denied(JoinGate.Failed));
            if (denied != null) __result = denied;
        }
    }
}
