using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Alta.Api.DataTransferModels.Models.Shared;
using Alta.Api.DataTransferModels.Utility;
using Alta.Networking;
using HarmonyLib;
using TavernAnti.Core;
using TavernLib.Services;

namespace TavernAnti.Patches;

[HarmonyPatch(typeof(UserRolesUtility), nameof(UserRolesUtility.GetRolesFromIdentityToken), [typeof(JwtSecurityToken)])]
public static class DeveloperClaimGuardPatch
{
    [HarmonyPostfix]
    public static void Postfix(JwtSecurityToken identityToken, ref UserRoles __result)
    {
        if (!NetworkSceneManager.IsServer || NetworkSceneManager.IsLocalTest) return;
        if (__result is not { IsDeveloper: true }) return;

        var username = identityToken?.Claims?.FirstOrDefault(c => c.Type == "Username")?.Value;

        if (Staff.IsStaff(username)) return;

        TavernAntiLogger.Warn($"Dropped a developer role claimed by '{username ?? "<unknown>"}': token signatures are not checked, so only staff in users.json keep it");

        __result.IsDeveloper = false;
    }
}
