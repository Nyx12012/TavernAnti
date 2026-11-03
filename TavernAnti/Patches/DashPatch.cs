using System;
using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using TavernAnti.Core;
using NetStream = Alta.Serialization.Stream;

namespace TavernAnti.Patches;

[HarmonyPatch]
public static class DashPatch
{
    private static bool _logged;
    private static bool _failed;
    private static AccessTools.FieldRef<PlayerLocomotionController, bool> _usingTrail;

    public static bool Prepare() => TargetMethod() != null;

    public static MethodBase TargetMethod() => PatchTargets.Resolve(typeof(PlayerLocomotionController), "RemoteTrailMovement",
        [typeof(IPlayer), typeof(NetStream), typeof(bool)], "PlayerLocomotionController.RemoteTrailMovement(player, stream, bool)",
        "telling vanilla teleport dashes apart from speed", ref _logged);

    [HarmonyPostfix]
    public static void Postfix(PlayerLocomotionController __instance, IPlayer player, NetStream stream)
    {
        try
        {
            if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest || stream == null || !stream.IsReading) return;
            var pc = ((LocomotionController)__instance).PlayerController;
            if (pc == null || player == null || !ReferenceEquals(pc.NetworkPlayer, player)) return;
            _usingTrail ??= AccessTools.FieldRefAccess<PlayerLocomotionController, bool>("isUsingTrail");
            if (_usingTrail(__instance)) MovementMonitor.Dashed(player.ConnectionToRemotePlayer);
        }
        catch (Exception e)
        {
            if (_failed) return;
            _failed = true;
            TavernAntiLogger.Error($"Teleport dash tracking failed, fast dashes may show as speed: {e}");
        }
    }
}
