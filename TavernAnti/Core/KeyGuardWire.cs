using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Alta.Global;
using Alta.Networking;
using HarmonyLib;
using NetStream = Alta.Serialization.Stream;

namespace TavernAnti.Core;

internal static class KeyProto
{
    internal const int WireType = 35;
    internal const uint Version = 1;
    internal const uint Hello = 1;
    internal const uint Challenge = 2;
    internal const uint Proof = 3;
    internal const uint Welcome = 4;
    internal const uint Reject = 6;
    internal const string StatementTag = "TavernSocial-auth-v1";
    internal const int NonceBytes = 32;

    internal static byte[] Statement(string serverFingerprint, string nonce, int sessionId, string name) =>
        Encoding.UTF8.GetBytes(StatementTag + "\n" + serverFingerprint + "\n" + nonce + "\n"
            + sessionId.ToString(CultureInfo.InvariantCulture) + "\n" + name + "\n");
}

internal sealed class KeyFields
{
    private const int MaxLines = 64;
    private const int MaxKey = 24;
    private readonly List<KeyValuePair<string, string>> _items = new();

    internal KeyFields Add(string key, string value)
    {
        _items.Add(new KeyValuePair<string, string>(key, value ?? ""));
        return this;
    }

    internal KeyFields Add(string key, long value) => Add(key, value.ToString(CultureInfo.InvariantCulture));

    internal string Get(string key)
    {
        foreach (var item in _items)
            if (item.Key == key) return item.Value;
        return null;
    }

    internal string Encode()
    {
        var sb = new StringBuilder();
        foreach (var item in _items)
        {
            sb.Append(item.Key).Append('=');
            foreach (var c in item.Value)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    internal static KeyFields Decode(string text)
    {
        if (text == null) return null;
        var f = new KeyFields();
        var at = 0;
        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            if (end < 0 || f._items.Count >= MaxLines) return null;
            var eq = text.IndexOf('=', at, end - at);
            if (eq <= at || eq - at > MaxKey) return null;
            var key = text.Substring(at, eq - at);
            foreach (var c in key)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return null;
            var value = Unescape(text, eq + 1, end);
            if (value == null) return null;
            f._items.Add(new KeyValuePair<string, string>(key, value));
            at = end + 1;
        }
        return f;
    }

    private static string Unescape(string text, int from, int to)
    {
        var sb = new StringBuilder(to - from);
        for (var i = from; i < to; i++)
        {
            var c = text[i];
            if (c < 0x20 || c > 0x7E) return null;
            if (c != '\\') { sb.Append(c); continue; }
            if (i + 1 >= to) return null;
            var n = text[++i];
            if (n == '\\') sb.Append('\\');
            else if (n == 'n') sb.Append('\n');
            else if (n == 'r') sb.Append('\r');
            else if (n == 'u')
            {
                if (i + 4 >= to || !int.TryParse(text.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) return null;
                sb.Append((char)code);
                i += 4;
            }
            else return null;
        }
        return sb.ToString();
    }
}

internal sealed class KeyInbox
{
    private const int ChunkChars = 800;
    private const int MaxChunks = 8;
    private const double StaleSeconds = 10;

    private readonly StringBuilder _buf = new();
    private bool _open;
    private uint _msg, _next;
    private double _lastAt;

    internal string Accept(uint msg, uint seq, bool fin, string piece, double now)
    {
        piece ??= "";
        if (_open && now - _lastAt > StaleSeconds) Drop();
        if (piece.Length > ChunkChars) { Drop(); return null; }
        if (seq == 0)
        {
            Drop();
            _open = true;
            _msg = msg;
        }
        else if (!_open || msg != _msg || seq != _next)
        {
            Drop();
            return null;
        }
        if (seq >= MaxChunks) { Drop(); return null; }
        _buf.Append(piece);
        _next = seq + 1;
        _lastAt = now;
        if (!fin) return null;
        var whole = _buf.ToString();
        Drop();
        return whole;
    }

    private void Drop()
    {
        _open = false;
        _next = 0;
        _buf.Length = 0;
    }
}

internal static class KeyWire
{
    private const int ChunkChars = 800;
    private static ConnectionChannel _channel;
    private static bool _channelResolved;
    private static uint _nextMsg;

    private static void Codec(NetStream s, ref uint ver, ref uint kind, ref uint msg, ref uint seq, ref uint fin, ref string payload)
    {
        s.SerializeBits(ref ver, 8);
        s.SerializeBits(ref kind, 8);
        s.SerializeBits(ref msg, 16);
        s.SerializeBits(ref seq, 8);
        s.SerializeBits(ref fin, 8);
        s.SerializeString(ref payload, NetStream.StringEncoding.UTF8);
    }

    internal static bool Read(NetStream s, out uint kind, out uint msg, out uint seq, out bool fin, out string payload)
    {
        uint ver = 0, k = 0, m = 0, sq = 0, f = 0;
        string p = null;
        kind = msg = seq = 0;
        fin = false;
        payload = null;
        try
        {
            Codec(s, ref ver, ref k, ref m, ref sq, ref f, ref p);
        }
        catch (Exception)
        {
            return false;
        }
        if (ver != KeyProto.Version) return false;
        kind = k;
        msg = m;
        seq = sq;
        fin = f == 1;
        payload = p ?? "";
        return true;
    }

    internal static void Send(Connection connection, uint kind, KeyFields body)
    {
        if (connection == null || connection.IsDisposed) return;
        var text = body.Encode();
        ResolveChannel();
        _nextMsg = (_nextMsg + 1) & 0xFFFF;
        var count = Math.Max(1, (text.Length + ChunkChars - 1) / ChunkChars);
        for (var i = 0; i < count; i++)
        {
            uint ver = KeyProto.Version, k = kind, m = _nextMsg, seq = (uint)i, fin = i == count - 1 ? 1u : 0u;
            var piece = text.Substring(i * ChunkChars, Math.Min(ChunkChars, text.Length - i * ChunkChars));
            try
            {
                if (!connection.Send(_channel, (MessageType)KeyProto.WireType, (c, s) => Codec(s, ref ver, ref k, ref m, ref seq, ref fin, ref piece))) return;
            }
            catch (Exception e)
            {
                TavernAntiLogger.Warn($"KeyGuard: sending kind {kind} failed: {e.Message}");
                return;
            }
        }
    }

    private static void ResolveChannel()
    {
        if (_channelResolved) return;
        _channelResolved = true;
        try
        {
            ConnectionChannel best = null;
            foreach (var ch in GlobalSettings<NetworkingSettings>.Instance.Channels)
                if (ch.IsReliable && (best == null || ch.MaximumSendInterval < best.MaximumSendInterval)) best = ch;
            _channel = best;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Warn($"KeyGuard: no reliable channel found, using the default: {e.Message}");
        }
    }

    internal static string MessageTypeWide()
    {
        try
        {
            var reader = AccessTools.TypeByName("Alta.Networking.Internal.MessageProcessor");
            var writer = AccessTools.TypeByName("Alta.Networking.Internal.OutgoingPacketManager");
            var read = reader == null ? null : AccessTools.Method(reader, "ProcessSingleMessageFromData",
                [typeof(Connection), typeof(ArraySegment<byte>), typeof(ConnectionChannel), typeof(MessageType).MakeByRefType()]);
            var write = writer == null ? null : AccessTools.Method(writer, "StartSerialize", [typeof(Connection), typeof(MessageType)]);
            if (read == null || write == null) return $"the game's message {(read == null ? "reader" : "writer")} was not found";
            if (!HasPrefix(read) || !HasPrefix(write)) return "TavernLib's 8 bit message type patch is missing";
            return null;
        }
        catch (Exception e)
        {
            return $"the message type check failed: {e.Message}";
        }
    }

    private static bool HasPrefix(MethodBase m) => HarmonyLib.Harmony.GetPatchInfo(m)?.Prefixes.Count > 0;
}

internal sealed class RsaPublic
{
    private const int MinBits = 2048;
    private const int MaxBits = 4096;
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    private readonly byte[] _n, _e;
    internal readonly string Fingerprint;

    private RsaPublic(byte[] n, byte[] e)
    {
        _n = n;
        _e = e;
        Fingerprint = ToBase64Url(Hash(Encoding.ASCII.GetBytes("tsk1:" + Convert.ToBase64String(n) + ":" + Convert.ToBase64String(e))));
    }

    internal string ModulusBase64 => Convert.ToBase64String(_n);
    internal string ExponentBase64 => Convert.ToBase64String(_e);
    internal string Short => Fingerprint.Length <= 8 ? Fingerprint : Fingerprint.Substring(0, 8);

    internal static RsaPublic FromBase64(string n, string e, out string why)
    {
        var nb = FromBase64(n);
        var eb = FromBase64(e);
        if (nb == null || eb == null) { why = "not base64"; return null; }
        return From(nb, eb, out why);
    }

    internal static RsaPublic From(byte[] n, byte[] e, out string why)
    {
        n = Strip(n);
        e = Strip(e);
        if (n.Length == 0 || e.Length == 0) { why = "empty"; return null; }
        var bits = (n.Length - 1) * 8;
        for (var b = n[0]; b != 0; b >>= 1) bits++;
        if (bits < MinBits || bits > MaxBits) { why = $"{bits} bit key"; return null; }
        if ((n[n.Length - 1] & 1) == 0) { why = "even modulus"; return null; }
        if (e.Length > 4 || (e[e.Length - 1] & 1) == 0 || (e.Length == 1 && e[0] < 3)) { why = "bad exponent"; return null; }
        why = null;
        return new RsaPublic(n, e);
    }

    internal bool Verify(byte[] data, byte[] signature)
    {
        if (data == null || signature == null || signature.Length != _n.Length) return false;
        var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false };
        try
        {
            rsa.ImportParameters(new RSAParameters { Modulus = (byte[])_n.Clone(), Exponent = (byte[])_e.Clone() });
            return rsa.VerifyHash(Hash(data), Sha256Oid, signature);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            rsa.Clear();
        }
    }

    internal static byte[] Hash(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    internal static string NewNonce()
    {
        var b = new byte[KeyProto.NonceBytes];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
        return ToBase64Url(b);
    }

    private static byte[] Strip(byte[] b)
    {
        if (b == null) return [];
        var i = 0;
        while (i < b.Length && b[i] == 0) i++;
        if (i == 0) return b;
        var r = new byte[b.Length - i];
        Buffer.BlockCopy(b, i, r, 0, r.Length);
        return r;
    }

    private static string ToBase64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
