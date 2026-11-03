using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Alta.Console;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Newtonsoft.Json;
using TavernAnti.Config;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Commands;

[Module("tavernanti", "TavernAnti anticheat: status, settings, players, KeyGuard keys")]
public static class TavernAntiCommands
{
    private static readonly string[] ConsoleOnly = ["discord_webhook_url"];

    private static AntiCheatConfigFile ConfigFile => TavernServices.GetService<AntiCheatConfigFile>();
    private static ViolationTracker Tracker => TavernServices.GetService<ViolationTracker>();

    [Command("status", "Enforcement, checks, webhook and every player with a score")]
    public static string Status()
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        var cfg = file.LastRead;
        var sb = new StringBuilder();
        sb.AppendLine($"enforcement: {Level(cfg)} (alert at {cfg.AlertThreshold}, kick at {cfg.KickThreshold}, ban at {cfg.BanThreshold}, window {cfg.ViolationWindowSeconds} s)");
        sb.AppendLine($"checks: movement {On(cfg.MovementChecks)}{Movement(cfg)}, interact {On(cfg.InteractChecks)}, grab {On(cfg.GrabChecks)}, dev_guard {(DevGuard.Standalone ? "standalone DevGuard" : On(cfg.DevGuard))}, join_guard {On(cfg.JoinGuard)}, key_guard {On(cfg.KeyGuard)}");
        sb.AppendLine($"webhook: {WebhookState(cfg)}");
        if (!Staff.Ready()) sb.AppendLine("staff: TavernLib's Roles API is missing, nobody is treated as staff");
        else if (KeyGuard.Gating) sb.AppendLine("staff: rights wait for each staff login's key proof");

        var online = Player.AllPlayers.Where(p => p?.ConnectionToRemotePlayer != null).ToList();
        var tracker = Tracker;
        sb.AppendLine($"online: {online.Count} player(s)");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in online)
        {
            var c = player.ConnectionToRemotePlayer;
            var login = Logins.NameOf(player);
            seen.Add(login);
            sb.AppendLine($"  {login} (id {c.UserInfo?.Identifier}) score {tracker?.ScoreOf(login) ?? 0}{StaffNote(player, login)}{(cfg.KeyGuard ? $", key: {KeyGuard.StateOf(c)}" : "")}");
        }
        var offline = tracker?.Scores().Where(s => !seen.Contains(s.Key)).ToList() ?? new List<KeyValuePair<string, int>>();
        if (offline.Count > 0)
            sb.AppendLine("offline with a score: " + string.Join(", ", offline.Select(s => $"{s.Key} {s.Value}").ToArray()));
        return sb.ToString().TrimEnd();
    }

    [Command("enforcement", "Show the enforcement level")]
    public static string ShowEnforcement()
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        return $"enforcement: {Level(file.LastRead)} (log, alert, kick or ban; blocking starts at alert)";
    }

    [Command("enforcement", "Set the enforcement level: enforcement <log|alert|kick|ban>")]
    public static string SetEnforcement(string level) => Set("enforcement", level);

    [Command("settings", "List every setting and its value")]
    public static string Settings()
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        var cfg = file.LastRead;
        var sb = new StringBuilder();
        foreach (var pair in SettingProps())
        {
            if (pair.Key == "violation_weights") continue;
            var value = pair.Key == "discord_webhook_url"
                ? (string.IsNullOrEmpty(cfg.DiscordWebhookUrl?.Trim()) ? "(not set)" : $"(set, {DiscordWebhook.Host(cfg.DiscordWebhookUrl)})")
                : Show(pair.Value.GetValue(cfg, null));
            sb.AppendLine($"{pair.Key} = {value}");
        }
        sb.AppendLine("violation_weights: " + Weights(cfg));
        return sb.ToString().TrimEnd();
    }

    [Command("set", "Change a setting and save it: set <name> <value>")]
    public static string Set(string name, string value)
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        name = name?.Trim().ToLowerInvariant() ?? "";
        value = value?.Trim() ?? "";
        if (ConsoleOnly.Contains(name))
            return "Set discord_webhook_url in anticheat_config.json, then run 'tavernanti reload'. The console prints every command line to the server output, so the URL never goes through it.";
        if (name == "violation_weights") return "Use 'tavernanti weight <violation> <n>'";

        var keys = SettingProps();
        if (!keys.TryGetValue(name, out var prop)) return $"No setting called {name}. 'tavernanti settings' lists them.";

        var cfg = file.LastRead;
        var old = prop.GetValue(cfg, null);
        object parsed;
        string why;
        if (name == "enforcement")
        {
            parsed = ParseLevel(value, out why);
        }
        else parsed = Parse(prop.PropertyType, value, out why);
        if (parsed == null) return $"{name}: {why}";

        prop.SetValue(cfg, parsed, null);
        TavernAntiLogger.Msg($"Console set {name}: {Show(old)} -> {Show(parsed)}");
        return $"{name}: {Show(old)} -> {Show(parsed)}{Saved(file)}{ThresholdNote(cfg)}";
    }

    [Command("weight", "List the violation weights")]
    public static string ShowWeights()
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        return "violation_weights: " + Weights(file.LastRead);
    }

    [Command("weight", "Change a violation weight and save it: weight <violation> <n>")]
    public static string SetWeight(string violation, string weight)
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        ViolationType? type = null;
        foreach (ViolationType t in Enum.GetValues(typeof(ViolationType)))
            if (string.Equals(t.ToString(), violation?.Trim(), StringComparison.OrdinalIgnoreCase)) type = t;
        if (type == null) return $"No violation called {violation}. Known: {string.Join(", ", Enum.GetNames(typeof(ViolationType)))}";
        if (!int.TryParse(weight?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 1000)
            return "The weight must be a whole number from 0 to 1000";

        var cfg = file.LastRead;
        var old = cfg.Weights.TryGetValue(type.Value, out var w) ? w : 1;
        cfg.Weights[type.Value] = n;
        TavernAntiLogger.Msg($"Console set weight {type.Value}: {old} -> {n}");
        return $"{type.Value} weight: {old} -> {n}{Saved(file)}{(n == 0 ? " (still logged, never adds to the score)" : "")}";
    }

    [Command("reload", "Re-read anticheat_config.json and keyguard_bindings.json")]
    public static string Reload()
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        var error = file.Reload();
        if (error != null)
        {
            TavernAntiLogger.Error($"Console reload: anticheat_config.json could not be read, the old settings stay live: {error}");
            return $"anticheat_config.json could not be read, the old settings stay live: {error}";
        }
        var cfg = file.LastRead;
        var keys = KeyGuard.ReloadBindings();
        TavernAntiLogger.Msg($"Console reload: enforcement={Level(cfg)}, dev_guard={On(cfg.DevGuard)}, join_guard={On(cfg.JoinGuard)}, key_guard={On(cfg.KeyGuard)}; KeyGuard: {keys}");
        return $"Reloaded. enforcement {Level(cfg)}; movement {On(cfg.MovementChecks)}{Movement(cfg)}, interact {On(cfg.InteractChecks)}, grab {On(cfg.GrabChecks)}, dev_guard {On(cfg.DevGuard)}, join_guard {On(cfg.JoinGuard)}, key_guard {On(cfg.KeyGuard)}; webhook {WebhookState(cfg)}; KeyGuard: {keys}{ThresholdNote(cfg)}";
    }

    [Command("webhook", "Show the Discord webhook state")]
    public static string Webhook()
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        return $"webhook: {WebhookState(file.LastRead)}. Use 'tavernanti webhook on|off|test'; the URL goes in anticheat_config.json (discord_webhook_url), then 'tavernanti reload'.";
    }

    [Command("webhook", "Discord webhook: webhook <on|off|test>")]
    public static string Webhook(string action)
    {
        var file = ConfigFile;
        if (file == null) return NotRunning;
        switch (action?.Trim().ToLowerInvariant())
        {
            case "test":
                return DiscordWebhook.Test();
            case "on":
            case "off":
                var cfg = file.LastRead;
                cfg.DiscordWebhookEnabled = action.Trim().ToLowerInvariant() == "on";
                TavernAntiLogger.Msg($"Console set discord_webhook_enabled: {On(cfg.DiscordWebhookEnabled)}");
                return $"webhook: {WebhookState(cfg)}{Saved(file)}";
            default:
                return "Use 'tavernanti webhook on', 'off' or 'test'";
        }
    }

    [Command("player", "Score and recent violations of a player: player <login or id>")]
    public static string PlayerInfo(string who)
    {
        var file = ConfigFile;
        var tracker = Tracker;
        if (file == null || tracker == null) return NotRunning;
        var login = Resolve(who, out var player);
        if (login == null) return "Give a login or a player id";

        var events = tracker.EventsOf(login);
        var sb = new StringBuilder();
        var c = player?.ConnectionToRemotePlayer;
        sb.Append($"{login}: score {events.Sum(e => e.Weight)} in the last {file.LastRead.ViolationWindowSeconds} s");
        sb.Append(c != null ? $", online (id {c.UserInfo?.Identifier})" : ", not online");
        if (player != null) sb.Append(StaffNote(player, login));
        else if (Staff.RoleOf(login) is { } role) sb.Append($", {role}");
        if (c != null && file.LastRead.KeyGuard) sb.Append($", key: {KeyGuard.StateOf(c)}");
        sb.AppendLine();
        if (events.Count == 0) sb.AppendLine("No violations in the window");
        var now = DateTime.UtcNow;
        foreach (var e in events.OrderByDescending(e => e.Time).Take(20))
            sb.AppendLine($"  {(now - e.Time).TotalSeconds:F0} s ago: {e.Type} ({(e.LogOnly ? "log only" : "+" + e.Weight)}) {e.Detail}");
        if (events.Count > 20) sb.AppendLine($"  ... {events.Count - 20} older");
        return sb.ToString().TrimEnd();
    }

    [Command("clear", "Clear a player's score: clear <login or id>")]
    public static string Clear(string who)
    {
        var tracker = Tracker;
        if (tracker == null) return NotRunning;
        var login = Resolve(who, out _);
        if (login == null) return "Give a login or a player id";
        var score = tracker.ScoreOf(login);
        if (!tracker.Clear(login)) return $"{login} has no score to clear";
        TavernAntiLogger.Msg($"Console cleared the score of {login} (was {score})");
        return $"{login}: score {score} cleared";
    }

    [Command("keys", "KeyGuard: list key bindings and online key states")]
    public static string ListKeys() => KeyGuard.Describe();

    [Command("keys", "KeyGuard: keys unbind <login> (next proof binds a new key) or keys rebind <login> (also asks them to rejoin)")]
    public static string Keys(string action, string who)
    {
        var verb = action?.Trim().ToLowerInvariant();
        if (verb != "unbind" && verb != "rebind") return "Use 'tavernanti keys', 'keys unbind <login>' or 'keys rebind <login>'";
        var login = Resolve(who, out _);
        if (login == null) return "Give a login or a player id";
        return KeyGuard.Unbind(login, verb == "rebind");
    }

    private const string NotRunning = "TavernAnti is not running in server mode";

    private static string Level(AntiCheatConfig cfg) => cfg.Enforcement.ToString().ToLowerInvariant();

    private static string On(bool on) => on ? "on" : "off";

    private static string Movement(AntiCheatConfig cfg) =>
        $" (hover {On(cfg.HoverCheck)}, underground {On(cfg.UndergroundCheck)}{(cfg.MovementLogOnly ? ", speed/fly/hover/underground log only" : "")})";

    private static string WebhookState(AntiCheatConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.DiscordWebhookUrl?.Trim())) return "off (no discord_webhook_url set)";
        var problem = DiscordWebhook.Problem(cfg.DiscordWebhookUrl);
        if (problem != null) return $"off ({problem})";
        return cfg.DiscordWebhookEnabled ? $"on, posts to {DiscordWebhook.Host(cfg.DiscordWebhookUrl)}" : "set but switched off (discord_webhook_enabled: false)";
    }

    private static string StaffNote(IPlayer player, string login)
    {
        var raw = Staff.RoleOf(login);
        if (raw == null) return "";
        return Staff.RoleOf(player) != null ? $", {raw} (exempt)" : $", {raw} (rights wait for the key proof)";
    }

    private static string Weights(AntiCheatConfig cfg) =>
        string.Join(", ", Enum.GetValues(typeof(ViolationType)).Cast<ViolationType>()
            .Select(t => $"{t} {(cfg.Weights.TryGetValue(t, out var w) ? w : 1)}").ToArray());

    private static string Saved(AntiCheatConfigFile file)
    {
        var error = file.Save();
        if (error == null) return " (saved)";
        TavernAntiLogger.Error($"Saving anticheat_config.json failed, the change is live until a restart: {error}");
        return $" (live, but saving failed: {error})";
    }

    private static string ThresholdNote(AntiCheatConfig cfg) =>
        cfg.AlertThreshold <= cfg.KickThreshold && cfg.KickThreshold <= cfg.BanThreshold
            ? ""
            : $". Note: thresholds are out of order (alert {cfg.AlertThreshold}, kick {cfg.KickThreshold}, ban {cfg.BanThreshold}), the highest one reached wins";

    private static Dictionary<string, PropertyInfo> SettingProps()
    {
        var keys = new Dictionary<string, PropertyInfo>();
        foreach (var prop in typeof(AntiCheatConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttributes(typeof(JsonPropertyAttribute), true).FirstOrDefault() as JsonPropertyAttribute;
            if (attr?.PropertyName != null && prop.CanWrite) keys[attr.PropertyName] = prop;
        }
        return keys;
    }

    private static object ParseLevel(string value, out string why)
    {
        why = null;
        foreach (Enforcement e in Enum.GetValues(typeof(Enforcement)))
            if (string.Equals(e.ToString(), value, StringComparison.OrdinalIgnoreCase)) return e.ToString().ToLowerInvariant();
        why = "use log, alert, kick or ban";
        return null;
    }

    private static object Parse(Type type, string value, out string why)
    {
        why = null;
        if (type == typeof(bool))
        {
            switch (value.ToLowerInvariant())
            {
                case "true": case "on": case "1": case "yes": return true;
                case "false": case "off": case "0": case "no": return false;
            }
            why = "use on or off";
            return null;
        }
        if (type == typeof(int))
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) return i;
            why = "use a whole number, 0 or more";
            return null;
        }
        if (type == typeof(float))
        {
            if (float.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var f) && !float.IsInfinity(f) && !float.IsNaN(f)) return f;
            why = "use a number, 0 or more, with a dot for decimals";
            return null;
        }
        why = "this setting cannot be changed from the console, edit anticheat_config.json and reload";
        return null;
    }

    private static string Show(object value) => value switch
    {
        null => "(none)",
        bool b => On(b),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Resolve(string who, out IPlayer player)
    {
        player = null;
        who = who?.Trim();
        if (string.IsNullOrEmpty(who)) return null;
        var isId = int.TryParse(who, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        foreach (var p in Player.AllPlayers)
        {
            var c = p?.ConnectionToRemotePlayer;
            if (c == null) continue;
            var login = Logins.Of(c);
            if (string.Equals(login, who, StringComparison.OrdinalIgnoreCase) || (isId && c.UserInfo != null && c.UserInfo.Identifier == id))
            {
                player = p;
                return login;
            }
        }
        return who;
    }
}
