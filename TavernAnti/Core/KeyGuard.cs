using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Servers;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using TavernAnti.Config;
using TavernLib.Backend.Api;
using TavernLib.Services;
using NetStream = Alta.Serialization.Stream;

namespace TavernAnti.Core;

internal static class KeyGuard
{
    private const double ChallengeTtl = 60;
    private const int ChallengesPerMinute = 6;
    private const int FramesPerTenSeconds = 40;

    private sealed class Peer
    {
        internal readonly KeyInbox Inbox = new();
        internal double SpawnedAt = -1;
        internal string Nonce;
        internal double NonceAt;
        internal int ChallengedId;
        internal string ChallengedName;
        internal readonly Queue<double> Challenges = new();
        internal readonly Queue<double> Frames = new();
        internal bool Proven;
        internal bool Reported;
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<Connection, Peer> Peers = new();
    private static Dictionary<string, string> _bindings;
    private static RsaPublic _serverKey;
    private static ISocket _socket;
    private static bool _stepAdded, _proofOff, _started, _keyLoading;
    private static string _proofOffWhy;
    private static double _nextAttach;

    private static double Now => Clock.Elapsed.TotalSeconds;

    private static string BindingsPath => Path.Combine(TavernAntiDirectories.TavernAntiRoot, "keyguard_bindings.json");
    private static string ServerKeyPath => Path.Combine(TavernAntiDirectories.TavernAntiRoot, "keyguard_server.priv.xml");

    internal static void Tick(AntiCheatConfig config, ViolationTracker tracker)
    {
        if (config == null || !config.KeyGuard) return;
        if (!_started) Start();
        if (!_stepAdded) AddStep();
        if (_proofOff) return;
        if (_serverKey == null && !_keyLoading) LoadServerKey();
        if (_socket == null && Now >= _nextAttach)
        {
            _nextAttach = Now + 1;
            Attach();
        }
        Deadlines(config, tracker);
    }

    private static void Start()
    {
        _started = true;
        if (MelonBase.RegisteredMelons.Any(m => string.Equals(m.Info?.Name, "TavernEssentials", StringComparison.OrdinalIgnoreCase)))
            _proofOffWhy = "TavernEssentials runs on this server and owns the key check on message type 35";
        else
            _proofOffWhy = KeyWire.MessageTypeWide();
        _proofOff = _proofOffWhy != null;
        if (!_proofOff) LoadBindings();
        TavernAntiLogger.Msg(_proofOff
            ? $"KeyGuard on: id check only, the key proof is off ({_proofOffWhy})"
            : "KeyGuard on: id check plus the TavernEssentials key proof on message type 35");
    }

    private static void AddStep()
    {
        var handler = ServerHandler.Current;
        if (handler == null) return;
        var field = AccessTools.Field(typeof(ServerHandler), "connectionSteps");
        if (field == null)
        {
            _stepAdded = true;
            TavernAntiLogger.Error("KeyGuard: this game build has no ServerHandler.connectionSteps, so the id check is off");
            return;
        }
        if (field.GetValue(handler) == null) return;
        handler.AddConnectionStep(IdStep);
        _stepAdded = true;
        if (Player.AllPlayers.Count > 0)
            TavernAntiLogger.Warn($"KeyGuard: {Player.AllPlayers.Count} player(s) joined before the id check was added");
    }

    private static Task IdStep(Connection connection, Action<string> progress)
    {
        try
        {
            var config = TavernServices.GetService<AntiCheatConfigFile>()?.LastRead;
            var tracker = TavernServices.GetService<ViolationTracker>();
            var login = connection?.UserInfo?.Username;
            if (config == null || tracker == null || !config.KeyGuard || login == null) return Task.FromResult(0);

            var id = connection.UserInfo.Identifier;
            var account = AccountId(login, out var why);
            if (account == null)
            {
                TavernAntiLogger.Warn($"KeyGuard: could not look up {login} in users.json ({why}); id {id} not checked");
                return Task.FromResult(0);
            }
            if (account.Value == id) return Task.FromResult(0);

            if (!tracker.ReportJoin(connection, login, ViolationType.KeyIdMismatch, $"joined as id {id}, but users.json gives {login} id {account.Value}"))
                return Task.FromResult(0);
            progress?.Invoke("TavernAnti: this is not your player id");
            throw new InvalidOperationException($"KeyGuard refused {login}: id {id} is not the account's id {account.Value}");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"KeyGuard id check failed: {e}");
            if (TavernServices.GetService<AntiCheatConfigFile>()?.LastRead?.Blocks == true)
                throw new InvalidOperationException("KeyGuard could not check the join", e);
            return Task.FromResult(0);
        }
    }

    private static int? AccountId(string login, out string why)
    {
        why = null;
        var users = TavernServices.GetService<TavernManager>()?.UserConfig;
        if (users == null) { why = "TavernLib's user config is not running"; return null; }
        var user = users.GetUser(login);
        if (user == null) { why = "not in users.json"; return null; }
        var raw = Convert.ToUInt64(user.UserId);
        if (raw > int.MaxValue) { why = $"user_id {raw} does not fit"; return null; }
        return (int)raw;
    }

    private static void LoadServerKey()
    {
        _keyLoading = true;
        var path = ServerKeyPath;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var rsa = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
                var created = false;
                if (File.Exists(path)) rsa.FromXmlString(File.ReadAllText(path));
                else
                {
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, rsa.ToXmlString(true));
                    File.Move(tmp, path);
                    created = true;
                }
                var p = rsa.ExportParameters(false);
                rsa.Clear();
                var key = RsaPublic.From(p.Modulus, p.Exponent, out var why) ?? throw new CryptographicException(why);
                _serverKey = key;
                TavernAntiLogger.Msg($"KeyGuard: server key {key.Short} {(created ? "created" : "loaded")}");
            }
            catch (Exception e)
            {
                _proofOff = true;
                _proofOffWhy = "the server key did not load";
                TavernAntiLogger.Error($"KeyGuard: the server key did not load ({e.Message}), so the key proof is off");
            }
        });
    }

    private static void Attach()
    {
        var socket = Socket.Current;
        if (socket == null || !socket.IsServer) return;
        _socket = socket;
        socket.ConnectionCreated += Register;
        socket.SocketDestroyed += Detach;
        foreach (var c in socket.Connections) Register(c);
    }

    private static void Detach(ISocket socket)
    {
        socket.ConnectionCreated -= Register;
        socket.SocketDestroyed -= Detach;
        Peers.Clear();
        _socket = null;
    }

    private static void Register(Connection c)
    {
        if (c == null || Peers.ContainsKey(c)) return;
        Peers[c] = new Peer();
        c.SetHandler((MessageType)KeyProto.WireType, Receive);
        c.Disconnected += conn => Peers.Remove(conn);
    }

    private static void Receive(Connection c, NetStream stream)
    {
        var framed = KeyWire.Read(stream, out var kind, out var msg, out var seq, out var fin, out var piece);
        if (!Peers.TryGetValue(c, out var p) || !framed) return;
        var now = Now;
        if (!Take(p.Frames, now, 10, FramesPerTenSeconds)) return;
        var whole = p.Inbox.Accept(msg, seq, fin, piece, now);
        if (whole == null) return;
        var body = KeyFields.Decode(whole);
        if (body == null) return;
        try
        {
            if (kind == KeyProto.Hello) OnHello(c, p, now);
            else if (kind == KeyProto.Proof) OnProof(c, p, body, now);
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"KeyGuard: handling a message from {Logins.Of(c)} failed: {e}");
        }
    }

    private static bool Take(Queue<double> times, double now, double window, int max)
    {
        while (times.Count > 0 && now - times.Peek() > window) times.Dequeue();
        if (times.Count >= max) return false;
        times.Enqueue(now);
        return true;
    }

    private static bool Ids(Connection c, out int id, out string name)
    {
        id = 0;
        name = null;
        var login = c.UserInfo;
        var player = c.Player;
        if (login == null || player?.UserInfo == null || player.UserInfo.Identifier != login.Identifier) return false;
        id = login.Identifier;
        name = login.Username;
        return true;
    }

    private static void OnHello(Connection c, Peer p, double now)
    {
        if (_serverKey == null || p.Proven || !Ids(c, out var id, out var name)) return;
        if (!Take(p.Challenges, now, 60, ChallengesPerMinute)) return;
        var again = p.Nonce != null && p.ChallengedId == id && p.ChallengedName == name && now - p.NonceAt <= ChallengeTtl;
        if (!again)
        {
            p.Nonce = RsaPublic.NewNonce();
            p.NonceAt = now;
            p.ChallengedId = id;
            p.ChallengedName = name;
        }
        KeyWire.Send(c, KeyProto.Challenge, new KeyFields()
            .Add("proto", KeyProto.Version)
            .Add("nonce", p.Nonce)
            .Add("srv_n", _serverKey.ModulusBase64)
            .Add("srv_e", _serverKey.ExponentBase64)
            .Add("id", id)
            .Add("name", name)
            .Add("ttl", (long)Math.Ceiling(ChallengeTtl - (now - p.NonceAt))));
    }

    private static void OnProof(Connection c, Peer p, KeyFields proof, double now)
    {
        var nonce = p.Nonce;
        p.Nonce = null;
        string why = null;
        RsaPublic key = null;
        if (nonce == null) why = "no-challenge";
        else if (proof.Get("nonce") != nonce) why = "stale";
        else if (now - p.NonceAt > ChallengeTtl) why = "expired";
        else if (!Ids(c, out var id, out _) || id != p.ChallengedId) why = "session-changed";
        else
        {
            key = RsaPublic.FromBase64(proof.Get("n"), proof.Get("e"), out var keyWhy);
            if (key == null) why = "bad-key";
            else
            {
                var sig = RsaPublic.FromBase64(proof.Get("sig"));
                if (sig == null || !key.Verify(KeyProto.Statement(_serverKey.Fingerprint, nonce, p.ChallengedId, p.ChallengedName), sig)) why = "bad-signature";
            }
            if (why != null && keyWhy != null) TavernAntiLogger.Warn($"KeyGuard: {p.ChallengedName} sent an unusable key ({keyWhy})");
        }

        if (why == "no-challenge" || why == "stale" || why == "expired" || why == "session-changed")
        {
            KeyWire.Send(c, KeyProto.Reject, new KeyFields().Add("why", why));
            return;
        }

        var login = p.ChallengedName;
        if (why != null)
        {
            KeyWire.Send(c, KeyProto.Reject, new KeyFields().Add("why", why));
            Fail(c, p, ViolationType.KeyMismatch, $"key proof refused ({why})");
            return;
        }

        var bindingKey = login.ToLowerInvariant();
        string bound;
        lock (_bindings) _bindings.TryGetValue(bindingKey, out bound);
        if (bound != null && bound != key.Fingerprint)
        {
            KeyWire.Send(c, KeyProto.Reject, new KeyFields().Add("why", "wrong-key"));
            Fail(c, p, ViolationType.KeyMismatch, $"proved key {Short(key.Fingerprint)}, but {login} is bound to key {Short(bound)}");
            return;
        }
        if (bound == null)
        {
            lock (_bindings) _bindings[bindingKey] = key.Fingerprint;
            SaveBindings();
            TavernAntiLogger.Msg($"KeyGuard: {login} bound to key {key.Short} (first proof)");
        }
        else TavernAntiLogger.Msg($"KeyGuard: {login} proved key {key.Short}");
        p.Proven = true;
        KeyWire.Send(c, KeyProto.Welcome, new KeyFields().Add("fp", key.Fingerprint));
    }

    private static void Deadlines(AntiCheatConfig config, ViolationTracker tracker)
    {
        if (_socket == null || tracker == null) return;
        var now = Now;
        foreach (var pair in Peers.ToList())
        {
            var c = pair.Key;
            var p = pair.Value;
            if (p.Proven || p.Reported || c.IsDisposed || c.Player == null) continue;
            if (p.SpawnedAt < 0)
            {
                p.SpawnedAt = now;
                continue;
            }
            if (now - p.SpawnedAt < config.KeyGuardProofSeconds) continue;
            Fail(c, p, ViolationType.KeyMissing, $"no key proof within {config.KeyGuardProofSeconds:F0} s (no TavernEssentials on their PC, or its Social is off)");
        }
    }

    private static void Fail(Connection c, Peer p, ViolationType type, string detail)
    {
        p.Reported = true;
        var player = c.Player;
        var tracker = TavernServices.GetService<ViolationTracker>();
        if (player == null || tracker == null) return;
        if (tracker.Report(player, type, detail) && !c.IsDisposed)
            EnforcementActions.Kick(c, Logins.NameOf(player), "TavernAnti could not confirm your identity key");
    }

    private static string Short(string fp) => string.IsNullOrEmpty(fp) || fp.Length <= 8 ? fp ?? "?" : fp.Substring(0, 8);

    private static void LoadBindings()
    {
        _bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(BindingsPath)) return;
            var read = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(BindingsPath));
            if (read != null)
                foreach (var kv in read) _bindings[kv.Key.ToLowerInvariant()] = kv.Value;
            TavernAntiLogger.Msg($"KeyGuard: {_bindings.Count} key binding(s) loaded");
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"KeyGuard: {BindingsPath} could not be read ({e.Message}); it is left alone");
            _proofOff = true;
            _proofOffWhy = "keyguard_bindings.json could not be read";
        }
    }

    private static void SaveBindings()
    {
        try
        {
            string json;
            lock (_bindings) json = JsonConvert.SerializeObject(_bindings, Formatting.Indented);
            var tmp = BindingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(BindingsPath)) File.Replace(tmp, BindingsPath, null);
            else File.Move(tmp, BindingsPath);
        }
        catch (Exception e)
        {
            TavernAntiLogger.Error($"KeyGuard: saving the key bindings failed: {e.Message}");
        }
    }
}
