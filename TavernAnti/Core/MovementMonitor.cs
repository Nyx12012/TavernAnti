using System;
using System.Collections.Generic;
using Alta.Chunks;
using Alta.Global;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using TavernAnti.Config;
using UnityEngine;

namespace TavernAnti.Core;

public static class MovementMonitor
{
    private const float ArrivalRadius = 8f;
    private static readonly TimeSpan PullbackCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RepeatReport = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TerrainRefresh = TimeSpan.FromSeconds(10);
    private const float MaxStallCredit = 2f;
    private const float ProbeDepth = 50f;
    private const float ProbeLift = 0.5f;
    private const float ProbeSpread = 0.5f;

    private class State
    {
        public PlayerController Controller;
        public Climber Climber;
        public Vector3 LastFrame;
        public DateTime LastChangeAt;
        public Vector3 WindowStart;
        public DateTime WindowAt;
        public Vector3 LastGood;
        public int OverspeedTicks;
        public int AscentTicks;
        public DateTime GraceUntil;
        public Vector3? GraceTarget;
        public DateTime NextPullback;
        public DateTime LastTeleportAt;
        public DateTime LastDashAt;
        public bool DashOpen;
        public Vector3 DashFrom;
        public float DashPeak;
        public DateTime NextRideNote;
        public DateTime HoverSince;
        public DateTime NextHoverReport;
        public DateTime UndergroundSince;
        public DateTime NextUndergroundReport;
    }

    private static readonly Dictionary<Connection, State> States = new();
    private static readonly List<IPlayer> Buffer = new();
    private static readonly Vector3[] ProbeOffsets =
    [
        Vector3.zero,
        new(ProbeSpread, 0f, 0f),
        new(-ProbeSpread, 0f, 0f),
        new(0f, 0f, ProbeSpread),
        new(0f, 0f, -ProbeSpread),
    ];
    private static TerrainCollider[] _terrains = new TerrainCollider[0];
    private static DateTime _terrainsAt;
    private static int _errors;
    private static bool _climbLogged;

    public static void Init()
    {
        Logins.Disconnected += connection =>
        {
            lock (States) States.Remove(connection);
        };
    }

    public static void Tick(AntiCheatConfig cfg, ViolationTracker tracker)
    {
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;
        if (cfg == null || tracker == null) return;

        Buffer.Clear();
        Buffer.AddRange(Player.AllPlayers);
        var now = DateTime.UtcNow;
        foreach (var player in Buffer)
        {
            try
            {
                Sample(player, cfg, tracker, now);
            }
            catch (Exception e)
            {
                if (_errors++ < 5) TavernAntiLogger.Error($"Movement check failed for {Logins.NameOf(player)}: {e}");
            }
        }
    }

    public static void ServerMoved(Connection connection, Vector3 target, float graceSeconds)
    {
        var s = StateOf(connection);
        if (s == null) return;
        lock (s)
        {
            s.GraceUntil = DateTime.UtcNow + TimeSpan.FromSeconds(graceSeconds);
            s.GraceTarget = target;
        }
    }

    public static void Dashed(Connection connection)
    {
        var s = StateOf(connection);
        if (s == null) return;
        lock (s)
        {
            if (!s.DashOpen)
            {
                s.DashOpen = true;
                s.DashFrom = s.LastFrame;
                s.DashPeak = 0f;
            }
            s.LastDashAt = DateTime.UtcNow;
        }
    }

    public static string RecentJump(Connection connection, Vector3 feet, AntiCheatConfig cfg)
    {
        var s = StateOf(connection);
        if (s == null) return null;
        lock (s)
        {
            var now = DateTime.UtcNow;
            var since = (now - s.LastTeleportAt).TotalSeconds;
            if (since < cfg.GrabAfterTeleportSeconds) return $"{since:F1} s after a teleport";
            if (now < s.GraceUntil) return null;
            var jump = Vector3.Distance(s.LastFrame, feet);
            if (jump > cfg.MaxTeleportDistance) return $"{jump:F1} m from where the movement check last saw them";
        }
        return null;
    }

    private static State StateOf(Connection connection)
    {
        if (connection == null) return null;
        lock (States)
            return States.TryGetValue(connection, out var s) ? s : null;
    }

    private static void Sample(IPlayer player, AntiCheatConfig cfg, ViolationTracker tracker, DateTime now)
    {
        var connection = player.ConnectionToRemotePlayer;
        if (connection == null || !connection.IsApproved) return;
        var pc = player.PlayerController;
        if (pc == null) return;

        var pos = pc.PlayerFeetPosition;

        State s;
        lock (States)
        {
            if (!States.TryGetValue(connection, out s))
            {
                States[connection] = s = new State();
                Logins.Watch(connection);
            }
        }

        lock (s)
        {
            if (!ReferenceEquals(s.Controller, pc))
            {
                var isRespawn = s.Controller is not null;
                s.Controller = pc;
                s.Climber = FindClimber(pc);
                Baseline(s, pos, now);
                s.GraceUntil = now + TimeSpan.FromSeconds(cfg.ServerMoveGraceSeconds);
                s.GraceTarget = null;
                TavernAntiLogger.Msg($"{Logins.NameOf(player)} {(isRespawn ? "respawned" : "joined")}: movement baseline {pos} (head {pc.Head?.position})");
                return;
            }

            if (!cfg.MovementChecks || Staff.IsStaff(player))
            {
                Baseline(s, pos, now);
                return;
            }

            if (now < s.GraceUntil)
            {
                if (s.GraceTarget is not { } target)
                {
                    Baseline(s, pos, now);
                    return;
                }
                if (Vector3.Distance(pos, target) <= ArrivalRadius)
                {
                    Baseline(s, pos, now);
                    s.GraceUntil = DateTime.MinValue;
                    s.GraceTarget = null;
                    return;
                }
            }
            else if (s.GraceTarget != null)
            {
                TavernAntiLogger.Msg($"{Logins.NameOf(player)} did not arrive at the server move target {s.GraceTarget} within {cfg.ServerMoveGraceSeconds}s");
                s.GraceTarget = null;
            }

            LogClimbState(s);
            CheckFrame(player, pc, s, pos, now, cfg, tracker);
        }
    }

    private static void CheckFrame(IPlayer player, PlayerController pc, State s, Vector3 pos, DateTime now, AntiCheatConfig cfg, ViolationTracker tracker)
    {
        var jump = Vector3.Distance(s.LastFrame, pos);
        if (jump > 0f)
        {
            var stalled = Math.Min((float)(now - s.LastChangeAt).TotalSeconds, MaxStallCredit);
            var allowed = cfg.MaxTeleportDistance + cfg.MaxPlayerSpeedMps * stalled;
            if (jump > allowed)
            {
                var from = s.LastFrame;
                var back = s.LastGood;
                Baseline(s, pos, now);
                s.LastTeleportAt = now;
                if (tracker.Report(player, ViolationType.Teleport, $"jumped {jump:F1} m (allowed {allowed:F1}) from {from} to {pos}"))
                    PullBack(player, pc, s, back, now);
                return;
            }
            s.LastFrame = pos;
            s.LastChangeAt = now;
        }

        var dt = (float)(now - s.WindowAt).TotalSeconds;
        if (dt < cfg.MovementWindowSeconds) return;

        var start = s.WindowStart;
        var speed = new Vector2(pos.x - start.x, pos.z - start.z).magnitude / dt;
        var verticalSpeed = (pos.y - start.y) / dt;
        s.WindowStart = pos;
        s.WindowAt = now;

        var climbing = IsClimbing(s);
        var riding = pc.LocomotionController != null && pc.LocomotionController.MovementContext != null;
        var dashing = (now - s.LastDashAt).TotalSeconds < cfg.DashGraceSeconds;
        var ground = GroundGap(pos);

        if (dashing) s.DashPeak = Math.Max(s.DashPeak, speed);
        else if (s.DashOpen)
        {
            s.DashOpen = false;
            if (s.DashPeak > cfg.MaxPlayerSpeedMps)
                TavernAntiLogger.Msg($"{Logins.NameOf(player)} vanilla teleport dash: {Vector3.Distance(s.DashFrom, pos):F1} m, peak {s.DashPeak:F1} m/s (not counted as speed)");
        }

        if (CheckHover(player, pc, s, pos, now, cfg, tracker, ground, verticalSpeed, climbing || riding || dashing)) return;
        if (CheckUnderground(player, pc, s, pos, now, cfg, tracker)) return;

        var overspeed = speed > cfg.MaxPlayerSpeedMps;
        if (overspeed && riding && now >= s.NextRideNote)
        {
            s.NextRideNote = now + RepeatReport;
            TavernAntiLogger.Msg($"{Logins.NameOf(player)} moved {speed:F1} m/s on a moving platform at {pos} (not counted as speed)");
        }
        s.OverspeedTicks = overspeed && !riding && !dashing ? s.OverspeedTicks + 1 : 0;
        var ungrounded = ground is { } g && g > cfg.FlyGroundCheckDistance;
        s.AscentTicks = verticalSpeed > cfg.FlyMinAscendSpeedMps && ungrounded && !climbing && !riding ? s.AscentTicks + 1 : 0;

        if (s.AscentTicks >= cfg.FlyViolationConsecutiveTicks)
        {
            s.AscentTicks = 0;
            if (tracker.Report(player, ViolationType.Flying, $"rising {verticalSpeed:F1} m/s ungrounded, not climbing, for {cfg.FlyViolationConsecutiveTicks} windows at {pos}"))
                PullBack(player, pc, s, s.LastGood, now);
            return;
        }

        if (s.OverspeedTicks >= cfg.SpeedViolationConsecutiveTicks)
        {
            s.OverspeedTicks = 0;
            if (tracker.Report(player, ViolationType.SpeedHack, $"{speed:F1} m/s across the ground for {cfg.SpeedViolationConsecutiveTicks} windows, no teleport dash, at {pos}"))
                PullBack(player, pc, s, s.LastGood, now);
            return;
        }

        if (s.AscentTicks == 0 && s.OverspeedTicks == 0 && s.HoverSince == DateTime.MinValue && s.UndergroundSince == DateTime.MinValue)
            s.LastGood = pos;
    }

    private static bool CheckHover(IPlayer player, PlayerController pc, State s, Vector3 pos, DateTime now, AntiCheatConfig cfg, ViolationTracker tracker, float? ground, float verticalSpeed, bool excused)
    {
        if (!cfg.HoverCheck || excused || ground is not { } gap || gap <= cfg.HoverMinHeight || Math.Abs(verticalSpeed) > cfg.HoverMaxVerticalSpeedMps)
        {
            s.HoverSince = DateTime.MinValue;
            return false;
        }
        if (s.HoverSince == DateTime.MinValue) s.HoverSince = now;
        var held = (now - s.HoverSince).TotalSeconds;
        if (held < cfg.HoverSeconds || now < s.NextHoverReport) return false;

        s.NextHoverReport = now + RepeatReport;
        var height = float.IsInfinity(gap) ? $"no ground within {ProbeDepth:F0} m" : $"{gap:F1} m above the ground";
        if (!tracker.Report(player, ViolationType.Hovering, $"{height}, vertical {verticalSpeed:F1} m/s, for {held:F1} s, not climbing, at {pos}")) return false;
        PullBack(player, pc, s, s.LastGood, now);
        return true;
    }

    private static bool CheckUnderground(IPlayer player, PlayerController pc, State s, Vector3 pos, DateTime now, AntiCheatConfig cfg, ViolationTracker tracker)
    {
        if (!cfg.UndergroundCheck || !OnOverworld(player, pc) || TerrainHeight(pos, now) is not { } top || top - pos.y <= cfg.UndergroundDepth)
        {
            s.UndergroundSince = DateTime.MinValue;
            return false;
        }
        if (s.UndergroundSince == DateTime.MinValue) s.UndergroundSince = now;
        var held = (now - s.UndergroundSince).TotalSeconds;
        if (held < cfg.UndergroundSeconds || now < s.NextUndergroundReport) return false;

        s.NextUndergroundReport = now + RepeatReport;
        if (!tracker.Report(player, ViolationType.Underground, $"{top - pos.y:F1} m below the terrain for {held:F1} s, outside caves and interiors, at {pos}")) return false;
        PullBack(player, pc, s, s.LastGood, now);
        return true;
    }

    private static void Baseline(State s, Vector3 pos, DateTime now)
    {
        s.LastFrame = pos;
        s.LastChangeAt = now;
        s.WindowStart = pos;
        s.WindowAt = now;
        s.LastGood = pos;
        s.OverspeedTicks = 0;
        s.AscentTicks = 0;
        s.DashOpen = false;
        s.HoverSince = DateTime.MinValue;
        s.UndergroundSince = DateTime.MinValue;
    }

    private static void PullBack(IPlayer player, PlayerController pc, State s, Vector3 to, DateTime now)
    {
        if (now < s.NextPullback) return;
        s.NextPullback = now + PullbackCooldown;
        if (float.IsNaN(to.x + to.y + to.z) || float.IsInfinity(to.x + to.y + to.z) || to == Vector3.zero
            || LocationChunkHelper.GetBestContaining(to) == null)
        {
            TavernAntiLogger.Warn($"Not pulling {Logins.NameOf(player)} back: {to} is not a safe destination");
            return;
        }
        TavernAntiLogger.Warn($"Pulling {Logins.NameOf(player)} back to {to}");
        pc.ForceTeleportTo(to);
    }

    private static Climber FindClimber(PlayerController pc)
    {
        if (pc is not SmoothLocomotionPlayerController smooth) return null;
        var locomotion = smooth.SmoothLocomotion;
        return locomotion != null ? locomotion.GetComponent<Climber>() : null;
    }

    private static bool IsClimbing(State s)
    {
        if (s.Climber == null) return false;
        var handler = s.Climber.ClimbingHandler;
        return handler != null && handler.IsClimbing;
    }

    private static void LogClimbState(State s)
    {
        if (_climbLogged) return;
        _climbLogged = true;
        if (s.Climber != null && s.Climber.ClimbingHandler != null)
            TavernAntiLogger.Msg("Climbing is visible on this server, so the fly and hover checks skip climbing players");
        else
            TavernAntiLogger.Warn("Climbing is not visible on this server (no climbing handler on the player body), so climbing may show as flying or hovering");
    }

    private static float? GroundGap(Vector3 feet)
    {
        var settings = GlobalSettings<SmoothLocomotionSettings>.Instance;
        if (settings == null) return null;
        var best = float.PositiveInfinity;
        foreach (var offset in ProbeOffsets)
        {
            if (Physics.Raycast(feet + offset + Vector3.up * ProbeLift, Vector3.down, out var hit, ProbeDepth + ProbeLift, settings.StandableMask, QueryTriggerInteraction.Ignore))
                best = Math.Min(best, Math.Max(0f, hit.distance - ProbeLift));
        }
        return best;
    }

    private static bool OnOverworld(IPlayer player, PlayerController pc)
    {
        var chunk = player.ChunkManager?.Chunk;
        if (chunk != null) return chunk is OverworldChunk;
        return pc.Entity != null && pc.Entity.Chunk is OverworldChunk;
    }

    private static float? TerrainHeight(Vector3 pos, DateTime now)
    {
        if (now >= _terrainsAt)
        {
            _terrains = UnityEngine.Object.FindObjectsOfType<TerrainCollider>();
            _terrainsAt = now + TerrainRefresh;
        }
        float? best = null;
        foreach (var terrain in _terrains)
        {
            if (terrain == null || !terrain.enabled || !terrain.gameObject.activeInHierarchy) continue;
            var b = terrain.bounds;
            if (pos.x < b.min.x || pos.x > b.max.x || pos.z < b.min.z || pos.z > b.max.z) continue;
            var ray = new Ray(new Vector3(pos.x, b.max.y + 1f, pos.z), Vector3.down);
            if (terrain.Raycast(ray, out var hit, b.size.y + 2f))
                best = best is { } h ? Math.Max(h, hit.point.y) : hit.point.y;
        }
        return best;
    }
}
