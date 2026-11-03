using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Alta.Networking;
using Alta.QuickAccessActions;
using ATT.FeatureTestGymnasium;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using TavernAnti.Config;

namespace TavernAnti.Core;

internal static class DevGuard
{
    private const string StandaloneName = "DevGuard";
    private const string StandaloneHarmonyId = "shadow.devguard";

    internal static bool Standalone { get; private set; }

    private static bool _listed;
    private static bool _overlapChecked;

    internal static bool Active(AntiCheatConfig config) => !Standalone && config != null && config.DevGuard;

    internal static void Detect()
    {
        var where = Find();
        if (where == null) return;
        Standalone = true;
        TavernAntiLogger.Warn($"Standalone DevGuard found ({where}): TavernAnti's built-in DevGuard is off so nothing is patched twice. Remove DevGuard.dll to use the built-in one.");
    }

    private static string Find()
    {
        try
        {
            if (MelonBase.RegisteredMelons.Any(m => string.Equals(m.Info?.Name, StandaloneName, StringComparison.OrdinalIgnoreCase)))
                return "loaded mod";
        }
        catch (Exception e)
        {
            TavernAntiLogger.Warn($"Could not list loaded mods while looking for DevGuard: {e.Message}");
        }

        foreach (var dir in new[] { MelonEnvironment.ModsDirectory, MelonEnvironment.PluginsDirectory })
        {
            try
            {
                var path = Path.Combine(dir, StandaloneName + ".dll");
                if (File.Exists(path)) return path;
            }
            catch (Exception) { }
        }
        return null;
    }

    internal static void Tick(AntiCheatConfig config)
    {
        if (!_overlapChecked) CheckOverlap();
        if (_listed || !Active(config)) return;
        try
        {
            if (!NetworkSceneManager.IsServer) return;
            var manager = ActionItemsManager.Instance;
            if (manager == null) return;
            _listed = true;
            var guarded = Tablets.Guarded(manager);
            TavernAntiLogger.Msg(guarded.Count == 0
                ? "DevGuard: no teleport tablet among the action items"
                : $"DevGuard: teleport tablet action items guarded (staff only): {string.Join(", ", guarded.ToArray())}");
        }
        catch (Exception e)
        {
            _listed = true;
            TavernAntiLogger.Warn($"DevGuard: could not list action items: {e.Message}");
        }
    }

    private static void CheckOverlap()
    {
        _overlapChecked = true;
        if (Standalone) return;
        try
        {
            if (HarmonyLib.Harmony.GetAllPatchedMethods().Any(m => HarmonyLib.Harmony.GetPatchInfo(m)?.Owners.Contains(StandaloneHarmonyId) == true))
                TavernAntiLogger.Error("A standalone DevGuard patched the game after TavernAnti started: both copies are running. Remove DevGuard.dll.");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Warn($"Could not check for a second DevGuard: {e.Message}");
        }
    }
}

internal static class Tablets
{
    private static readonly FieldInfo ItemsField = AccessTools.Field(typeof(ActionItemsManager), "actionItems");
    private static readonly Dictionary<int, bool> Known = new();

    private static ActionItemData[] Items(ActionItemsManager manager) => ItemsField?.GetValue(manager) as ActionItemData[];

    internal static bool IsTeleportTablet(ActionItemsManager manager, int index)
    {
        lock (Known)
            if (Known.TryGetValue(index, out var known)) return known;

        var items = Items(manager) ?? throw new InvalidOperationException("actionItems field not found");
        var hit = false;
        if (index >= 0 && index < items.Length && items[index] != null)
        {
            var prefab = items[index].ActionItemPrefab;
            hit = prefab != null && prefab.GetComponentInChildren<GymnasiumTeleportTablet>(true) != null;
        }
        lock (Known) Known[index] = hit;
        return hit;
    }

    internal static List<string> Guarded(ActionItemsManager manager)
    {
        var items = Items(manager) ?? throw new InvalidOperationException("actionItems field not found, so every action item summon is refused");
        var guarded = new List<string>();
        for (var i = 0; i < items.Length; i++)
            if (IsTeleportTablet(manager, i))
                guarded.Add($"[{i}] {items[i].ActionItemPrefab.name}");
        return guarded;
    }
}
