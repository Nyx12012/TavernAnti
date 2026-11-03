using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Alta.Character;
using Alta.Global;
using Alta.Inventory;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using TavernAnti.Config;
using TavernLib.Services;
using UnityEngine;

namespace TavernAnti.Core;

internal static class GrabGuard
{
    private const float OriginSlack = 1f;
    private const int MaxWalk = 32;
    private static readonly FieldInfo AllowanceField = AccessTools.Field(typeof(Interactable), "multipleAllowance");
    private static bool _broken;
    private static bool _reachLogged;

    internal static bool Watching
    {
        get
        {
            if (_broken || !NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return false;
            var cfg = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
            return cfg != null && cfg.GrabChecks;
        }
    }

    internal static void Stop(Exception e)
    {
        _broken = true;
        TavernAntiLogger.Error($"GrabGuard stopped checking grabs and docks until the server restarts: a message could not be rewound. {e}");
    }

    internal static bool Check(IPlayer player, Controller hand, Component target, string action)
    {
        if (player?.ConnectionToRemotePlayer == null || target == null) return false;
        if (Staff.IsStaff(player)) return false;

        var cfg = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
        var tracker = TavernServices.GetService<ViolationTracker>();
        if (cfg == null || tracker == null) return false;
        LogVanillaReach(cfg);

        var what = Name(target);
        var other = OtherOwner(player, target, out var how);
        if (other != null)
            return tracker.Report(player, ViolationType.GrabOtherPlayer, $"{action} {what}, {how} {Logins.NameOf(other)}");

        var pc = hand?.PlayerController;
        if (pc == null || pc.Head == null) return false;
        var handAt = hand.transform.position;
        var head = pc.Head.position;
        var body = pc.transform.position;

        var jumped = MovementMonitor.RecentJump(player.ConnectionToRemotePlayer, pc.PlayerFeetPosition, cfg);
        if (jumped != null)
            return tracker.Report(player, ViolationType.GrabBodyReach, $"{action} {what} {jumped}");

        var headToBody = Vector3.Distance(head, body);
        if (headToBody > cfg.GrabMaxHeadToBody)
            return tracker.Report(player, ViolationType.GrabBodyReach, $"{action} {what} with the head {headToBody:F1} m from the body (max {cfg.GrabMaxHeadToBody:F1})");

        var handToHead = Vector3.Distance(handAt, head);
        if (handToHead > cfg.GrabMaxHandToHead)
            return tracker.Report(player, ViolationType.GrabArmReach, $"{action} {what} with the hand {handToHead:F1} m from the head (max {cfg.GrabMaxHandToHead:F1})");

        if (ItemDistance(target, handAt, out var reach, out var exact))
        {
            var max = cfg.GrabMaxHandToItem + (exact ? 0f : OriginSlack);
            if (reach > max)
                return tracker.Report(player, ViolationType.InteractRange, $"{action} {what} {reach:F1} m from the hand (max {max:F1}{(exact ? "" : ", measured to the item origin")})");
        }
        return false;
    }

    private static Player OtherOwner(IPlayer me, Component start, out string how)
    {
        how = null;
        var seen = new HashSet<Component>();
        var todo = new Stack<Component>();
        todo.Push(start);
        while (todo.Count > 0 && seen.Count < MaxWalk)
        {
            var c = todo.Pop();
            if (c == null || !seen.Add(c)) continue;

            if (c is PickupDock dock)
            {
                var wearer = Wearer(dock);
                if (wearer != null && !ReferenceEquals(wearer, me))
                {
                    how = "docked on the body of";
                    return wearer;
                }
                todo.Push(dock.DockedPickup);
                if (dock.transform.parent != null) todo.Push(dock.transform.parent.GetComponentInParent<Pickup>());
            }
            else if (c is Pickup p)
            {
                if (!SharedCarry(p))
                {
                    foreach (var i in p.Interactors)
                    {
                        var holder = i?.Controller?.PlayerController?.NetworkPlayer;
                        if (holder != null && !ReferenceEquals(holder, me))
                        {
                            how = "held by";
                            return holder;
                        }
                    }
                }
                todo.Push(p.Dock);
                if (p.transform.parent != null) todo.Push(p.transform.parent.GetComponentInParent<Pickup>());
                foreach (var joint in p.GetComponentsInChildren<Joint>(true))
                    if (joint != null && joint.connectedBody != null) todo.Push(joint.connectedBody.GetComponentInParent<Pickup>());
            }
            else if (c is PickupContainer container)
            {
                todo.Push(container.Pickup);
            }
            else
            {
                todo.Push(c.GetComponentInParent<Pickup>());
            }
        }
        return null;
    }

    private static bool SharedCarry(Pickup p)
    {
        if (AllowanceField == null) return false;
        var allowance = (MultipleInteractionAllowance)AllowanceField.GetValue(p);
        return allowance.ContainsAny(MultipleInteractionAllowance.MultiplePlayers);
    }

    private static Player Wearer(PickupDock dock)
    {
        var pc = dock.GetComponentInParent<PlayerController>();
        if (pc != null) return pc.NetworkPlayer;
        foreach (var ip in Player.AllPlayers)
        {
            if (ip?.PlayerController is not PlayerCharacter character) continue;
            if (Holds(character.BeltDocks, dock) || Holds(character.BackDocks, dock)) return ip as Player;
        }
        return null;
    }

    private static bool Holds(PickupDockGroup group, PickupDock dock)
    {
        if (group == null) return false;
        foreach (var d in group)
            if (ReferenceEquals(d, dock)) return true;
        return false;
    }

    private static bool ItemDistance(Component target, Vector3 hand, out float distance, out bool exact)
    {
        distance = 0f;
        exact = false;
        var pickup = target as Pickup ?? (target as PickupDock)?.DockedPickup ?? (target as PickupContainer)?.Pickup;
        Component anchor = pickup != null ? pickup : target as PickupDock;
        if (anchor == null) return false;

        var best = float.MaxValue;
        if (pickup != null)
        {
            foreach (var c in pickup.Colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                var point = c is MeshCollider { convex: false } ? c.bounds.ClosestPoint(hand) : c.ClosestPoint(hand);
                best = Math.Min(best, Vector3.Distance(hand, point));
            }
        }
        if (best < float.MaxValue)
        {
            distance = best;
            exact = true;
            return true;
        }
        distance = Vector3.Distance(hand, anchor.transform.position);
        return true;
    }

    private static void LogVanillaReach(AntiCheatConfig cfg)
    {
        if (_reachLogged) return;
        _reachLogged = true;
        try
        {
            var assist = GlobalSettings<GrabAssistSettings>.Instance;
            if (assist == null)
            {
                TavernAntiLogger.Msg($"Vanilla grab assist settings are not loaded on this server; grab_max_hand_to_item is {cfg.GrabMaxHandToItem:F1} m");
                return;
            }
            TavernAntiLogger.Msg($"Vanilla grab assist reaches {assist.AssistLength:F2} m plus a {assist.DetectionRadius:F2} m radius from the hand; grab_max_hand_to_item is {cfg.GrabMaxHandToItem:F1} m");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Warn($"Could not read the vanilla grab assist settings: {e.Message}");
        }
    }

    internal static string Name(Component c)
    {
        var entity = c?.GetComponentInParent<NetworkEntity>();
        var name = (entity != null ? entity.name : c?.name) ?? "something";
        var clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
        if (clone > 0) name = name.Substring(0, clone);
        name = name.Trim();
        return entity != null ? name + " #" + entity.Identifier.ToString(CultureInfo.InvariantCulture) : name;
    }
}
