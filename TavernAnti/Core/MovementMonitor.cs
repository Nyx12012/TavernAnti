using System;
using System.Collections.Generic;
using Alta.Chunks;
using Alta.Global;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using TavernAnti.Config;
using UnityEngine;

namespace TavernAnti.Core;

/// <summary>
/// Server-side movement plausibility, sampled from the plugin's OnUpdate.
///
/// Why not Cody's NetworkEntity.SerializeMove postfix: on the server SerializeMove only writes the
/// transform when stream.IsReadingOnRemoteClient(), which is always false there, and MoveEntity is
/// only ever sent BY the server (ForcePositionSync). That hook never saw a position change. The
/// real body position arrives through NetworkTransform, which snaps it into the transform at
/// Socket.EndOfReceival on the server, so reading PlayerFeetPosition each frame sees every move.
///
/// Teleport is checked every frame; speed and fly over movement_window_seconds windows.
/// Server-driven moves (admin teleport, respawn, join) re-baseline and open a grace window
/// (see ServerMovePatch). Client-side self-teleports (tablet, AdminGauntlet, cheat clients) get no
/// grace and still count. Staff are never checked.
/// </summary>
public static class MovementMonitor
{
    // An admin teleport lands 2 m in front of the target player; ground snapping can shift it.
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

    /// <summary>Called once per frame on the main thread.</summary>
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

    /// <summary>
    /// A server-side teleport of this player is under way: accept one jump to the target within
    /// the grace window. Any other jump in that window still counts.
    /// </summary>
    public static void ServerMoved(Connection connection, Vector3 target, float graceSeconds)
    {
        if (connection == null) return;
        State s;
        lock (States)
            if (!States.TryGetValue(connection, out s)) return; // no baseline yet; the first sample sets one
        lock (s)
        {
            s.GraceUntil = DateTime.UtcNow + TimeSpan.FromSeconds(graceSeconds);
            s.GraceTarget = target;
        }
    }

    private static void Sample(IPlayer player, AntiCheatConfig cfg, ViolationTracker tracker, DateTime now)
    {
        var connection = player.ConnectionToRemotePlayer;
        if (connection == null || !connection.IsApproved) return;
        var pc = player.PlayerController;
        if (pc == null) return; // dead, or between bodies

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
            // A new PlayerController means join or respawn (DeadState.Respawn destroys the old one
            // and the server builds a new body). Either way the server placed them: no target known,
            // so accept anything for the grace window.
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
                // Not arrived yet: keep checking normally, so a jump anywhere else still counts.
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
        // Teleport: per frame. A client that stalls sends nothing and then its latest position,
        // so allow for top speed over the time since the position last changed. Capped, or a
        // player who stood still for a minute could then jump hundreds of metres unflagged.
        var jump = Vector3.Distance(s.LastFrame, pos);
        if (jump > 0f)
        {
            var stalled = Math.Min((float)(now - s.LastChangeAt).TotalSeconds, MaxStallCredit);
            var allowed = cfg.MaxTeleportDistance + cfg.MaxPlayerSpeedMps * stalled;
            if (jump > allowed)
            {
                var from = s.LastFrame;
                var back = s.LastGood;
                // Re-baseline on the new spot, or every later move compares against the old one
                // and reads as another teleport (Cody's lock-out: kick in 3 moves, ban in 6).
                Baseline(s, pos, now);
                if (tracker.Report(player, ViolationType.Teleport, $"jumped {jump:F1} m (allowed {allowed:F1}) from {from} to {pos}"))
                    PullBack(player, pc, s, back, now);
                return;
            }
            s.LastFrame = pos;
            s.LastChangeAt = now;
        }

        // Speed and fly: over a window, so close-together samples don't inflate the speed.
        var dt = (float)(now - s.WindowAt).TotalSeconds;
        if (dt < cfg.MovementWindowSeconds) return;

        var start = s.WindowStart;
        var speed = Vector3.Distance(start, pos) / dt;
        var verticalSpeed = (pos.y - start.y) / dt;
        s.WindowStart = pos;
        s.WindowAt = now;

        // Tracked independently of the speed check so one doesn't mask the other -
        // a flying client can ascend well under the horizontal speed cap.
        s.AscentTicks = verticalSpeed > cfg.FlyMinAscendSpeedMps && !IsGrounded(pos, cfg.FlyGroundCheckDistance) ? s.AscentTicks + 1 : 0;
        s.OverspeedTicks = speed > cfg.MaxPlayerSpeedMps ? s.OverspeedTicks + 1 : 0;

        // Checked ahead of the speed hack: a jump's ascent is ungrounded for only a couple of
        // windows before gravity turns it around, so a sustained run of them isn't a normal jump.
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

        // Only a window with no suspicion building counts as a good place to pull back to.
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

    /// <summary>
    /// Movement is client-authoritative, so writing the server transform (Cody's snap-back) does
    /// nothing for the client. ForceTeleportTo asks the client to move; ServerMovePatch then gives
    /// the pull-back its own grace window.
    /// </summary>
    private static void PullBack(IPlayer player, PlayerController pc, State s, Vector3 to, DateTime now)
    {
        if (now < s.NextPullback) return;
        s.NextPullback = now + PullbackCooldown;
        // LastGood is always a spot the player really stood on, but never send anyone to NaN,
        // the origin, or a spot with no chunk: they arrive with no hands.
        if (float.IsNaN(to.x + to.y + to.z) || float.IsInfinity(to.x + to.y + to.z) || to == Vector3.zero
            || LocationChunkHelper.GetBestContaining(to) == null)
        {
            TavernAntiLogger.Warn($"Not pulling {Logins.NameOf(player)} back: {to} is not a safe destination");
            return;
        }
        TavernAntiLogger.Warn($"Pulling {Logins.NameOf(player)} back to {to}");
        pc.ForceTeleportTo(to);
    }

    /// <summary>
    /// Independent server-side ground check (downward raycast against the same StandableMask
    /// the client's own locomotion uses), deliberately not the client-reported grounded bit.
    /// </summary>
    private static bool IsGrounded(Vector3 position, float groundCheckDistance)
    {
        var settings = GlobalSettings<SmoothLocomotionSettings>.Instance;
        if (settings == null) return true; // fail open - don't flag before world/settings are ready

        return Physics.Raycast(position + Vector3.up * 0.1f, Vector3.down, groundCheckDistance, settings.StandableMask, QueryTriggerInteraction.Ignore);
    }
}
