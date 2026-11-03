using System;
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

    public override void WriteToFile()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(LastRead, Formatting.Indented));
        if (!File.Exists(_path))
        {
            File.Move(tmp, _path);
            return;
        }
        try
        {
            File.Replace(tmp, _path, null);
        }
        catch (Exception)
        {
            File.Delete(_path);
            File.Move(tmp, _path);
        }
    }

    public string Reload()
    {
        try
        {
            ReadFromFile();
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public string Save()
    {
        try
        {
            WriteToFile();
            return null;
        }
        catch (Exception e)
        {
            try { File.Delete(_path + ".tmp"); } catch (Exception) { }
            return e.Message;
        }
    }
}
