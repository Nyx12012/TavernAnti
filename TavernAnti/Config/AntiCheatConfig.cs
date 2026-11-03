using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using TavernAnti.Core;

namespace TavernAnti.Config;

public class AntiCheatConfig
{
    // Movement plausibility (MovementMonitor)
    [JsonProperty("max_player_speed_mps")] public float MaxPlayerSpeedMps { get; set; } = 12f;
    [JsonProperty("max_teleport_distance")] public float MaxTeleportDistance { get; set; } = 15f;
    [JsonProperty("speed_violation_consecutive_ticks")] public int SpeedViolationConsecutiveTicks { get; set; } = 3;
    // Speed and fly are judged over windows this long; teleport is checked every frame.
    [JsonProperty("movement_window_seconds")] public float MovementWindowSeconds { get; set; } = 0.2f;
    // After a server-driven move (admin teleport, respawn) the player gets this long to arrive.
    [JsonProperty("server_move_grace_seconds")] public float ServerMoveGraceSeconds { get; set; } = 10f;

    // Flight detection (MovementMonitor) - independent server-side ground check,
    // does not trust the client-reported grounded bit.
    [JsonProperty("fly_ground_check_distance")] public float FlyGroundCheckDistance { get; set; } = 1.0f;
    [JsonProperty("fly_min_ascend_speed_mps")] public float FlyMinAscendSpeedMps { get; set; } = 0.75f;
    [JsonProperty("fly_violation_consecutive_ticks")] public int FlyViolationConsecutiveTicks { get; set; } = 5;

    // Interaction range/rate (InteractionGuardPatch)
    [JsonProperty("max_interact_reach")] public float MaxInteractReach { get; set; } = 3.0f;
    [JsonProperty("max_interacts_per_second")] public int MaxInteractsPerSecond { get; set; } = 8;

    // Escalation
    [JsonProperty("violation_weights")]
    public Dictionary<ViolationType, int> Weights { get; set; } = new()
    {
        [ViolationType.SpeedHack] = 2,
        [ViolationType.Teleport] = 5,
        [ViolationType.Flying] = 4,
        [ViolationType.InteractRange] = 3,
        [ViolationType.InteractRate] = 2,
        [ViolationType.UnauthorizedWrite] = 4,
        [ViolationType.CommandPermission] = 10,
    };

    [JsonProperty("violation_window_seconds")] public int ViolationWindowSeconds { get; set; } = 300;
    [JsonProperty("alert_threshold")] public int AlertThreshold { get; set; } = 5;
    [JsonProperty("kick_threshold")] public int KickThreshold { get; set; } = 15;
    [JsonProperty("ban_threshold")] public int BanThreshold { get; set; } = 30;

    // How far up the ladder log -> alert -> kick -> ban TavernAnti may go. From "alert" up it also
    // blocks what it catches (refuses the interaction or command, pulls the player back).
    // Log-only until the server owner opts in.
    [JsonProperty("enforcement")] public string EnforcementName { get; set; } = "log";

    [JsonIgnore]
    public Enforcement Enforcement
    {
        get
        {
            foreach (Enforcement e in Enum.GetValues(typeof(Enforcement)))
                if (string.Equals(e.ToString(), EnforcementName?.Trim(), StringComparison.OrdinalIgnoreCase)) return e;
            return Enforcement.Log;
        }
    }

    /// <summary>True from "alert" up: refuse what was caught, not just record it.</summary>
    [JsonIgnore] public bool Blocks => Enforcement >= Enforcement.Alert;
}
