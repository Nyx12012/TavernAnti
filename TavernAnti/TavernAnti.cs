using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Logging;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;

[assembly: MelonInfo(typeof(TavernAnti.TavernAntiPlugin), "TavernAnti", "0.1.0", "Tavern Team", "https://github.com/ModdingTavern/TavernAnti")]
// MelonLoader auto-patches a plugin at MelonHarmonyEarlyInit, right after Plugins\ load and
// before Unity is up. Patching NetworkEntity.SerializeMove that early breaks every
// Component.get_transform/get_gameObject icall and the server never boots. Patch explicitly
// in OnInitializeMelon instead (this also stops every patch being applied twice).
[assembly: HarmonyDontPatchAll]
namespace TavernAnti;

public class TavernAntiPlugin : MelonPlugin
{
    internal static MelonLogger.Instance Logger { get; private set; }

    // Server mode only; null on a client, which leaves OnUpdate idle.
    private static AntiCheatConfigFile _config;
    private static ViolationTracker _tracker;

    public override void OnEarlyInitializeMelon()
    {
        Logger = LoggerInstance;

        SetupServices();
    }

    public override void OnInitializeMelon()
    {
        try
        {
            // Called explicitly rather than relying on MelonLoader's implicit per-assembly
            // patching, since TavernLib does the latter without an explicit call anywhere
            // and that behavior isn't documented/guaranteed.
            // Fully-qualified: MelonLoader ships a legacy "Harmony" namespace shim that
            // collides with HarmonyLib.Harmony when both are in scope via `using`.
            new HarmonyLib.Harmony("com.moddingtavern.tavernanti").PatchAll(Assembly.GetExecutingAssembly());
            TavernAntiLogger.Msg("Harmony patches applied");

            MovementMonitor.Init();
            Patches.InteractionGuardPatch.Init();
            if (_config != null)
            {
                if (!Staff.Ready())
                    TavernAntiLogger.Error("TavernLib's Roles API was not found (needs TavernLib 1.5.3 or newer): nobody is treated as staff, so admins get checked too.");
                TavernAntiLogger.Msg($"enforcement={_config.LastRead.Enforcement.ToString().ToLowerInvariant()}; staff roles exempt: {string.Join(", ", Staff.Roles)}");
            }
        }
        catch (Exception e)
        {
            Logger.BigError($"Error when applying TavernAnti Harmony patches!!!!! {e}");
            throw;
        }
    }

    public override void OnUpdate()
    {
        if (_config == null) return;
        MovementMonitor.Tick(_config.LastRead, _tracker);
    }

    private void SetupServices()
    {
        try
        {
            // Every enforcement patch gates on NetworkSceneManager.IsServer, so on a client
            // process these services simply never receive any violation reports - but they
            // are only constructed at all on the dedicated server, same pattern TavernLib
            // uses for its own server-only services.
            if (!CommandLineArguments.Contains(CommandLineArguments.StartServerArgument)) return;

            TavernAntiLogger.Msg($"Booting TavernAnti in server mode (build {File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location):yyyy-MM-dd HH:mm:ss} UTC)");

            // TavernServices is TavernLib's own static service locator - shared with TavernLib
            // itself now that TavernLib.dll is a real reference, not TavernAnti's own copy.
            // Safe: keyed by concrete type, and none of TavernAnti's service types collide with
            // TavernLib's (TavernApiManager, DebugHelper, etc).
            // TavernLib's own configs live flat in TavernDirectories.ModdingTavern, which
            // already exists by this point (created as a side effect of TeenyPatches'
            // console-token setup). TavernAntiRoot is a subfolder of that TavernAnti itself
            // introduces, so unlike TavernLib's configs, nothing else creates it - ServerConfigFile
            // only calls File.CreateText, which throws DirectoryNotFoundException on a missing parent.
            Directory.CreateDirectory(TavernAntiDirectories.TavernAntiRoot);

            var config = new AntiCheatConfigFile(TavernAntiDirectories.AntiCheatConfig);
            config.ReadFromFile();
            TavernServices.AddService(config);

            TavernServices.AddService(new TrustedUserStore());
            _tracker = new ViolationTracker(config);
            TavernServices.AddService(_tracker);
            _config = config;
        }
        catch (Exception e)
        {
            Logger.BigError($"Error when setting up TavernAnti services!!!!! {e}");
            throw;
        }
    }
}
