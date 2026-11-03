using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using TavernAnti.Config;
using TavernLib.Services;

namespace TavernAnti.Core;

internal static class DiscordWebhook
{
    private const int MaxQueued = 20;
    private const int MaxLength = 1900;
    private const int GapMs = 1100;
    private static readonly string[] Hosts = ["discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com"];

    private static readonly Queue<(string Text, bool Test)> Pending = new();
    private static bool _pumping;
    private static int _dropped;
    private static string _badUrlLogged;
    private static bool _tlsSet;

    internal static string Problem(string url)
    {
        if (string.IsNullOrEmpty(url?.Trim())) return "no discord_webhook_url set";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "discord_webhook_url is not a URL";
        if (uri.Scheme != Uri.UriSchemeHttps) return "discord_webhook_url is not https";
        if (Array.IndexOf(Hosts, uri.Host.ToLowerInvariant()) < 0) return $"discord_webhook_url points at {uri.Host}, not Discord";
        if (!uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)) return "discord_webhook_url is not a webhook URL";
        return null;
    }

    internal static string Host(string url) =>
        Uri.TryCreate(url?.Trim() ?? "", UriKind.Absolute, out var uri) ? uri.Host : "?";

    private static AntiCheatConfig Config => TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;

    internal static void Post(string text)
    {
        var cfg = Config;
        if (cfg == null || !cfg.DiscordWebhookEnabled || string.IsNullOrEmpty(cfg.DiscordWebhookUrl?.Trim())) return;
        var problem = Problem(cfg.DiscordWebhookUrl);
        if (problem != null)
        {
            if (_badUrlLogged != problem)
            {
                _badUrlLogged = problem;
                TavernAntiLogger.Warn($"Discord webhook off: {problem}");
            }
            return;
        }
        Enqueue(text, false);
    }

    internal static string Test()
    {
        var cfg = Config;
        if (cfg == null) return "TavernAnti is not running in server mode";
        var problem = Problem(cfg.DiscordWebhookUrl);
        if (problem != null) return problem;
        Enqueue("TavernAnti webhook test", true);
        return $"Test message queued for {Host(cfg.DiscordWebhookUrl)}{(cfg.DiscordWebhookEnabled ? "" : " (alerts are off, the test goes out anyway)")}; Discord's answer goes to the server log";
    }

    private static void Enqueue(string text, bool test)
    {
        lock (Pending)
        {
            if (Pending.Count >= MaxQueued)
            {
                _dropped++;
                return;
            }
            Pending.Enqueue((Clip(text), test));
            if (_pumping) return;
            _pumping = true;
        }
        ThreadPool.QueueUserWorkItem(_ => Pump());
    }

    private static string Clip(string text)
    {
        text ??= "";
        return text.Length <= MaxLength ? text : text.Substring(0, MaxLength) + "...";
    }

    private static void Pump()
    {
        while (true)
        {
            (string Text, bool Test) next;
            int dropped;
            lock (Pending)
            {
                if (Pending.Count == 0)
                {
                    _pumping = false;
                    return;
                }
                next = Pending.Dequeue();
                dropped = _dropped;
                _dropped = 0;
            }

            var text = next.Text;
            if (dropped > 0) text += $"\n({dropped} earlier message(s) dropped, too many at once)";

            var url = Config?.DiscordWebhookUrl?.Trim();
            if (Problem(url) != null) continue;

            var wait = Send(url, text, next.Test);
            Thread.Sleep(Math.Max(GapMs, wait));
        }
    }

    private static int Send(string url, string text, bool test)
    {
        try
        {
            if (!_tlsSet)
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                _tlsSet = true;
            }
            var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["username"] = "TavernAnti",
                ["content"] = Escape(text),
                ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = new string[0] },
            }));
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
                if (test) TavernAntiLogger.Msg($"Discord webhook test: Discord answered {(int)response.StatusCode}, the webhook works");
            return 0;
        }
        catch (WebException e) when (e.Response is HttpWebResponse response)
        {
            var code = (int)response.StatusCode;
            var retry = 0;
            if (code == 429)
            {
                double.TryParse(response.Headers["Retry-After"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds);
                retry = (int)Math.Min(60000, Math.Max(1000, seconds * 1000));
            }
            response.Close();
            TavernAntiLogger.Warn(test
                ? $"Discord webhook test: Discord answered {code} (TLS works; {Meaning(code)})"
                : $"Discord webhook post failed: Discord answered {code} ({Meaning(code)})");
            return retry;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Warn($"Discord webhook {(test ? "test" : "post")} failed: {e.GetType().Name}: {Scrub(e.Message, url)}");
            return 0;
        }
    }

    private static string Meaning(int code) => code switch
    {
        401 or 403 or 404 => "the webhook URL is wrong or the webhook was deleted",
        429 => "rate limited, slowing down",
        _ => "unexpected answer",
    };

    private static string Scrub(string message, string url)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(url)) return message;
        message = message.Replace(url, "(webhook url)");
        var cut = url.IndexOf("/api/webhooks/", StringComparison.Ordinal);
        return cut < 0 ? message : message.Replace(url.Substring(cut), "(webhook path)");
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            if ("\\*_~`|>#[]()-".IndexOf(ch) >= 0) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
