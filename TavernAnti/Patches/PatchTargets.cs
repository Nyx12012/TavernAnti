using System;
using System.Reflection;
using HarmonyLib;

namespace TavernAnti.Patches;

internal static class PatchTargets
{
    internal static MethodInfo Resolve(string typeName, string name, Type[] args, string label, string what, ref bool logged) =>
        Resolve(AccessTools.TypeByName(typeName), name, args, label, what, ref logged);

    internal static MethodInfo Resolve(Type type, string name, Type[] args, string label, string what, ref bool logged)
    {
        var method = type == null ? null : AccessTools.Method(type, name, args);
        if (method == null && !logged)
        {
            logged = true;
            TavernAntiLogger.Error($"This game build has no {label}, so {what} is off");
        }
        return method;
    }
}
