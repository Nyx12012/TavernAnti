using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Alta.Api.DataTransferModels.Models.Shared;
using Alta.Api.DataTransferModels.Utility;
using Alta.Networking;
using HarmonyLib;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Patches;

/// <summary>
/// Closes the "set IsDeveloper to true" exploit (ATownshipTale_Decompiled): a JWT identity
/// token carrying a "Policy":"dev" claim makes UserRolesUtility.GetRolesFromIdentityToken
/// return IsDeveloper=true, which server-side join logic then treats as a real elevated role
/// (see ServerPlayerConnectionHandlerOld.CheckIfPlayerIsAllowed/CheckIfPlayerIsAllowedCustom -
/// both deny non-VR PlayerModes with "You will need a VR headset to play" UNLESS the joining
/// player's MemberStatus is >= Developer).
///
/// Root cause (see IdentityTokenClaimGuardPatch's doc comment for the full trace):
/// JWTUtility.CreateFromString - the sole decode path every identity token in the codebase
/// goes through - never validates a signature at all. It just base64-decodes the payload
/// segment and parses it as JSON; the `includeRawData` boolean only controls whether an extra
/// "raw" claim gets appended, nothing to do with validation. Any player can hand-craft a token
/// with any claims they want.
///
/// This patch treats a claimed developer role the same way CommandPermissionPatch treats an
/// unattributed networked command: fail closed unless the claiming user has a staff role (admin/moderator/owner) in
/// users.json's roles array (see Core.Staff - reuses TavernLib's own trust
/// store instead of a separate TavernAnti-owned allow-list). In practice
/// IdentityTokenClaimGuardPatch already strips an unverifiable "Policy" claim from the raw token
/// string before this method ever runs, so this postfix rarely has anything left to do - it's
/// kept as defense-in-depth in case some other path constructs a JwtSecurityToken without going
/// through JWTUtility.
/// </summary>
[HarmonyPatch(typeof(UserRolesUtility), nameof(UserRolesUtility.GetRolesFromIdentityToken), [typeof(JwtSecurityToken)])]
public static class DeveloperClaimGuardPatch
{
    [HarmonyPostfix]
    public static void Postfix(JwtSecurityToken identityToken, ref UserRoles __result)
    {
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;
        if (__result is not { IsDeveloper: true }) return;

        var username = identityToken?.Claims?.FirstOrDefault(c => c.Type == "Username")?.Value;

        if (Staff.IsStaff(username)) return; // trusted, claim stands

        TavernAntiLogger.Warn(
            $"Downgrading unverifiable developer-role claim for username='{username ?? "<unknown>"}' - " +
            "identity token signatures aren't meaningfully checked post-shutdown, so a claimed " +
            "\"Policy\":\"dev\" can't be trusted without a staff role (admin/moderator/owner) in users.json");

        __result.IsDeveloper = false;
    }
}
