using System.IO;
using TavernLib;

namespace TavernAnti;

public static class TavernAntiDirectories
{
    public static string TavernAntiRoot => Path.Combine(TavernDirectories.ModdingTavern, "TavernAnti");
    public static string AntiCheatConfig => Path.Combine(TavernAntiRoot, "anticheat_config.json");
}
