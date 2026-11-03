using System;
using System.Threading.Tasks;
using Alta.Console;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using ATT.Character.QuickAccessMenu;
using HarmonyLib;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Patches;

/// <summary>
/// Closes the arbitrary-console-command exploit (knowscheets' "runtimeconsole" reflects into
/// RuntimeConsole.RunCommandOnServer, which is just CommandSync.Instance.RouteCommand(cmd, null)
/// under the hood - the same call the legitimate "Run on Server" debug console button makes).
///
/// Confirmed by reading the decompiled source (not just inferred): CommandSync.RouteCommand
/// networks the command to the server via a MethodSync on EntityMessageType.SyncB. The server
/// receives it in CommandSync.SyncCommand(IPlayer player, Stream stream), which the IL2CPP
/// decompile only shows deserializing the string (the async continuation is lost to
/// decompilation). Separately, Alta.Console.CommandService.Handle(command, context) - the single
/// choke point ALL commands run through - when given context == null builds a default
/// CommandContext from the *server process's own logged-in account*
/// (ApiAccess.ApiClient.UserClient.LoggedInUserInfo), not from whichever player actually sent
/// the command. If SyncCommand's lost continuation falls through to Handle(cmd, null) rather
/// than building a context scoped to the sending player, any connected player can run commands
/// under the server's own identity - which matches the exploit's observed behavior exactly.
///
/// This patch doesn't depend on resolving that decompilation gap: it intercepts at
/// CommandService.Handle itself (the one place every path - local console, server console, and
/// the networked path - converges), and treats "networked command arrived with no context" as
/// the dangerous case regardless of exactly how SyncCommand's continuation gets there.
///
/// There is currently no accessible, verifiable source of per-player command permissions
/// (the game's real Policy claims live on the join-time JWT and aren't retained anywhere after
/// join completes), so this ships as a coarser check: the sender's connection login must hold a
/// staff role (admin/moderator/owner) through TavernLib's Roles API (see Core.Staff), rather
/// than a full per-player CommandContext reconstruction.
///
/// MANDATORY LIVE VERIFICATION before trusting this with enforcement above log: confirm on a real local
/// dedicated server that a non-operator client using the exploit's RunCommandOnServer
/// reflection call is actually denied here, and that legitimate operator use (server owner
/// console) still works.
/// </summary>
[HarmonyPatch]
public static class CommandPermissionPatch
{
    private static readonly TimeSpan AttributionWindow = TimeSpan.FromMilliseconds(500);
    private static IPlayer _lastNetworkedSender;
    private static DateTime _lastNetworkedSenderAt;

    // SyncCommand is private, so it can't be referenced via nameof() against the raw
    // (non-publicized) Root.Township.dll - Harmony resolves this string by reflection at
    // runtime instead, regardless of compile-time accessibility.
    [HarmonyPatch(typeof(CommandSync), "SyncCommand")]
    [HarmonyPrefix]
    public static void StashSender(IPlayer player)
    {
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;

        _lastNetworkedSender = player;
        _lastNetworkedSenderAt = DateTime.UtcNow;
    }

    [HarmonyPatch(typeof(CommandService), nameof(CommandService.Handle))]
    [HarmonyPrefix]
    public static bool GuardHandle(string command, CommandContext context, ref Task<CommandResult> __result)
    {
        if (context != null) return true; // already scoped to a real caller - not our concern
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return true;

        var sender = _lastNetworkedSender;
        var attributedRecently = sender != null && DateTime.UtcNow - _lastNetworkedSenderAt <= AttributionWindow;
        if (!attributedRecently) return true; // e.g. the server's own local console - not networked

        _lastNetworkedSender = null; // consume the attribution so it can't leak into an unrelated later call

        if (Staff.IsStaff(sender)) return true;

        var tracker = TavernServices.GetService<ViolationTracker>();
        var block = tracker != null && tracker.Report(sender, ViolationType.CommandPermission,
            $"Networked command \"{command}\" arrived with no CommandContext (would run as the server's own identity) and the sender is not staff");

        if (!block) return true;

        __result = Task.FromResult(CommandResult.ErrorResult(new Exception("Command denied by TavernAnti: sender not authorized"), null));
        return false;
    }
}
