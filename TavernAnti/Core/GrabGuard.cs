using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Alta.Character;
using Alta.Global;
using Alta.Inventory;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using TavernAnti.Config;
using TavernLib.Services;
using UnityEngine;

namespace TavernAnti.Core;

internal static class GrabGuard
{
    private const float OriginSlack = 1f;
    private const int MaxWalk = 32;
    private const string StandaloneName = "GrabGuard";
    private static readonly FieldInfo AllowanceField = AccessTools.Field(typeof(Interactable), "multipleAllowance");
    private static readonly FieldInfo ContainerDockField = AccessTools.Field(typeof(PickupContainer), "dock");
    private static bool _reachLogged;

    internal static bool Standalone { get; private set; }

    internal static void Detect()
    {
        var where = FindStandalone();
        if (where == null) return;
        Standalone = true;
        TavernAntiLogger.Warn($"Standalone GrabGuard found ({where}): TavernAnti's built-in GrabGuard is off so nothing is patched twice. Remove GrabGuard.dll to use the built-in one.");
    }

    private static string FindStandalone()
    {
        try
        {
            if (MelonBase.RegisteredMelons.Any(m => string.Equals(m.Info?.Name, StandaloneName, StringComparison.OrdinalIgnoreCase)))
                return "loaded mod";
        }
        catch (Exception e)
        {
            TavernAntiLogger.Warn($"Could not list loaded mods while looking for GrabGuard: {e.Message}");
        }

        foreach (var dir in new[] { MelonEnvironment.ModsDirectory, MelonEnvironment.PluginsDirectory })
        {
            try
            {
                var path = System.IO.Path.Combine(dir, StandaloneName + ".dll");
                if (System.IO.File.Exists(path)) return path;
            }
            catch (Exception) { }
        }
        return null;
    }

    internal static bool Watching
    {
        get
        {
            if (Standalone || !NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return false;
            var cfg = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
            return cfg != null && cfg.GrabChecks;
        }
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

    internal static bool CheckDockRequest(IPlayer player, Pickup pickup, NetworkEntity named, NetworkEntity into, bool docking)
    {
        if (player?.ConnectionToRemotePlayer == null || pickup == null || named == null) return false;
        if (Staff.IsStaff(player)) return false;

        var cfg = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
        var tracker = TavernServices.GetService<ViolationTracker>();
        if (cfg == null || tracker == null) return false;

        var what = Name(pickup);
        var verb = docking ? "docking" : "undocking";
        var hand = named.GetComponent<Controller>();
        if (hand == null || hand.Interactor == null)
            return tracker.Report(player, ViolationType.GrabForged, $"{verb} {what} naming {Name(named)} as the hand");

        var pc = hand.PlayerController;
        if (pc == null || !ReferenceEquals(pc.NetworkPlayer, player)) return false;
        if (!pc.IsAlive)
            return tracker.Report(player, ViolationType.GrabWhileDead, $"{verb} {what} while dead");

        var action = verb;
        if (docking && into != null) action += " into " + Name(into);
        if (Check(player, hand, pickup, action)) return true;
        return docking && into != null && DockTooFar(player, hand, into.GetComponent<PickupDock>(), what, cfg, tracker);
    }

    internal static bool CheckMove(IPlayer player, PickupContainer source, PickupDock into)
    {
        if (player?.ConnectionToRemotePlayer == null || source == null) return false;
        if (Staff.IsStaff(player)) return false;

        var cfg = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
        var tracker = TavernServices.GetService<ViolationTracker>();
        if (cfg == null || tracker == null) return false;

        var what = Name(source);
        var pc = player.PlayerController;
        if (pc == null)
            return tracker.Report(player, ViolationType.GrabForged, $"moving a stack out of {what} with no body");
        if (!pc.IsAlive)
            return tracker.Report(player, ViolationType.GrabWhileDead, $"moving a stack out of {what} while dead");

        var hand = NearestHand(pc, source.transform.position);
        if (hand == null) return false;
        if (Check(player, hand, source, "moving a stack out of")) return true;
        return DockTooFar(player, hand, into, what, cfg, tracker);
    }

    private static bool DockTooFar(IPlayer player, Controller hand, PickupDock into, string what, AntiCheatConfig cfg, ViolationTracker tracker)
    {
        if (into == null) return false;
        var reach = Vector3.Distance(hand.transform.position, into.transform.position);
        if (reach <= cfg.GrabMaxHandToDock) return false;
        return tracker.Report(player, ViolationType.InteractRange, $"putting {what} into {Name(into)}, {reach:F1} m from the hand (max {cfg.GrabMaxHandToDock:F1})");
    }

    private static Controller NearestHand(PlayerController pc, Vector3 at)
    {
        var left = pc.LeftController;
        var right = pc.RightController;
        if (left == null) return right;
        if (right == null) return left;
        return Vector3.Distance(left.transform.position, at) <= Vector3.Distance(right.transform.position, at) ? left : right;
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
                todo.Push(ContainerDockField?.GetValue(container) as PickupDock);
                todo.Push(container.GetComponentInParent<Pickup>());
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
        Component anchor = pickup != null ? pickup : target is PickupDock || target is PickupContainer ? target : null;
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
