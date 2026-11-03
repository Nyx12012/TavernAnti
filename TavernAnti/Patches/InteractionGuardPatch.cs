using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.Serialization;
using HarmonyLib;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;
using UnityEngine;

namespace TavernAnti.Patches;

/// <summary>
/// Guards Interact/InteractEnd entity messages: rejects interactions from beyond plausible
/// hand-reach distance and rate-limits how many interactions a player can trigger per second.
/// Direct countermeasure for the item-vacuum / long-range raycast-grab / auto-steal exploits,
/// all of which drive the interaction pipeline from arbitrary distance or rate.
///
/// Unlike NetworkEntity.SerializeMove (see MovementPlausibilityPatch), each call here receives
/// a Stream already scoped to exactly one discrete network message - EntityMessageHandler is
/// the top-level per-message dispatch choke point, not a shared multi-field serialization pass -
/// so a Harmony prefix can safely short-circuit the call entirely without leaving the stream
/// cursor out of sync for anything else.
/// </summary>
[HarmonyPatch(typeof(EntityMessageHandler), nameof(EntityMessageHandler.HandleMessage))]
public static class InteractionGuardPatch
{
    // Keyed on the connection and dropped on disconnect, so player objects don't leak.
    private static readonly ConcurrentDictionary<Connection, Queue<DateTime>> InteractTimestamps = new();

    // EntityMessageHandler.entity is a private field, inaccessible via normal dot-syntax
    // against the raw (non-publicized) Root.Township.dll - read via reflection instead.
    private static readonly FieldInfo EntityField = AccessTools.Field(typeof(EntityMessageHandler), "entity");

    public static void Init()
    {
        Logins.Disconnected += connection => InteractTimestamps.TryRemove(connection, out _);
    }

    [HarmonyPrefix]
    public static bool Prefix(EntityMessageHandler __instance, EntityMessageType type, IPlayer player, Stream stream)
    {
        if (type != EntityMessageType.Interact && type != EntityMessageType.InteractEnd) return true;
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return true;
        if (player?.ConnectionToRemotePlayer == null) return true;
        if (Staff.IsStaff(player)) return true;

        var config = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
        var tracker = TavernServices.GetService<ViolationTracker>();
        if (config == null || tracker == null) return true;

        if (IsOverRate(player.ConnectionToRemotePlayer, config.MaxInteractsPerSecond))
        {
            return !tracker.Report(player, ViolationType.InteractRate, $"type={type} limit={config.MaxInteractsPerSecond}/s");
        }

        var entity = EntityField.GetValue(__instance) as INetworkEntity;
        var target = entity?.Transform;
        var playerTransform = player.Transform;
        if (target != null && playerTransform != null)
        {
            var distance = Vector3.Distance(playerTransform.position, target.position);
            if (distance > config.MaxInteractReach)
            {
                return !tracker.Report(player, ViolationType.InteractRange, $"type={type} distance={distance:F1} max={config.MaxInteractReach}");
            }
        }

        return true;
    }

    private static bool IsOverRate(Connection connection, int maxPerSecond)
    {
        var now = DateTime.UtcNow;
        var queue = InteractTimestamps.GetOrAdd(connection, c =>
        {
            Logins.Watch(c);
            return new Queue<DateTime>();
        });

        lock (queue)
        {
            queue.Enqueue(now);
            while (queue.Count > 0 && (now - queue.Peek()).TotalSeconds > 1.0) queue.Dequeue();

            return queue.Count > maxPerSecond;
        }
    }
}
