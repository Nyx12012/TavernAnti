using System;
using System.Text;
using Alta.Api.DataTransferModels.Converters;
using Alta.Networking;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Patches;

/// <summary>
/// Root-cause fix for the "set IsDeveloper to true" family of exploits, deeper than
/// DeveloperClaimGuardPatch: JWTUtility.CreateFromString - THE single decode path every
/// identity token in the entire codebase goes through - never validates a signature at all,
/// for any caller, regardless of its `includeRawData` flag (traced in full: it splits the raw
/// string on '.', base64-decodes only the middle/payload segment, parses it as JSON, and
/// builds a JwtSecurityToken directly from that - the header and signature segments are never
/// even read). Any player can hand-craft a token with any claims they want; nothing anywhere
/// in this codebase checks otherwise post-shutdown (see DeveloperClaimGuardPatch's doc comment
/// for why - TavernLib's own identity-token validation call is patched to always return true,
/// because there is no live Alta backend left to validate against).
///
/// DeveloperClaimGuardPatch closes this at UserRolesUtility.GetRolesFromIdentityToken, but that
/// only protects consumers that go through UserRolesUtility. It does NOT protect
/// ServerPlayerConnectionHandlerOld.CheckIfPlayerIsAllowed's separate "dev join token" branch
/// (~line 168), which reads the "Policy":"dev" claim directly off the JwtSecurityToken and, if
/// present, skips almost every other join check entirely (server_id match, server
/// whitelist/blacklist, connection limit, version check, duplicate-join check, and the
/// VR-headset-required check) - gated only on IsValidShortLivedIdentityTokenAsync /
/// IsValidIdentityTokenAsync, which likely also resolve to the same always-true validator.
///
/// Rather than trying to intercept every current and future consumer individually, this
/// patches the shared decode itself: before JWTUtility.CreateFromString runs, this rewrites
/// the raw token string to strip an unverifiable "Policy" claim (unless the token's Username
/// claim has a staff role (admin/moderator/owner) in users.json, per Core.Staff), so every
/// consumer - CheckIfPlayerIsAllowed's direct claim check, UserRolesUtility, and anything else
/// that decodes identity tokens - sees a token that never had the claim in the first place.
/// This makes DeveloperClaimGuardPatch's own check largely redundant in practice (the claim
/// won't survive this far for non-operators) but it's kept as defense-in-depth in case some
/// path decodes tokens another way.
///
/// Fails open by design: any parsing surprise leaves the token untouched rather than risking a
/// crash in the join/command pipeline. That means the worst case of a bug here is "no worse
/// than before this patch existed", not "server can't accept joins".
/// </summary>
[HarmonyPatch(typeof(JWTUtility), nameof(JWTUtility.CreateFromString))]
public static class IdentityTokenClaimGuardPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref string rawData)
    {
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;
        if (string.IsNullOrEmpty(rawData)) return;

        try
        {
            var segments = rawData.Split('.');
            if (segments.Length < 2) return; // not shaped like a JWT - let the original method handle/fail normally

            var payload = JObject.Parse(DecodeSegment(segments[1]));

            var policy = payload["Policy"]?.Value<string>();
            if (string.IsNullOrEmpty(policy)) return; // nothing privileged claimed - nothing to guard

            var username = payload["Username"]?.Value<string>();
            if (Staff.IsStaff(username)) return; // trusted, leave the token as-is

            TavernAntiLogger.Warn(
                $"Stripping unverifiable Policy=\"{policy}\" claim from an identity token for username='{username ?? "<unknown>"}' " +
                "before it reaches any consumer - JWTUtility.CreateFromString never checks a signature, so this claim can't be " +
                "trusted without a staff role (admin/moderator/owner) in users.json");

            payload.Remove("Policy");
            segments[1] = EncodeSegment(payload.ToString(Formatting.None));
            rawData = string.Join(".", segments);
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Error while guarding identity token claims (leaving token untouched): {e}");
        }
    }

    // Mirrors JWTUtility.CreateFromString's own (non-URL-safe) base64 handling exactly, so the
    // rewritten token decodes identically through the original method afterward.
    private static string DecodeSegment(string segment)
    {
        var padded = segment.PadRight(4 * ((segment.Length + 3) / 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string EncodeSegment(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=');
    }
}
