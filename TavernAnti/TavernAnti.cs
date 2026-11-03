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
// Patched by hand in OnInitializeMelon: auto patching a plugin this early hangs the server boot.
[assembly: HarmonyDontPatchAll]
namespace TavernAnti;

public class TavernAntiPlugin : MelonPlugin
{
    internal static MelonLogger.Instance Logger { get; private set; }

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
            if (!CommandLineArguments.Contains(CommandLineArguments.StartServerArgument)) return;

            TavernAntiLogger.Msg($"Booting TavernAnti in server mode (build {File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location):yyyy-MM-dd HH:mm:ss} UTC)");

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
