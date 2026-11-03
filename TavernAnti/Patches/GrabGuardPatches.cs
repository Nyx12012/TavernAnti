using System;
using System.Reflection;
using Alta.Inventory;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using TavernAnti.Core;
using UnityEngine;
using NetStream = Alta.Serialization.Stream;

namespace TavernAnti.Patches;

internal static class GrabStream
{
    internal static bool Decide(NetStream stream, uint start, Func<bool> check, Action refuse)
    {
        var used = stream.BitIndex - start;
        try
        {
            stream.BackTrack(used);
        }
        catch (Exception e)
        {
            GrabGuard.Stop(e);
            return false;
        }

        bool block;
        try
        {
            block = check();
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"A grab check failed: {e}");
            return true;
        }
        if (!block) return true;

        stream.SkipBits(used);
        try
        {
            refuse();
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Could not send a refusal back: {e}");
        }
        return false;
    }

    internal static bool Unreadable(NetStream stream, uint start, Exception reason)
    {
        TavernAntiLogger.Warn($"Could not read a grab or dock message, letting it through: {reason.Message}");
        try
        {
            var now = stream.BitIndex;
            if (now > start) stream.BackTrack(now - start);
            return true;
        }
        catch (Exception e)
        {
            GrabGuard.Stop(e);
            return false;
        }
    }

    internal static MethodInfo Resolve(Type type, string name, Type[] args, string label, ref bool logged)
    {
        var method = type == null ? null : AccessTools.Method(type, name, args);
        if (method == null && !logged)
        {
            logged = true;
            TavernAntiLogger.Error($"GrabGuard: this game build has no {label}, so that path is not checked");
        }
        return method;
    }
}

internal static class GrabRefusal
{
    private static readonly FieldInfo EndField = AccessTools.Field(typeof(Interactor), "syncEnd");
    private static readonly FieldInfo BuzzField = AccessTools.Field(typeof(Interactor), "syncConfirmationRequest");

    internal static void Drop(Interactor interactor, IPlayer player, NetworkEntity target)
    {
        if (interactor?.Entity == null || target == null) return;
        if (EndField?.GetValue(interactor) is MethodSyncStruct<InteractArguments> end)
        {
            end.SendToPlayer(player, new InteractArguments(interactor.Entity, target, Vector3.zero, Vector3.zero, true));
            var docked = (target.CommonInteractable as PickupDock)?.DockedPickup?.Entity;
            if (docked != null) end.SendToPlayer(player, new InteractArguments(interactor.Entity, docked, Vector3.zero, Vector3.zero, true));
        }
        if (BuzzField?.GetValue(interactor) is MethodSync buzz) buzz.SendToPlayer(player);
    }

    internal static Component Target(Interactor interactor, uint id, out NetworkEntity entity)
    {
        entity = interactor?.Entity?.Scene?.GetEntity(id);
        return entity != null ? entity.CommonInteractable : null;
    }

    internal static bool IsSender(Controller hand, IPlayer player)
    {
        var pc = hand?.PlayerController;
        return pc != null && pc.IsAlive && ReferenceEquals(pc.NetworkPlayer, player);
    }
}

[HarmonyPatch]
public static class GrabStartPatch
{
    private static bool _logged;

    public static bool Prepare() => TargetMethod() != null;

    public static MethodBase TargetMethod() => GrabStream.Resolve(typeof(Interactor), "SyncStart",
        [typeof(IPlayer), typeof(NetStream), typeof(InteractArguments)], "Interactor.SyncStart(player, stream, args)", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(Interactor __instance, IPlayer player, NetStream stream, InteractArguments args)
    {
        if (stream == null || stream.IsWriting || !GrabGuard.Watching) return true;
        var start = stream.BitIndex;
        var peek = args;
        try
        {
            peek.Serialize(player, stream);
        }
        catch (Exception e)
        {
            return GrabStream.Unreadable(stream, start, e);
        }
        NetworkEntity entity = null;
        return GrabStream.Decide(stream, start,
            () =>
            {
                var hand = __instance.Controller;
                if (!GrabRefusal.IsSender(hand, player)) return false;
                var target = GrabRefusal.Target(__instance, peek.Target, out entity);
                return target != null && GrabGuard.Check(player, hand, target, "grabbing");
            },
            () => GrabRefusal.Drop(__instance, player, entity));
    }
}

[HarmonyPatch]
public static class GrabPointPatch
{
    private static bool _logged;

    public static bool Prepare() => TargetMethod() != null;

    public static MethodBase TargetMethod() => GrabStream.Resolve(typeof(Interactor), "SyncStartGrabPoint",
        [typeof(IPlayer), typeof(NetStream), typeof(GrabPointArguments)], "Interactor.SyncStartGrabPoint(player, stream, args)", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(Interactor __instance, IPlayer player, NetStream stream, GrabPointArguments args)
    {
        if (stream == null || stream.IsWriting || !GrabGuard.Watching) return true;
        var start = stream.BitIndex;
        var peek = args;
        try
        {
            peek.Serialize(stream);
        }
        catch (Exception e)
        {
            return GrabStream.Unreadable(stream, start, e);
        }
        NetworkEntity entity = null;
        return GrabStream.Decide(stream, start,
            () =>
            {
                var hand = __instance.Controller;
                if (!GrabRefusal.IsSender(hand, player)) return false;
                var target = GrabRefusal.Target(__instance, peek.Target, out entity);
                return target != null && GrabGuard.Check(player, hand, target, "grabbing");
            },
            () => GrabRefusal.Drop(__instance, player, entity));
    }
}

[HarmonyPatch]
public static class DockRequestPatch
{
    private static readonly FieldInfo PickupField = AccessTools.Field(typeof(PickupDocker), "pickup");
    private static readonly FieldInfo RequestField = AccessTools.Field(typeof(PickupDocker), "requestDock");
    private static bool _logged;

    public static bool Prepare() => TargetMethod() != null && PickupField != null && RequestField != null;

    public static MethodBase TargetMethod() => GrabStream.Resolve(typeof(PickupDocker), "RequestDock",
        [typeof(IPlayer), typeof(NetStream), typeof(RequestDockArguments)], "PickupDocker.RequestDock(player, stream, request)", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(PickupDocker __instance, IPlayer player, NetStream stream, RequestDockArguments request)
    {
        if (stream == null || stream.IsWriting || player == null || !GrabGuard.Watching) return true;
        var start = stream.BitIndex;
        var peek = request;
        try
        {
            peek.Serialize(player, stream);
        }
        catch (Exception e)
        {
            return GrabStream.Unreadable(stream, start, e);
        }
        var pickup = PickupField.GetValue(__instance) as Pickup;
        return GrabStream.Decide(stream, start,
            () =>
            {
                var scene = pickup?.Entity?.Scene;
                var hand = scene?.GetEntity(peek.Interactor)?.GetComponent<Controller>();
                if (hand == null || !GrabRefusal.IsSender(hand, player)) return false;
                var action = peek.IsDocking ? "docking" : "undocking";
                if (peek.IsDocking)
                {
                    var into = scene.GetEntity(peek.Dock);
                    if (into != null) action += " into " + GrabGuard.Name(into);
                }
                return GrabGuard.Check(player, hand, pickup, action);
            },
            () => Restore(__instance, player));
    }

    private static void Restore(PickupDocker docker, IPlayer player)
    {
        if (RequestField.GetValue(docker) is not MethodSyncStruct<RequestDockArguments> sync) return;
        var pickup = PickupField.GetValue(docker) as Pickup;
        var dock = docker.CurrentDock;
        var docked = dock != null;
        sync.SendToPlayer(player, new RequestDockArguments(null, dock, docked, docked && ReferenceEquals(dock.DockedPickup, pickup), false));
    }
}
