using System.IO;
using Newtonsoft.Json;
using TavernLib.Backend.Server.Configs;
using TavernLib.Services;

namespace TavernAnti.Config;

public class AntiCheatConfigFile : ServerConfigFile<AntiCheatConfig>, IService
{
    private readonly string _path;

    public AntiCheatConfigFile(string filePath) : base(filePath)
    {
        _path = filePath;
    }

    public override void ReadFromFile()
    {
        if (!File.Exists(_path))
        {
            File.WriteAllText(_path, JsonConvert.SerializeObject(new AntiCheatConfig(), Formatting.Indented));
            TavernAntiLogger.Msg($"Wrote the default config to {_path}");
        }
        base.ReadFromFile();
    }
}
