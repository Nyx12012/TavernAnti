using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.Serialization;
using HarmonyLib;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Patches;

[HarmonyPatch(typeof(EntityMessageHandler), nameof(EntityMessageHandler.HandleMessage))]
public static class InteractionGuardPatch
{
    private static readonly ConcurrentDictionary<Connection, Queue<DateTime>> InteractTimestamps = new();

    public static void Init()
    {
        Logins.Disconnected += connection => InteractTimestamps.TryRemove(connection, out _);
    }

    [HarmonyPrefix]
    public static bool Prefix(EntityMessageType type, IPlayer player, Stream stream)
    {
        if (type != EntityMessageType.Interact && type != EntityMessageType.InteractEnd && type != EntityMessageType.GrabPositionSync) return true;
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
