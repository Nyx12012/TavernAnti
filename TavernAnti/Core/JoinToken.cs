using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using Alta.Api.DataTransferModels.Converters;

namespace TavernAnti.Core;

internal sealed class JoinToken
{
    private static readonly string[] RolePolicies = ["dev", "mod", "supporter"];

    internal int UserId;
    internal string Username;
    internal readonly List<string> Roles = new();
    internal string Problem;

    internal static JoinToken Read(string token)
    {
        var read = new JoinToken();
        if (string.IsNullOrEmpty(token))
        {
            read.Problem = "that is empty";
            return read;
        }

        string uid = null;
        int uids = 0, names = 0;
        try
        {
            foreach (Claim c in JWTUtility.CreateFromString(token, false).Claims)
            {
                if (c.Type == "UserId") { uids++; uid = c.Value; }
                else if (c.Type == "Username") { names++; read.Username = c.Value; }
                else if (c.Type == "Policy") read.AddRoles(c.Value);
            }
        }
        catch (Exception)
        {
            read.Problem = "that cannot be read";
            return read;
        }

        if (uids != 1) read.Problem = $"that has {uids} UserId claims";
        else if (names != 1) read.Problem = $"that has {names} Username claims";
        else if (!int.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out read.UserId)) read.Problem = "whose UserId is not a number";
        return read;
    }

    private void AddRoles(string policy)
    {
        if (policy == null) return;
        foreach (var part in policy.Split(','))
        {
            var p = part.Trim();
            if (Array.IndexOf(RolePolicies, p) >= 0 && !Roles.Contains(p)) Roles.Add(p);
        }
    }
}
