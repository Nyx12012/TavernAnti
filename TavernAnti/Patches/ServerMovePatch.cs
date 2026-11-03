using System;
using Alta.Alchemy;
using Alta.Networking;
using HarmonyLib;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;
using UnityEngine;

namespace TavernAnti.Patches;

[HarmonyPatch(typeof(PlayerEffectController), nameof(PlayerEffectController.Teleport))]
public static class ServerMovePatch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerEffectController __instance, Vector3 position)
    {
        try
        {
            if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;
            var character = __instance.Player;
            if (character == null || character.IsLocalPlayer) return;

            var player = character.NetworkPlayer;
            var config = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
            if (player == null || config == null) return;

            MovementMonitor.ServerMoved(player.ConnectionToRemotePlayer, position, config.ServerMoveGraceSeconds);
            TavernAntiLogger.Msg($"Server moved {Logins.NameOf(player)} to {position}: grace {config.ServerMoveGraceSeconds}s");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Server move tracking failed: {e}");
        }
    }
}
