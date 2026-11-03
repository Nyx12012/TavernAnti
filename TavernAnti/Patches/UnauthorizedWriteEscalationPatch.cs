using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Patches;

[HarmonyPatch(typeof(StreamAuthorityHelper), nameof(StreamAuthorityHelper.LogUnauthorizedMessage))]
public static class UnauthorizedWriteEscalationPatch
{
    [HarmonyPostfix]
    public static void Postfix(IPlayer player)
    {
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;

        TavernServices.GetService<ViolationTracker>()
            ?.Report(player, ViolationType.UnauthorizedWrite, "StreamAuthorityHelper rejected an unauthorized network write");
    }
}
