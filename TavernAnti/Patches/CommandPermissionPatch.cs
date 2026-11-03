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

[HarmonyPatch]
public static class CommandPermissionPatch
{
    private static readonly TimeSpan AttributionWindow = TimeSpan.FromMilliseconds(500);
    private static IPlayer _lastNetworkedSender;
    private static DateTime _lastNetworkedSenderAt;

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
        if (context != null) return true;
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return true;

        var sender = _lastNetworkedSender;
        var attributedRecently = sender != null && DateTime.UtcNow - _lastNetworkedSenderAt <= AttributionWindow;
        if (!attributedRecently) return true;

        _lastNetworkedSender = null;

        if (Staff.IsStaff(sender)) return true;

        var tracker = TavernServices.GetService<ViolationTracker>();
        var block = tracker != null && tracker.Report(sender, ViolationType.CommandPermission,
            $"Networked command \"{command}\" arrived with no CommandContext (would run as the server's own identity) and the sender is not staff");

        if (!block) return true;

        __result = Task.FromResult(CommandResult.ErrorResult(new Exception("Command denied by TavernAnti: sender not authorized"), null));
        return false;
    }
}
