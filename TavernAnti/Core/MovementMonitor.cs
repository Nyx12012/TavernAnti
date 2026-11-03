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
    private const float MaxStallCredit = 2f;

    private class State
    {
        public PlayerController Controller;
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
    }

    private static readonly Dictionary<Connection, State> States = new();
    private static readonly List<IPlayer> Buffer = new();
    private static int _errors;

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
        if (connection == null) return;
        State s;
        lock (States)
            if (!States.TryGetValue(connection, out s)) return;
        lock (s)
        {
            s.GraceUntil = DateTime.UtcNow + TimeSpan.FromSeconds(graceSeconds);
            s.GraceTarget = target;
        }
    }

    public static string RecentJump(Connection connection, Vector3 feet, AntiCheatConfig cfg)
    {
        if (connection == null) return null;
        State s;
        lock (States)
            if (!States.TryGetValue(connection, out s)) return null;
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
                Baseline(s, pos, now);
                s.GraceUntil = now + TimeSpan.FromSeconds(cfg.ServerMoveGraceSeconds);
                s.GraceTarget = null;
                TavernAntiLogger.Msg($"{Logins.NameOf(player)} {(isRespawn ? "respawned" : "joined")}: movement baseline {pos} (head {pc.Head?.position})");
                return;
            }

            if (Staff.IsStaff(player))
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
        var speed = Vector3.Distance(start, pos) / dt;
        var verticalSpeed = (pos.y - start.y) / dt;
        s.WindowStart = pos;
        s.WindowAt = now;

        s.AscentTicks = verticalSpeed > cfg.FlyMinAscendSpeedMps && !IsGrounded(pos, cfg.FlyGroundCheckDistance) ? s.AscentTicks + 1 : 0;
        s.OverspeedTicks = speed > cfg.MaxPlayerSpeedMps ? s.OverspeedTicks + 1 : 0;

        if (s.AscentTicks >= cfg.FlyViolationConsecutiveTicks)
        {
            s.AscentTicks = 0;
            if (tracker.Report(player, ViolationType.Flying, $"rising {verticalSpeed:F1} m/s ungrounded for {cfg.FlyViolationConsecutiveTicks} windows at {pos}"))
                PullBack(player, pc, s, s.LastGood, now);
            return;
        }

        if (s.OverspeedTicks >= cfg.SpeedViolationConsecutiveTicks)
        {
            s.OverspeedTicks = 0;
            if (tracker.Report(player, ViolationType.SpeedHack, $"{speed:F1} m/s for {cfg.SpeedViolationConsecutiveTicks} windows at {pos}"))
                PullBack(player, pc, s, s.LastGood, now);
            return;
        }

        if (s.AscentTicks == 0 && s.OverspeedTicks == 0) s.LastGood = pos;
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

    private static bool IsGrounded(Vector3 position, float groundCheckDistance)
    {
        var settings = GlobalSettings<SmoothLocomotionSettings>.Instance;
        if (settings == null) return true;

        return Physics.Raycast(position + Vector3.up * 0.1f, Vector3.down, groundCheckDistance, settings.StandableMask, QueryTriggerInteraction.Ignore);
    }
}
