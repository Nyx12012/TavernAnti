using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using TavernAnti.Config;
using TavernLib.Services;

namespace TavernAnti.Core;

public class ViolationTracker(AntiCheatConfigFile config) : IService
{
    public readonly struct Event(DateTime time, int weight, ViolationType type, string detail)
    {
        public DateTime Time { get; } = time;
        public int Weight { get; } = weight;
        public ViolationType Type { get; } = type;
        public string Detail { get; } = detail;
    }

    private class Record
    {
        public readonly List<Event> Events = new();
    }

    private readonly ConcurrentDictionary<string, Record> _records = new(StringComparer.OrdinalIgnoreCase);

    public bool Report(IPlayer player, ViolationType type, string detail)
    {
        if (player == null) return false;
        return Report(player.ConnectionToRemotePlayer, player, Logins.Of(player), type, detail);
    }

    public bool ReportJoin(Connection connection, string login, ViolationType type, string detail)
    {
        return Report(connection, null, login, type, detail);
    }

    private bool Report(Connection connection, IPlayer player, string login, ViolationType type, string detail)
    {
        try
        {
            if (!AppliesToStaff(type) && (player != null ? Staff.IsStaff(player) : Staff.IsStaff(login))) return false;

            var cfg = config.LastRead;
            var joining = player == null;
            var who = login ?? $"(unknown login, connection {connection?.Identifier})";
            var weight = cfg.Weights.TryGetValue(type, out var w) ? w : 1;
            var record = _records.GetOrAdd(who, _ => new Record());

            int before, score;
            lock (record)
            {
                var now = DateTime.UtcNow;
                var windowStart = now - TimeSpan.FromSeconds(cfg.ViolationWindowSeconds);
                record.Events.RemoveAll(e => e.Time < windowStart);
                before = record.Events.Sum(e => e.Weight);
                record.Events.Add(new Event(now, weight, type, detail));
                score = before + weight;
            }

            var level = cfg.Enforcement;
            TavernAntiLogger.Warn($"{who} violation={type} detail=\"{detail}\" weight={weight} score={score} enforcement={level.ToString().ToLowerInvariant()}");

            var reached = score >= cfg.BanThreshold ? Enforcement.Ban
                : score >= cfg.KickThreshold ? Enforcement.Kick
                : score >= cfg.AlertThreshold ? Enforcement.Alert
                : Enforcement.Log;
            if (reached == Enforcement.Log) return cfg.Blocks;

            var step = reached <= level ? reached : level;
            if (step < reached)
                TavernAntiLogger.Warn($"{who} reached {reached.ToString().ToLowerInvariant()} (score {score}); enforcement stops at {level.ToString().ToLowerInvariant()}");

            var reason = $"suspicious activity score {score}";
            var last = $"{type}: {detail}";
            switch (step)
            {
                case Enforcement.Ban:
                    EnforcementActions.Ban(connection, login, reason, TavernServices.GetService<TrustedUserStore>(), joining, last);
                    lock (record) record.Events.Clear();
                    break;
                case Enforcement.Kick:
                    if (joining)
                    {
                        TavernAntiLogger.Warn($"{who} refused at the door ({reason})");
                        DiscordWebhook.Post($"Refused {who} at the door ({reason}). Last: {last}");
                    }
                    else EnforcementActions.Kick(connection, who, reason, last);
                    break;
                case Enforcement.Alert:
                    if (before < cfg.AlertThreshold)
                    {
                        TavernAntiLogger.Warn($"ALERT: {who} crossed the alert threshold ({reason}, last: {type})");
                        DiscordWebhook.Post($"ALERT: {who} crossed the alert threshold ({reason}). Last: {last}");
                    }
                    break;
            }

            return cfg.Blocks;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Error while recording violation: {e}");
            return false;
        }
    }

    private static bool AppliesToStaff(ViolationType type) =>
        type is ViolationType.KeyIdMismatch or ViolationType.KeyMissing or ViolationType.KeyMismatch;

    public int ScoreOf(string login) => EventsOf(login).Sum(e => e.Weight);

    public List<Event> EventsOf(string login)
    {
        if (login == null || !_records.TryGetValue(login, out var record)) return new List<Event>();
        var windowStart = DateTime.UtcNow - TimeSpan.FromSeconds(config.LastRead.ViolationWindowSeconds);
        lock (record) return record.Events.Where(e => e.Time >= windowStart).ToList();
    }

    public List<KeyValuePair<string, int>> Scores()
    {
        return _records.Keys
            .Select(login => new KeyValuePair<string, int>(login, ScoreOf(login)))
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value)
            .ToList();
    }

    public bool Clear(string login) => login != null && _records.TryRemove(login, out _);
}
