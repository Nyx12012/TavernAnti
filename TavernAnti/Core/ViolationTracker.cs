using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Alta.Networking.Scripts.Player;
using TavernAnti.Config;
using TavernLib.Services;

namespace TavernAnti.Core;

/// <summary>
/// Per-login sliding-window suspicion accumulator. Every detector reports into this instead of
/// acting directly, so the ladder (log -> alert -> kick -> ban) lives in one place. Keyed on the
/// connection login, so a kicked player who rejoins keeps their score until the window lapses.
/// </summary>
public class ViolationTracker(AntiCheatConfigFile config) : IService
{
    private class Record
    {
        public readonly List<(DateTime Time, int Weight)> Events = new();
    }

    private readonly ConcurrentDictionary<string, Record> _records = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a violation and runs the ladder. Returns true if the caller should block what it
    /// caught (enforcement is "alert" or higher and the player is not staff).
    /// </summary>
    public bool Report(IPlayer player, ViolationType type, string detail)
    {
        if (player == null) return false;

        try
        {
            if (Staff.IsStaff(player)) return false;

            var cfg = config.LastRead;
            var login = Logins.Of(player);
            var who = login ?? $"(unknown login, connection {player.ConnectionToRemotePlayer?.Identifier})";
            var weight = cfg.Weights.TryGetValue(type, out var w) ? w : 1;
            var record = _records.GetOrAdd(who, _ => new Record());

            int before, score;
            lock (record)
            {
                var now = DateTime.UtcNow;
                var windowStart = now - TimeSpan.FromSeconds(cfg.ViolationWindowSeconds);
                record.Events.RemoveAll(e => e.Time < windowStart);
                before = record.Events.Sum(e => e.Weight);
                record.Events.Add((now, weight));
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
            switch (step)
            {
                case Enforcement.Ban:
                    EnforcementActions.Ban(player, login, reason, TavernServices.GetService<TrustedUserStore>());
                    lock (record) record.Events.Clear();
                    break;
                case Enforcement.Kick:
                    EnforcementActions.Kick(player, who, reason);
                    break;
                case Enforcement.Alert:
                    // Alert once per crossing, not on every violation above the line.
                    // TODO(step 5): Discord webhook.
                    if (before < cfg.AlertThreshold)
                        TavernAntiLogger.Warn($"ALERT: {who} crossed the alert threshold ({reason}, last: {type})");
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

    public int ScoreOf(string login)
    {
        if (login == null || !_records.TryGetValue(login, out var record)) return 0;
        var windowStart = DateTime.UtcNow - TimeSpan.FromSeconds(config.LastRead.ViolationWindowSeconds);
        lock (record) return record.Events.Where(e => e.Time >= windowStart).Sum(e => e.Weight);
    }

    public void Clear(string login)
    {
        if (login != null) _records.TryRemove(login, out _);
    }
}
