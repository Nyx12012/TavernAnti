using System;
using System.Reflection;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.QuickAccessActions;
using ATT.Character.QuickAccessMenu;
using HarmonyLib;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;
using NetStream = Alta.Serialization.Stream;

namespace TavernAnti.Patches;

internal static class DevGuardHooks
{
    internal static bool On => DevGuard.Active(TavernServices.GetService<AntiCheatConfigFile>()?.LastRead);

    internal static string RoleOf(IPlayer player) => Staff.RoleOf(Logins.Of(player));

    internal static void Report(IPlayer player, ViolationType type, string detail) =>
        TavernServices.GetService<ViolationTracker>()?.Report(player, type, detail);
}

internal sealed class PlayerInfo
{
    internal uint Mode;
    internal int Id;
    internal string Name = "";
    internal int Status;

    internal static PlayerInfo Read(NetStream s)
    {
        var i = new PlayerInfo();
        s.SerializeBits(ref i.Mode, 16);
        s.SerializeInteger(ref i.Id, 0, int.MaxValue);
        s.SerializeString(ref i.Name, NetStream.StringEncoding.UTF8);
        s.SerializeInteger(ref i.Status, 0, 3);
        return i;
    }

    internal static PlayerInfo Peek(NetStream s)
    {
        NetStream copy = null;
        try
        {
            copy = s.Clone() as NetStream;
            return copy == null ? null : Read(copy);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            (copy as IDisposable)?.Dispose();
        }
    }
}

[HarmonyPatch]
public static class PlayerInfoPatch
{
    private static readonly FieldInfo SyncField = AccessTools.Field(typeof(Player), "syncModeControllerAndUserInfoWithRemote");
    private static Player _pending;
    private static bool _logged;

    public static bool Prepare() => !DevGuard.Standalone && TargetMethod() != null;

    public static MethodBase TargetMethod() => PatchTargets.Resolve(typeof(Player), "SyncModeControllerAndUserInfoWithRemote",
        [typeof(IPlayer), typeof(NetStream)], "Player.SyncModeControllerAndUserInfoWithRemote", "the player info guard", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(Player __instance, IPlayer player, NetStream stream)
    {
        _pending = null;
        try
        {
            if (!NetworkSceneManager.IsServer || stream == null || !stream.IsReading) return true;
            if (!ReferenceEquals(__instance, player) || !DevGuardHooks.On) return true;

            var sent = PlayerInfo.Peek(stream);
            var who = Logins.NameOf(__instance);
            if (sent == null)
            {
                PlayerInfo.Read(stream);
                TavernAntiLogger.Warn($"{who} sent player info that could not be checked: dropped");
                return false;
            }

            var current = __instance.UserInfo;
            var role = DevGuardHooks.RoleOf(__instance);
            var sameUser = current != null && sent.Id == current.Identifier && sent.Name == current.Username;
            var sameMode = sent.Mode == 0 || sent.Mode == (uint)__instance.PlayerMode;
            var shield = (PlayerSubscriberStatus)sent.Status;

            if (role != null && sameUser && sameMode)
            {
                TavernAntiLogger.Msg($"{who} set their shield to {shield} ({role})");
                _pending = __instance;
                return true;
            }

            PlayerInfo.Read(stream);
            var change = $"shield {shield}";
            if (!sameUser) change += $", id/name {sent.Id}/{sent.Name}";
            if (!sameMode) change += $", mode {sent.Mode}";
            if (role != null) TavernAntiLogger.Warn($"{who} ({role}) tried to change their id, name or mode ({change}): dropped");
            else DevGuardHooks.Report(__instance, ViolationType.PlayerInfoRewrite, $"tried to change their player info ({change}), dropped");
            return false;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Player info check failed, letting the game handle it: {e}");
            return true;
        }
    }

    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        var p = _pending;
        _pending = null;
        if (p == null) return __exception;
        try
        {
            if (SyncField?.GetValue(p) is MethodSync sync) sync.SendToChunks([p]);
            else TavernAntiLogger.Warn("Player info sync field missing; others see the new shield on their next join");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Sending the new shield failed: {e}");
        }
        return __exception;
    }
}

[HarmonyPatch]
public static class TeleportTabletPatch
{
    private static bool _logged;

    public static bool Prepare() => !DevGuard.Standalone && TargetMethod() != null;

    public static MethodBase TargetMethod() => PatchTargets.Resolve(typeof(ActionItemsManager), "SpawnActionItemForPlayer",
        [typeof(IPlayer), typeof(int)], "ActionItemsManager.SpawnActionItemForPlayer", "the teleport tablet guard", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(ActionItemsManager __instance, IPlayer player, int dataIndex)
    {
        try
        {
            if (!NetworkSceneManager.IsServer || !DevGuardHooks.On) return true;
            if (!Tablets.IsTeleportTablet(__instance, dataIndex)) return true;
            var role = DevGuardHooks.RoleOf(player);
            if (role != null)
            {
                TavernAntiLogger.Msg($"Teleport tablet allowed for {Logins.NameOf(player)} ({role})");
                return true;
            }
            DevGuardHooks.Report(player, ViolationType.DevTool, "summoned the teleport tablet, refused");
            return false;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Teleport tablet check failed, refusing: {e}");
            return false;
        }
    }
}

[HarmonyPatch]
public static class RepairBoxDevPatch
{
    private static bool _logged;

    public static bool Prepare() => !DevGuard.Standalone && TargetMethod() != null;

    public static MethodBase TargetMethod() => PatchTargets.Resolve(typeof(RepairBox), "SyncDevToolCommand",
        [typeof(IPlayer), typeof(NetStream), typeof(int)], "RepairBox.SyncDevToolCommand", "the repair box dev command guard", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(IPlayer player, NetStream stream, int devCommandIndex)
    {
        try
        {
            if (!NetworkSceneManager.IsServer || stream == null || !stream.IsReading || !DevGuardHooks.On) return true;
            var role = DevGuardHooks.RoleOf(player);
            if (role != null)
            {
                TavernAntiLogger.Msg($"Repair box dev command allowed for {Logins.NameOf(player)} ({role})");
                return true;
            }
            var completed = false;
            var command = devCommandIndex;
            stream.SerializeBool(ref completed);
            stream.SerializeInteger(ref command);
            DevGuardHooks.Report(player, ViolationType.DevTool, $"sent repair box dev command {command}, refused");
            return false;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Repair box dev check failed, letting the game handle it: {e}");
            return true;
        }
    }
}

[HarmonyPatch]
public static class ServerSettingsPatch
{
    private static bool _logged;

    public static bool Prepare() => !DevGuard.Standalone && TargetMethod() != null;

    public static MethodBase TargetMethod() => PatchTargets.Resolve(typeof(ServerSettingsSync), "IsServerSyncPermitted",
        [typeof(IPlayer)], "ServerSettingsSync.IsServerSyncPermitted", "the server settings guard", ref _logged);

    [HarmonyPrefix]
    public static bool Prefix(IPlayer player, ref Task<bool> __result)
    {
        if (!DevGuardHooks.On) return true;
        var ok = false;
        try
        {
            var role = DevGuardHooks.RoleOf(player);
            ok = role != null;
            if (ok) TavernAntiLogger.Msg($"Server settings change allowed for {Logins.NameOf(player)} ({role})");
            else DevGuardHooks.Report(player, ViolationType.DevTool, "tried to change the server settings, refused");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Server settings check failed, refusing: {e}");
        }
        __result = Task.FromResult(ok);
        return false;
    }
}
