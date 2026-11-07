using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using TavernAnti.Core;

namespace TavernAnti.Config;

public class AntiCheatConfig
{
    [JsonProperty("movement_checks")] public bool MovementChecks { get; set; } = true;
    [JsonProperty("max_player_speed_mps")] public float MaxPlayerSpeedMps { get; set; } = 12f;
    [JsonProperty("max_teleport_distance")] public float MaxTeleportDistance { get; set; } = 15f;
    [JsonProperty("speed_violation_consecutive_ticks")] public int SpeedViolationConsecutiveTicks { get; set; } = 3;
    [JsonProperty("movement_window_seconds")] public float MovementWindowSeconds { get; set; } = 0.2f;
    [JsonProperty("server_move_grace_seconds")] public float ServerMoveGraceSeconds { get; set; } = 10f;

    [JsonProperty("fly_ground_check_distance")] public float FlyGroundCheckDistance { get; set; } = 1.0f;
    [JsonProperty("fly_min_ascend_speed_mps")] public float FlyMinAscendSpeedMps { get; set; } = 0.75f;
    [JsonProperty("fly_violation_consecutive_ticks")] public int FlyViolationConsecutiveTicks { get; set; } = 5;

    [JsonProperty("movement_log_only")] public bool MovementLogOnly { get; set; } = true;
    [JsonProperty("dash_grace_seconds")] public float DashGraceSeconds { get; set; } = 1f;
    [JsonProperty("hover_check")] public bool HoverCheck { get; set; } = true;
    [JsonProperty("hover_min_height")] public float HoverMinHeight { get; set; } = 1.5f;
    [JsonProperty("hover_max_vertical_speed_mps")] public float HoverMaxVerticalSpeedMps { get; set; } = 0.3f;
    [JsonProperty("hover_seconds")] public float HoverSeconds { get; set; } = 2f;
    [JsonProperty("underground_check")] public bool UndergroundCheck { get; set; } = true;
    [JsonProperty("underground_depth")] public float UndergroundDepth { get; set; } = 2f;
    [JsonProperty("underground_seconds")] public float UndergroundSeconds { get; set; } = 3f;

    [JsonProperty("interact_checks")] public bool InteractChecks { get; set; } = true;
    [JsonProperty("max_interacts_per_second")] public int MaxInteractsPerSecond { get; set; } = 8;

    [JsonProperty("grab_checks")] public bool GrabChecks { get; set; } = true;
    [JsonProperty("grab_max_hand_to_item")] public float GrabMaxHandToItem { get; set; } = 2.5f;
    [JsonProperty("grab_max_hand_to_head")] public float GrabMaxHandToHead { get; set; } = 1.5f;
    [JsonProperty("grab_max_head_to_body")] public float GrabMaxHeadToBody { get; set; } = 4f;
    [JsonProperty("grab_after_teleport_seconds")] public float GrabAfterTeleportSeconds { get; set; } = 3f;
    [JsonProperty("grab_max_hand_to_dock")] public float GrabMaxHandToDock { get; set; } = 4f;

    [JsonProperty("dev_guard")] public bool DevGuard { get; set; } = true;
    [JsonProperty("join_guard")] public bool JoinGuard { get; set; } = true;
    [JsonProperty("key_guard")] public bool KeyGuard { get; set; } = false;
    [JsonProperty("key_guard_proof_seconds")] public float KeyGuardProofSeconds { get; set; } = 45f;

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
        [ViolationType.GrabOtherPlayer] = 5,
        [ViolationType.GrabArmReach] = 3,
        [ViolationType.GrabBodyReach] = 3,
        [ViolationType.ForgedJoinId] = 10,
        [ViolationType.BadJoinToken] = 10,
        [ViolationType.ForgedRoleClaim] = 5,
        [ViolationType.PlayerInfoRewrite] = 5,
        [ViolationType.DevTool] = 3,
        [ViolationType.KeyIdMismatch] = 10,
        [ViolationType.KeyMissing] = 5,
        [ViolationType.KeyMismatch] = 10,
        [ViolationType.Hovering] = 4,
        [ViolationType.Underground] = 4,
        [ViolationType.GrabForged] = 10,
        [ViolationType.GrabWhileDead] = 1,
    };

    [JsonProperty("violation_window_seconds")] public int ViolationWindowSeconds { get; set; } = 300;
    [JsonProperty("alert_threshold")] public int AlertThreshold { get; set; } = 5;
    [JsonProperty("kick_threshold")] public int KickThreshold { get; set; } = 15;
    [JsonProperty("ban_threshold")] public int BanThreshold { get; set; } = 30;

    [JsonProperty("enforcement")] public string EnforcementName { get; set; } = "log";

    [JsonProperty("discord_webhook_url")] public string DiscordWebhookUrl { get; set; } = "";
    [JsonProperty("discord_webhook_enabled")] public bool DiscordWebhookEnabled { get; set; } = true;

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

    [JsonIgnore] public bool Blocks => Enforcement >= Enforcement.Alert;

    public bool IsLogOnly(ViolationType type) =>
        MovementLogOnly && type is ViolationType.SpeedHack or ViolationType.Flying or ViolationType.Hovering or ViolationType.Underground;
}
