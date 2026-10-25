using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Distraint.Core
{
    /// <summary>
    /// Local-network sync of study time with other devices (the phone).
    ///   - Listens on TCP port 47653 for one-line JSON messages, and announces itself by UDP broadcast (port 47654)
    ///     so the phone can find this PC.
    ///   - "pair": while the pairing window is open (2 minutes, 5 tries) the phone sends the 6-digit code and receives the
    ///     shared secret, masked with the code.
    ///   - "sync": every message is signed (HMAC-SHA256) with the shared secret and carries a timestamp (5 minute window).
    ///     The reply carries this device's own numbers, so one round trip updates both sides.
    /// Each device only ever reports ITS OWN study seconds for the day; the goal check adds them up (see StudyState).
    /// StudyState is not thread-safe, so every access to it goes through the `ui` callback (the UI thread).
    /// </summary>
    public sealed class SyncService
    {
        public const int Port = 47653, BeaconPort = 47654;
        private const int MaxLine = 4096;

        private readonly StudyState state;
        private readonly Action<Action> ui;                  // runs an action on the UI thread and waits for it
        private TcpListener listener;
        private CancellationTokenSource cts;
        private readonly object pairLock = new object();
        private string pairCode;
        private DateTime pairUntil;
        private int pairAttempts;

        /// <summary>Raised (on the UI thread) when a device has just paired.</summary>
        public event Action Paired;

        public SyncService(StudyState state, Action<Action> runOnUi) { this.state = state; ui = runOnUi; }

        public bool IsRunning => cts != null;

        public void Start()
        {
            if (cts != null) return;
            try { listener = new TcpListener(IPAddress.Any, Port); listener.Start(); }
            catch { listener = null; return; }                // port busy: sync stays off
            cts = new CancellationTokenSource();
            CancellationToken t = cts.Token;
            Task.Run(() => AcceptLoop(t));
            Task.Run(() => BeaconLoop(t));
        }

        public void Stop()
        {
            cts?.Cancel();
            try { listener?.Stop(); } catch { }
            cts = null;
            listener = null;
            CancelPairing();
        }

        // ---------------- pairing ----------------
        /// <summary>Opens the pairing window and returns the 6-digit code to type on the phone.</summary>
        public string StartPairing()
        {
            lock (pairLock)
            {
                pairCode = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
                pairUntil = DateTime.UtcNow.AddMinutes(2);
                pairAttempts = 0;
                return pairCode;
            }
        }

        public void CancelPairing() { lock (pairLock) { pairCode = null; } }

        public int PairSecondsLeft()
        {
            lock (pairLock) return pairCode == null ? 0 : Math.Max(0, (int)(pairUntil - DateTime.UtcNow).TotalSeconds);
        }

        /// <summary>This PC's IPv4 addresses, for typing on the phone if automatic discovery doesn't find it.</summary>
        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
                }
            }
            catch { }
            return list;
        }

        // ---------------- network loops ----------------
        private async Task AcceptLoop(CancellationToken t)
        {
            while (!t.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(t); }
                catch { break; }
                _ = Task.Run(() => Handle(c));
            }
        }

        private async Task BeaconLoop(CancellationToken t)
        {
            try
            {
                using (var udp = new UdpClient { EnableBroadcast = true })
                {
                    while (!t.IsCancellationRequested)
                    {
                        string id = Ui(() => state.DeviceId), name = Ui(() => state.DeviceName);
                        byte[] data = Encoding.UTF8.GetBytes("distraint|" + id + "|" + name + "|" + Port);
                        try { await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, BeaconPort)); } catch { }
                        await Task.Delay(5000, t);
                    }
                }
            }
            catch { }
        }

        private async Task Handle(TcpClient c)
        {
            using (c)
            using (var timeout = new CancellationTokenSource(5000))
            {
                try
                {
                    NetworkStream ns = c.GetStream();
                    string line = await ReadLine(ns, timeout.Token);
                    if (line == null) return;
                    string reply = Process(line);
                    if (reply == null) return;
                    byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    await ns.WriteAsync(bytes, 0, bytes.Length, timeout.Token);
                }
                catch { /* bad or slow client: just drop it */ }
            }
        }

        /// <summary>Reads up to the first newline, refusing anything longer than MaxLine bytes.</summary>
        private static async Task<string> ReadLine(NetworkStream ns, CancellationToken t)
        {
            var buf = new List<byte>();
            var one = new byte[1];
            while (buf.Count < MaxLine)
            {
                int n = await ns.ReadAsync(one, 0, 1, t);
                if (n == 0) break;
                if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
                buf.Add(one[0]);
            }
            return null;
        }

        // ---------------- messages ----------------
        private T Ui<T>(Func<T> f) { T r = default(T); ui(() => r = f()); return r; }

        private static long NowSec() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static string Sign(byte[] key, params object[] parts) =>
            Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join("|", parts))));

        private string Process(string line)
        {
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                long L(string k) => r.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

                switch (S("type"))
                {
                    case "pair": return HandlePair(S("code"), S("nonce"), S("id"), S("name"));
                    case "sync": return HandleSync(S("id"), S("name"), S("day"), L("sec"), (int)L("reset"), L("ts"), S("sig"));
                    default: return null;
                }
            }
        }

        private string HandlePair(string code, string nonceB64, string id, string name)
        {
            if (code == null || nonceB64 == null || id == null) return Fail("pair");
            bool ok;
            lock (pairLock)
            {
                if (pairCode == null || DateTime.UtcNow > pairUntil || pairAttempts >= 5) return Fail("pair");
                pairAttempts++;
                ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(pairCode));
                if (ok) pairCode = null;                       // single use
            }
            if (!ok) return Fail("pair");

            byte[] secret = Convert.FromBase64String(Ui(() => state.EnsureSecret()));
            byte[] mask = HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Convert.FromBase64String(nonceB64));
            var masked = new byte[secret.Length];
            for (int i = 0; i < secret.Length; i++) masked[i] = (byte)(secret[i] ^ mask[i]);

            string myId = Ui(() => state.DeviceId), myName = Ui(() => state.DeviceName);
            ui(() => { state.AddPeer(id, name ?? "Device"); Paired?.Invoke(); });
            return JsonSerializer.Serialize(new { type = "pair", ok = true, id = myId, name = myName, secret = Convert.ToBase64String(masked) });
        }

        private string HandleSync(string id, string name, string day, long sec, int reset, long ts, string sig)
        {
            string secretB64 = Ui(() => state.Secret);
            if (secretB64 == null || id == null || day == null || sig == null) return Fail("sync");
            byte[] key = Convert.FromBase64String(secretB64);

            if (Math.Abs(NowSec() - ts) > 300) return Fail("sync");               // stale or replayed
            string expect = Sign(key, id, name, day, sec, reset, ts);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expect), Encoding.UTF8.GetBytes(sig))) return Fail("sync");

            ui(() => state.MergePeer(id, name ?? "Device", day, Math.Max(0, sec), reset));

            // Reply with this device's own numbers.
            var me = Ui(() => (id: state.DeviceId, name: state.DeviceName, day: state.CurrentDayKey(),
                               sec: state.LocalStudiedSec(), reset: state.RolloverHour));
            long now = NowSec();
            string mySig = Sign(key, me.id, me.name, me.day, me.sec, me.reset, now);
            return JsonSerializer.Serialize(new { type = "sync", ok = true, id = me.id, name = me.name, day = me.day, sec = me.sec, reset = me.reset, ts = now, sig = mySig });
        }

        private static string Fail(string type) => JsonSerializer.Serialize(new { type, ok = false });
    }
}
