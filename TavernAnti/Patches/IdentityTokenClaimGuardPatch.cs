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
            if (segments.Length < 2) return;

            var payload = JObject.Parse(DecodeSegment(segments[1]));

            var policy = payload["Policy"]?.Value<string>();
            if (string.IsNullOrEmpty(policy)) return;

            var username = payload["Username"]?.Value<string>();
            if (Staff.IsStaff(username)) return;

            TavernAntiLogger.Warn($"Stripped Policy=\"{policy}\" from the identity token of '{username ?? "<unknown>"}': token signatures are not checked, so only staff in users.json keep it");

            payload.Remove("Policy");
            segments[1] = EncodeSegment(payload.ToString(Formatting.None));
            rawData = string.Join(".", segments);
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"Error while guarding identity token claims (leaving token untouched): {e}");
        }
    }

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
