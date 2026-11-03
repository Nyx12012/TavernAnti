using System;
using Alta.Alchemy;
using Alta.Networking;
using HarmonyLib;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;
using UnityEngine;

namespace TavernAnti.Patches;

/// <summary>
/// Marks server-driven teleports so the movement monitor doesn't punish them. On the server,
/// PlayerEffectController.Teleport only sends a TeleportRequest to the player's client, which then
/// moves itself. Everything server-side funnels through here: PlayerController.ForceTeleportTo,
/// both TavernLib `player teleport` overloads (to a player, to a spawn area), `world forest
/// teleport`, population teleports, forest wipe, server-side potions, and our own pull-back.
///
/// Only the server's own call counts. The client also sends a TeleportRequest when it teleports
/// itself (for the effect), but the server's NetworkTeleport handler never calls Teleport for a
/// remote player, so a client cannot earn a grace window this way.
/// </summary>
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
