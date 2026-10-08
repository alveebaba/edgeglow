using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace EdgeGlow
{
    public sealed class DeviceInfo
    {
        public string Ip, Id, Sku;
        public DateTime LastSeenUtc;
        public DateTime LastStatusUtc;
        public bool? PowerOn;
        public int? Brightness;
        internal int IdentityGeneration;
        internal DeviceInfo Copy()
        {
            return new DeviceInfo { Ip = Ip, Id = Id, Sku = Sku, LastSeenUtc = LastSeenUtc,
                LastStatusUtc = LastStatusUtc, PowerOn = PowerOn, Brightness = Brightness,
                IdentityGeneration = IdentityGeneration };
        }
    }

    // A command's destination and observed state are fixed before it waits for the
    // transport. Returning Snapshot never exposes the token's private mutable copy.
    public sealed class EndpointToken
    {
        private readonly DeviceInfo snapshot;
        private readonly int generation;
        public EndpointToken(DeviceInfo device, int transportGeneration)
        {
            if (device == null) throw new ArgumentNullException("device");
            snapshot = device.Copy(); generation = transportGeneration;
        }
        public string Id { get { return snapshot.Id; } }
        public string Ip { get { return snapshot.Ip; } }
        public string Sku { get { return snapshot.Sku; } }
        public int Generation { get { return generation; } }
        public DeviceInfo Snapshot { get { return snapshot.Copy(); } }
    }

    // The internal boundary lets tests run the production parser and socket lifecycle
    // against recording/in-memory sockets without opening any network interface.
    internal sealed class LanInterface
    {
        public string Id;
        public IPAddress Address;
        public byte[] Mask;
        public IPAddress Broadcast;
        public string Route = "";
        public string Key { get { return Id + "|" + Address + "|" + BitConverter.ToString(Mask) + "|" + Route; } }
    }

    internal interface ILanDatagramSocket : IDisposable
    {
        void Send(byte[] bytes, IPEndPoint destination);
        byte[] Receive(out IPEndPoint source);
        void Join(LanInterface local);
        void ConfigureMulticast(LanInterface local);
    }

    internal sealed class UdpLanSocket : ILanDatagramSocket
    {
        private readonly UdpClient client;
        public UdpLanSocket(bool replySocket)
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                client.Client.SendTimeout = 1000;
                if (replySocket)
                {
                    client.Client.ExclusiveAddressUse = true;
                    client.Client.ReceiveTimeout = 250;
                    client.Client.Bind(new IPEndPoint(IPAddress.Any, 4002));
                    client.EnableBroadcast = true;
                    client.MulticastLoopback = false;
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                }
            }
            catch (SocketException ex)
            {
                client.Close();
                if (replySocket) throw new IOException("Cannot bind UDP 0.0.0.0:4002 (" + ex.SocketErrorCode +
                    ", WinSock " + ex.ErrorCode + "). Another application may own the Govee reply port.", ex);
                throw;
            }
            catch { client.Close(); throw; }
        }
        public void Send(byte[] bytes, IPEndPoint destination) { client.Send(bytes, bytes.Length, destination); }
        public byte[] Receive(out IPEndPoint source)
        {
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] bytes = client.Receive(ref endpoint);
            source = endpoint; return bytes;
        }
        public void Join(LanInterface local) { client.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), local.Address); }
        public void ConfigureMulticast(LanInterface local)
        {
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
        }
        public void Dispose() { client.Close(); }
    }

    public sealed class GoveeLan : IDisposable
    {
        public const string EnablePacket = "uwABsQEK";
        public const string DisablePacket = "uwABsQAL";
        private const int MaximumDatagram = 16384;
        private static readonly byte[] ScanRequest = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}");
        private readonly object gate = new object(), sendGate = new object(), standbyGate = new object();
        private readonly ManualResetEvent stopping = new ManualResetEvent(false);
        private readonly Func<bool, ILanDatagramSocket> socketFactory;
        private readonly Func<LanInterface[]> interfaceProvider;
        private readonly Func<DateTime> utcNow;
        private readonly Action<ILanDatagramSocket, int, DeviceInfo> receiveProcessed;
        private readonly Dictionary<string, DeviceInfo> devices = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SessionState> sessions = new Dictionary<string, SessionState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StandbyState> standby = new Dictionary<string, StandbyState>(StringComparer.OrdinalIgnoreCase);
        private long standbyVersion;
        private readonly List<string> conflictAudit = new List<string>();
        private long observationSequence;
        private readonly HashSet<string> joined = new HashSet<string>(StringComparer.Ordinal);
        private ILanDatagramSocket receiver, sender;
        private Thread receiveThread;
        private LanInterface[] locals = new LanInterface[0];
        private DeviceInfo target;
        private bool started, disposed;
        private int generation, discoveryBusy, backgroundWorkers;
        private bool stopHandleClosed;
        private DateTime retryAt, interfaceCheckAt;
        private string lastError = "";

        private sealed class SessionState
        {
            public DeviceInfo Target;
            public bool? OriginalPower;
            public int? OriginalBrightness;
            public bool ChangedPower;
            public DateTime PowerCommandStatusUtc;
            public bool ConflictBlocked;
            public long LastConflictSequence;
            public string LastConflictingId;
        }

        // Temporary monitor-follow power changes are separate from the original
        // session baseline. Identity, rather than an address, carries them through
        // DHCP; a current validated token is still required for every command.
        private sealed class StandbyState
        {
            public bool? OriginalPower;
            public int? OriginalBrightness;
            public bool ChangedPower;
            public DateTime PowerCommandStatusUtc;
        }

        public GoveeLan() : this(delegate(bool reply) { return new UdpLanSocket(reply); }, GetLocalInterfaces, delegate { return DateTime.UtcNow; }) { }
        internal GoveeLan(Func<bool, ILanDatagramSocket> sockets, Func<LanInterface[]> interfaces, Func<DateTime> clock)
            : this(sockets, interfaces, clock, null) { }
        internal GoveeLan(Func<bool, ILanDatagramSocket> sockets, Func<LanInterface[]> interfaces, Func<DateTime> clock,
            Action<ILanDatagramSocket, int, DeviceInfo> processingObserver)
        {
            if (sockets == null || interfaces == null || clock == null) throw new ArgumentNullException("transport dependencies");
            socketFactory = sockets; interfaceProvider = interfaces; utcNow = clock; receiveProcessed = processingObserver;
        }

        public event Action<DeviceInfo> DeviceSeen;
        public event Action<string> Error;
        public int Generation { get { lock (gate) return generation; } }
        public DateTime LastSeenUtc { get { lock (gate) return target == null ? DateTime.MinValue : target.LastSeenUtc; } }
        public DeviceInfo CurrentTarget { get { lock (gate) return target == null ? null : target.Copy(); } }
        public EndpointToken CurrentEndpoint { get { lock (gate) return target == null ? null : new EndpointToken(target, generation); } }
        internal string[] ConflictAudit { get { lock (gate) return conflictAudit.ToArray(); } }

        public void Start()
        {
            lock (sendGate)
            {
                lock (gate) { ThrowIfDisposed(); if (started) return; }
                RebuildTransport(interfaceProvider());
                lock (gate)
                {
                    ThrowIfDisposed();
                    started = true;
                    receiveThread = new Thread(ReceiveLoop);
                    receiveThread.IsBackground = true;
                    receiveThread.Name = "EdgeGlow LAN replies";
                    Interlocked.Increment(ref backgroundWorkers);
                    try { receiveThread.Start(); }
                    catch { started = false; receiveThread = null; BackgroundCompleted(); throw; }
                }
            }
        }

        public void Discover()
        {
            lock (gate)
            {
                ThrowIfDisposed(); EnsureStarted();
                if (Interlocked.Exchange(ref discoveryBusy, 1) != 0) return;
                Interlocked.Increment(ref backgroundWorkers);
            }
            bool queued = false;
            try { queued = ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    MaintainTransport(true);
                    for (int attempt = 0; attempt < 3 && !stopping.WaitOne(0); attempt++)
                    {
                        lock (sendGate)
                        {
                            ILanDatagramSocket scan; LanInterface[] interfaces;
                            lock (gate) { if (disposed) return; scan = receiver; interfaces = locals; }
                            if (scan == null) return;
                            foreach (LanInterface local in interfaces)
                            {
                                scan.ConfigureMulticast(local);
                                scan.Send(ScanRequest, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 4001));
                                scan.Send(ScanRequest, new IPEndPoint(local.Broadcast, 4001));
                            }
                        }
                        if (attempt < 2 && stopping.WaitOne(750)) return;
                    }
                }
                catch (SocketException ex) { FaultTransport("LAN discovery socket failed: " + ex.SocketErrorCode); }
                catch (ObjectDisposedException) { if (!stopping.WaitOne(0)) FaultTransport("LAN discovery socket was closed unexpectedly."); }
                catch (Exception ex) { ReportError("LAN discovery failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref discoveryBusy, 0); BackgroundCompleted(); }
            }); }
            finally
            {
                if (!queued) { Interlocked.Exchange(ref discoveryBusy, 0); BackgroundCompleted(); }
            }
        }

        public void SetTarget(DeviceInfo device)
        {
            ValidateDevice(device);
            lock (sendGate)
            lock (gate)
            {
                ThrowIfDisposed(); EnsureStarted();
                DeviceInfo observed;
                if (!devices.TryGetValue(device.Id, out observed) || observed.Ip != device.Ip || observed.IdentityGeneration != generation)
                    throw new InvalidOperationException("A current matching H6609 scan must establish this device and address first.");
                if (!IsLocalSource(IPAddress.Parse(observed.Ip), locals))
                    throw new InvalidOperationException("The selected device is not on an active physical local subnet.");
                target = observed.Copy();
            }
        }

        // Snapshot only. Repeating preparation never overwrites the original session
        // state. Unknown/stale fields remain unknown, and brightness is never changed.
        public void PrepareSession() { PrepareSession(RequireCurrentEndpoint()); }
        public void PrepareSession(EndpointToken expected)
        {
            lock (sendGate) lock (gate)
            { ValidateControlToken(expected); PrepareSessionLocked(expected.Snapshot); }
        }

        private SessionState PrepareSessionLocked(DeviceInfo device)
        {
            lock (gate)
            {
                string key = SessionKey(device); SessionState session;
                if (sessions.TryGetValue(key, out session)) return session;
                bool fresh = IsFreshStatus(device, utcNow());
                session = new SessionState { Target = device.Copy(),
                    OriginalPower = fresh ? device.PowerOn : null,
                    OriginalBrightness = fresh ? device.Brightness : null };
                sessions.Add(key, session); return session;
            }
        }

        public void Enable() { Enable(RequireCurrentEndpoint(), null); }
        public void Enable(EndpointToken expected, Func<bool> authorize)
        {
            lock (sendGate)
            {
                SessionState session = null;
                // Do not retain a snapshot for an operation canceled before its first
                // possible datagram. Once attempted, the snapshot survives failure.
                SendControlLocked(RazerJson(EnablePacket), expected, authorize, false,
                    delegate { session = PrepareSessionLocked(expected.Snapshot); });
                DeviceInfo device = expected.Snapshot;
                if (session.OriginalPower == false && device.PowerOn == false && IsFreshStatus(device, utcNow()) &&
                    device.LastStatusUtc > session.PowerCommandStatusUtc)
                {
                    SendControlLocked("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}", expected, authorize, false, delegate
                    {
                        // Failed sends may have reached the device. The obligation
                        // must precede the attempt and survive a following failure.
                        session.ChangedPower = true;
                        session.PowerCommandStatusUtc = device.LastStatusUtc;
                    });
                }
            }
        }

        public void EnableStream() { EnableStream(RequireCurrentEndpoint(), null); }
        public void EnableStream(EndpointToken expected, Func<bool> authorize)
        {
            lock (sendGate)
            {
                SendControlLocked(RazerJson(EnablePacket), expected, authorize, false,
                    delegate { PrepareSessionLocked(expected.Snapshot); });
            }
        }

        public void Standby(EndpointToken expected, Func<bool> authorize)
        {
            lock (sendGate)
            {
                long version;
                lock (gate) { ValidateStandbyToken(expected); lock (standbyGate) version = ++standbyVersion; }
                StandbyState intent = null;
                SendControlLocked("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}", expected,
                    delegate { return AuthorizeStandby(expected, version, null, authorize); }, false, delegate
                    {
                        DeviceInfo device = expected.Snapshot;
                        lock (standbyGate)
                        {
                            if (version != standbyVersion) throw new OperationCanceledException("Automatic standby was canceled.");
                            if (!standby.TryGetValue(device.Id, out intent))
                            {
                                SessionState original;
                                if (!sessions.TryGetValue(SessionKey(device), out original))
                                {
                                    // The selected, freshly scanned identity can have
                                    // moved before its old endpoint is cleaned up.
                                    foreach (SessionState prior in sessions.Values)
                                        if (String.Equals(prior.Target.Id, device.Id, StringComparison.OrdinalIgnoreCase) &&
                                            (original == null || prior.Target.LastSeenUtc > original.Target.LastSeenUtc)) original = prior;
                                    if (original == null) throw new InvalidOperationException("Standby requires a retained lighting session.");
                                }
                                intent = new StandbyState { OriginalPower = original.OriginalPower,
                                    OriginalBrightness = original.OriginalBrightness, ChangedPower = original.ChangedPower,
                                    PowerCommandStatusUtc = original.PowerCommandStatusUtc };
                                // Failed sends may have reached the strip. The wake
                                // obligation must exist before the first off attempt.
                                standby.Add(device.Id, intent);
                            }
                        }
                    });
                SendControlLocked(RazerJson(DisablePacket), expected,
                    delegate { return AuthorizeStandby(expected, version, intent, authorize); }, false, null);
            }
        }

        public void Wake(EndpointToken expected, Func<bool> authorize)
        {
            lock (sendGate)
            {
                long version; StandbyState intent;
                lock (gate)
                {
                    ValidateStandbyToken(expected);
                    lock (standbyGate)
                    {
                        version = standbyVersion;
                        if (!standby.TryGetValue(expected.Id, out intent))
                            throw new InvalidOperationException("No matching automatic standby is pending.");
                    }
                }
                SendControlLocked(RazerJson(EnablePacket), expected,
                    delegate { return AuthorizeStandby(expected, version, intent, authorize); }, false,
                    delegate { RequireStandbyIntent(expected.Id, version, intent); RestoreStandbyBaseline(expected.Snapshot, intent); });
                SendControlLocked("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}", expected,
                    delegate { return AuthorizeStandby(expected, version, intent, authorize); }, false, delegate
                    {
                        RequireStandbyIntent(expected.Id, version, intent);
                        SessionState session = RestoreStandbyBaseline(expected.Snapshot, intent);
                        // The explicit monitor-follow wake is authorized by our
                        // own off attempt, including an originally unknown state.
                        // Normal cleanup still obeys the unchanged original state.
                        if (session.OriginalPower == false) session.ChangedPower = intent.ChangedPower = true;
                        session.PowerCommandStatusUtc = intent.PowerCommandStatusUtc = expected.Snapshot.LastStatusUtc;
                    });
                lock (standbyGate)
                {
                    StandbyState current;
                    if (version == standbyVersion && standby.TryGetValue(expected.Id, out current) && Object.ReferenceEquals(current, intent))
                        standby.Remove(expected.Id);
                }
            }
        }

        public void CancelStandby()
        {
            // Never acquire the identity/send gates here: an engine can cancel
            // while its control lock is held and an authorization callback waits
            // for that lock. This gate is never held across such a callback.
            lock (standbyGate) { standbyVersion++; standby.Clear(); }
        }

        public bool HasStandby(string deviceId)
        {
            if (String.IsNullOrWhiteSpace(deviceId)) return false;
            lock (standbyGate) return standby.ContainsKey(deviceId);
        }

        private void ValidateStandbyToken(EndpointToken expected)
        {
            ValidateControlToken(expected);
            DateTime now = utcNow(), seen = expected.Snapshot.LastSeenUtc;
            if (seen > now || seen <= now.AddSeconds(-20))
                throw new InvalidOperationException("Automatic standby requires a fresh matching device reply.");
        }

        private bool AuthorizeStandby(EndpointToken expected, long version, StandbyState intent, Func<bool> authorize)
        {
            ValidateStandbyToken(expected);
            if (!MatchesStandbyIntent(expected.Id, version, intent)) return false;
            bool allowed = authorize == null || authorize();
            // The callback can reenter cancellation or the parser. Recheck both
            // freshness and ownership after it returns, at this send boundary.
            ValidateStandbyToken(expected);
            return allowed && MatchesStandbyIntent(expected.Id, version, intent);
        }

        private bool MatchesStandbyIntent(string id, long version, StandbyState intent)
        {
            lock (standbyGate)
            {
                StandbyState current;
                return version == standbyVersion && (intent == null ||
                    (standby.TryGetValue(id, out current) && Object.ReferenceEquals(current, intent)));
            }
        }

        private void RequireStandbyIntent(string id, long version, StandbyState intent)
        {
            if (!MatchesStandbyIntent(id, version, intent)) throw new OperationCanceledException("Automatic standby was canceled.");
        }

        private SessionState RestoreStandbyBaseline(DeviceInfo device, StandbyState intent)
        {
            SessionState session;
            if (!sessions.TryGetValue(SessionKey(device), out session))
            {
                session = new SessionState { Target = device.Copy() };
                sessions.Add(SessionKey(device), session);
            }
            session.OriginalPower = intent.OriginalPower; session.OriginalBrightness = intent.OriginalBrightness;
            session.ChangedPower = session.ChangedPower || intent.ChangedPower;
            session.PowerCommandStatusUtc = intent.PowerCommandStatusUtc;
            return session;
        }

        public void Disable() { Disable(RequireCurrentEndpoint()); }
        public void Disable(DeviceInfo savedTarget)
        {
            ValidateDevice(savedTarget);
            Disable(new EndpointToken(savedTarget, savedTarget.IdentityGeneration));
        }
        public void Disable(EndpointToken saved)
        {
            if (saved == null) throw new ArgumentNullException("saved");
            DeviceInfo device = saved.Snapshot; ValidateDevice(device);
            lock (sendGate)
            {
                SessionState session;
                lock (gate) { ValidateReleaseTarget(device); sessions.TryGetValue(SessionKey(device), out session); }
                // Every command rechecks conflicts; a reset cannot forget an observed
                // conflicting owner while this release obligation remains pending.
                if (session != null && session.ChangedPower && session.OriginalPower == false)
                    SendControlLocked("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}", saved, null, true, null);
                SendControlLocked(RazerJson(DisablePacket), saved, null, true, null);
                lock (gate) sessions.Remove(SessionKey(device));
            }
        }

        public void SendColors(Color[] colors, bool flag)
        { SendColors(RequireCurrentEndpoint(), colors, flag, null); }
        public void SendColors(EndpointToken expected, Color[] colors, bool flag, Func<bool> authorize)
        {
            byte[] frame = BuildFrame(colors, flag);
            lock (sendGate) SendControlLocked(RazerJson(Convert.ToBase64String(frame)), expected, authorize, false, null);
        }
        public void Poll()
        {
            EndpointToken expected = RequireCurrentEndpoint();
            lock (sendGate) SendControlLocked("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}", expected, null, false, null);
        }

        public static byte[] BuildFrame(Color[] colors, bool flag)
        {
            if (colors == null) throw new ArgumentNullException("colors");
            if (colors.Length != 10 && colors.Length != 18) throw new ArgumentException("Supported layouts require 10 or 18 zone colors; physical mapping must be checked on the strip.", "colors");
            int payloadLength = 2 + colors.Length * 3;
            byte[] frame = new byte[7 + colors.Length * 3];
            frame[0] = 0xBB;
            frame[1] = (byte)(payloadLength >> 8);
            frame[2] = (byte)payloadLength;
            frame[3] = 0xB0;
            frame[4] = flag ? (byte)1 : (byte)0;
            frame[5] = (byte)colors.Length;
            for (int i = 0; i < colors.Length; i++)
            {
                frame[6 + i * 3] = colors[i].R;
                frame[7 + i * 3] = colors[i].G;
                frame[8 + i * 3] = colors[i].B;
            }
            byte checksum = 0;
            for (int i = 0; i < frame.Length - 1; i++) checksum ^= frame[i];
            frame[frame.Length - 1] = checksum;
            return frame;
        }

        private static string RazerJson(string value) { return "{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"" + value + "\"}}}"; }
        private static string SessionKey(DeviceInfo device) { return device.Id + "|" + device.Ip; }
        private static bool IsFreshStatus(DeviceInfo device, DateTime now)
        {
            return device.LastStatusUtc != DateTime.MinValue && device.LastStatusUtc <= now &&
                (now - device.LastStatusUtc).TotalSeconds <= 5;
        }
        private static void ValidateDevice(DeviceInfo device)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (!String.Equals(device.Sku, "H6609", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(device.Id))
                throw new ArgumentException("Only an identified H6609 can be targeted.", "device");
            IPAddress address;
            if (!IPAddress.TryParse(device.Ip, out address) || !IsPrivateIPv4(address))
                throw new ArgumentException("The target must use a private IPv4 LAN address.", "device");
        }
        private EndpointToken RequireCurrentEndpoint()
        {
            lock (gate)
            {
                ThrowIfDisposed(); EnsureStarted();
                if (target == null) throw new InvalidOperationException("Select a discovered H6609 before sending commands.");
                return new EndpointToken(target, generation);
            }
        }
        private void ValidateControlToken(EndpointToken expected)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            DeviceInfo device = expected.Snapshot; ValidateDevice(device);
            ThrowIfDisposed(); EnsureStarted();
            DeviceInfo observed;
            if (expected.Generation != generation || device.IdentityGeneration != generation || target == null ||
                !String.Equals(target.Id, expected.Id, StringComparison.OrdinalIgnoreCase) || target.Ip != expected.Ip ||
                target.IdentityGeneration != generation || !devices.TryGetValue(expected.Id, out observed) ||
                observed.Ip != expected.Ip || observed.IdentityGeneration != generation)
                throw new InvalidOperationException("The command endpoint is stale; obtain a fresh matching device token before retrying.");
            ValidateReleaseTarget(device);
        }
        private void SendControlLocked(string json, EndpointToken expected, Func<bool> authorize, bool release, Action beforeSend)
        {
            // The parser uses this same identity gate. Once authorization succeeds,
            // an accepted scan cannot change the destination between validation and
            // this datagram. The next datagram must validate and authorize again.
            lock (gate)
            {
                DeviceInfo device = expected == null ? null : expected.Snapshot;
                if (release) ValidateReleaseTarget(device); else ValidateControlToken(expected);
                if (authorize != null && !authorize()) throw new OperationCanceledException("Control authorization expired before the datagram was sent.");
                // Authorization is external code and can reenter the parser in a test
                // or callback. A changed identity still must not authorize this token.
                if (release) ValidateReleaseTarget(device); else ValidateControlToken(expected);
                if (beforeSend != null) beforeSend();
                SendLocked(json, device);
            }
        }
        private void ValidateReleaseTarget(DeviceInfo device)
        {
            lock (gate)
            {
                ThrowIfDisposed(); EnsureStarted();
                if (!IsLocalSource(IPAddress.Parse(device.Ip), locals))
                    throw new InvalidOperationException("The saved release target is outside the current physical local subnet; release remains pending.");
                foreach (DeviceInfo observed in devices.Values)
                    if (observed.Ip == device.Ip && !String.Equals(observed.Id, device.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A different device identity now occupies the saved release address; release remains pending.");
                DeviceInfo known;
                SessionState session;
                bool knownSession = sessions.TryGetValue(SessionKey(device), out session);
                if (knownSession && session.ConflictBlocked)
                    throw new InvalidOperationException("A retained identity conflict blocks this saved endpoint; release remains pending until a newer exact matching scan.");
                bool knownScan = devices.TryGetValue(device.Id, out known) && known.Ip == device.Ip;
                if (!knownSession && !knownScan)
                    throw new InvalidOperationException("The saved release target has no established identity or session.");
            }
        }
        private void SendLocked(string json, DeviceInfo device)
        {
            ILanDatagramSocket socket;
            lock (gate)
            {
                ThrowIfDisposed(); EnsureStarted();
                if (!IsLocalSource(IPAddress.Parse(device.Ip), locals))
                    throw new InvalidOperationException("The device is outside the current physical local subnet.");
                socket = sender;
            }
            if (socket == null) throw new IOException("The local transport is recovering; retry after a valid device scan.");
            try { socket.Send(Encoding.UTF8.GetBytes(json), new IPEndPoint(IPAddress.Parse(device.Ip), 4003)); }
            catch (SocketException ex) { FaultTransport("Govee send socket failed: " + ex.SocketErrorCode); throw; }
            catch (ObjectDisposedException) { FaultTransport("Govee send socket was closed unexpectedly."); throw; }
        }

        private void ReceiveLoop()
        {
            try
            {
            while (!stopping.WaitOne(0))
            {
                ILanDatagramSocket socket = null; int receivedGeneration = -1;
                try
                {
                    MaintainTransport(false);
                    lock (gate) { if (disposed) return; socket = receiver; receivedGeneration = generation; }
                    if (socket == null) { stopping.WaitOne(100); continue; }
                    IPEndPoint source;
                    byte[] bytes = socket.Receive(out source);
                    DeviceInfo info = null;
                    try { info = AcceptDatagram(bytes, source, receivedGeneration, socket); }
                    finally
                    {
                        // Optional injected observation only: it cannot grant receive
                        // authority. Production has no observer. This runs even when
                        // a retired receiver packet is rejected by the real parser.
                        if (receiveProcessed != null)
                            try { receiveProcessed(socket, receivedGeneration, info == null ? null : info.Copy()); }
                            catch (Exception ex) { ReportError("Receive completion observer failed: " + ex.Message); }
                    }
                    Action<DeviceInfo> handler = DeviceSeen;
                    if (info != null && handler != null)
                    {
                        try { handler(info.Copy()); } catch (Exception ex) { ReportError("Device notification failed: " + ex.Message); }
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock ||
                        ex.SocketErrorCode == SocketError.Interrupted || ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                    FaultTransport("Govee receive socket failed: " + ex.SocketErrorCode, socket, receivedGeneration);
                }
                catch (ObjectDisposedException) { if (!stopping.WaitOne(0)) FaultTransport("Govee receiver was closed unexpectedly.", socket, receivedGeneration); }
                catch (Exception ex) { ReportError("Govee transport recovery: " + ex.Message); stopping.WaitOne(100); }
            }
            }
            finally { BackgroundCompleted(); }
        }

        // Parser convenience for existing isolated tests. Real receive processing
        // always supplies the socket captured before Receive, via the overload below.
        internal DeviceInfo AcceptDatagram(byte[] bytes, IPEndPoint source, int receivedGeneration)
        {
            lock (gate) return AcceptDatagram(bytes, source, receivedGeneration, receiver);
        }
        internal DeviceInfo AcceptDatagram(byte[] bytes, IPEndPoint source, int receivedGeneration, ILanDatagramSocket receivedSocket)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumDatagram || source == null) return null;
            lock (gate)
            {
                // Retirement revokes authority immediately, before construction and
                // before the next public generation is published. An old socket may
                // still finish Receive successfully after it has been closed.
                if (disposed || receiver == null || !Object.ReferenceEquals(receivedSocket, receiver) ||
                    receivedGeneration != generation || !IsLocalSource(source.Address, locals)) return null;
                try
                {
                    JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaximumDatagram, RecursionLimit = 12 };
                    IDictionary<string, object> root = json.DeserializeObject(Encoding.UTF8.GetString(bytes).TrimEnd('\0')) as IDictionary<string, object>;
                    IDictionary<string, object> msg = ObjectValue(root, "msg"), data = ObjectValue(msg, "data");
                    string command = StringValue(msg, "cmd"), ip = source.Address.ToString();
                    if (data == null) return null;
                    if (command == "scan")
                    {
                        string sku = StringValue(data, "sku"), id = StringValue(data, "device");
                        if (String.IsNullOrWhiteSpace(id) || id.Length > 128 || String.IsNullOrWhiteSpace(sku) || sku.Length > 32) return null;
                        ObserveAddressIdentity(id, sku, ip);
                        if (!String.Equals(sku, "H6609", StringComparison.OrdinalIgnoreCase))
                        {
                            List<string> unsupportedAddress = new List<string>();
                            foreach (DeviceInfo known in devices.Values) if (known.Ip == ip) unsupportedAddress.Add(known.Id);
                            foreach (string oldId in unsupportedAddress) devices.Remove(oldId);
                            if (target != null && target.Ip == ip)
                            { target.LastSeenUtc = DateTime.MinValue; target.IdentityGeneration = -1; target.LastStatusUtc = DateTime.MinValue; target.PowerOn = null; target.Brightness = null; }
                            return null;
                        }
                        DeviceInfo prior; devices.TryGetValue(id, out prior);
                        DeviceInfo info = new DeviceInfo { Ip = ip, Id = id, Sku = "H6609", LastSeenUtc = utcNow(), IdentityGeneration = generation };
                        if (prior != null && prior.Ip == ip && prior.IdentityGeneration == generation)
                        { info.PowerOn = prior.PowerOn; info.Brightness = prior.Brightness; info.LastStatusUtc = prior.LastStatusUtc; }
                        if (devices.Count >= 64 && !devices.ContainsKey(id)) return null;
                        // The latest accepted scan owns this address. Old conflicting
                        // identities are retired from the live map, but their session
                        // tombstone and audit evidence above survive all rebuilds.
                        List<string> replaced = new List<string>();
                        foreach (DeviceInfo known in devices.Values)
                            if (known.Ip == ip && !String.Equals(known.Id, id, StringComparison.OrdinalIgnoreCase)) replaced.Add(known.Id);
                        foreach (string oldId in replaced) devices.Remove(oldId);
                        devices[id] = info;
                        if (target != null && target.Ip == ip && !String.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase))
                        { target.LastSeenUtc = DateTime.MinValue; target.IdentityGeneration = -1; target.LastStatusUtc = DateTime.MinValue; target.PowerOn = null; target.Brightness = null; }
                        if (target != null && String.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase)) target = info.Copy();
                        return info.Copy();
                    }
                    if (command == "devStatus" && target != null && target.Ip == ip && target.IdentityGeneration == generation)
                    {
                        DeviceInfo established;
                        if (!devices.TryGetValue(target.Id, out established) || established.Ip != ip || established.IdentityGeneration != generation) return null;
                        bool? power = ReadPower(data); int? brightness = ReadBrightness(data);
                        if (power == null && brightness == null)
                        { target.PowerOn = null; target.Brightness = null; target.LastStatusUtc = DateTime.MinValue; devices[target.Id] = target.Copy(); return null; }
                        // Each field is from THIS response. Missing/invalid values are
                        // unknown rather than inherited or guessed from a sent command.
                        target.PowerOn = power; target.Brightness = brightness;
                        target.LastStatusUtc = utcNow(); target.LastSeenUtc = target.LastStatusUtc;
                        devices[target.Id] = target.Copy(); return target.Copy();
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return null;
        }
        private void ObserveAddressIdentity(string id, string sku, string ip)
        {
            long sequence = ++observationSequence;
            foreach (SessionState session in sessions.Values)
            {
                if (session.Target.Ip != ip) continue;
                if (!String.Equals(session.Target.Id, id, StringComparison.OrdinalIgnoreCase) || !String.Equals(sku, session.Target.Sku, StringComparison.OrdinalIgnoreCase))
                {
                    string identity = id + "/" + sku;
                    bool changed = !session.ConflictBlocked || !String.Equals(session.LastConflictingId, identity, StringComparison.OrdinalIgnoreCase);
                    session.ConflictBlocked = true; session.LastConflictSequence = sequence; session.LastConflictingId = identity;
                    if (changed)
                    {
                        conflictAudit.Add(utcNow().ToString("o") + " sequence=" + sequence + " endpoint=" + SessionKey(session.Target) + " conflicting=" + identity);
                        if (conflictAudit.Count > 128) conflictAudit.RemoveAt(0);
                    }
                }
                else if (sequence > session.LastConflictSequence) session.ConflictBlocked = false;
            }
        }
        private static bool? ReadPower(IDictionary<string, object> data)
        {
            object value; if (!data.TryGetValue("onOff", out value) || !(value is int)) return null;
            int state = (int)value; return state == 0 ? (bool?)false : state == 1 ? (bool?)true : null;
        }
        private static int? ReadBrightness(IDictionary<string, object> data)
        {
            object value; if (!data.TryGetValue("brightness", out value) || !(value is int)) return null;
            int brightness = (int)value; return brightness >= 0 && brightness <= 100 ? (int?)brightness : null;
        }
        private static IDictionary<string, object> ObjectValue(IDictionary<string, object> parent, string key)
        { object value; return parent != null && parent.TryGetValue(key, out value) ? value as IDictionary<string, object> : null; }
        private static string StringValue(IDictionary<string, object> parent, string key)
        { object value; return parent != null && parent.TryGetValue(key, out value) ? value as string : null; }

        internal void MaintainTransport(bool forceInterfaceCheck)
        {
            lock (sendGate)
            {
            DateTime now = utcNow(); bool check;
            lock (gate)
            {
                if (disposed) return;
                check = forceInterfaceCheck || now >= interfaceCheckAt;
                if (check) interfaceCheckAt = now.AddSeconds(5);
                if (receiver == null && now < retryAt) return;
            }
            LanInterface[] current = check ? interfaceProvider() : null;
            bool rebuild;
            lock (gate) rebuild = receiver == null || (current != null && !SameInterfaces(locals, current));
            if (rebuild)
            {
                try { RebuildTransport(current ?? interfaceProvider()); }
                catch (Exception ex)
                {
                    lock (gate) retryAt = utcNow().AddSeconds(2);
                    ReportError("Govee transport retry in 2 seconds: " + ex.Message);
                }
            }
            else if (current != null)
            {
                lock (sendGate)
                lock (gate)
                {
                    if (receiver == null || disposed) return;
                    foreach (LanInterface local in current)
                        if (!joined.Contains(local.Key))
                        {
                            try { receiver.Join(local); joined.Add(local.Key); }
                            catch (SocketException ex) { ReportError("Multicast join unavailable on " + local.Address + ": " + ex.SocketErrorCode); }
                        }
                }
            }
            }
        }
        private void RebuildTransport(LanInterface[] current)
        {
            lock (sendGate)
            {
                ILanDatagramSocket oldReceiver, oldSender;
                lock (gate)
                {
                    ThrowIfDisposed();
                    if (receiver != null && SameInterfaces(locals, current ?? new LanInterface[0])) return;
                    oldReceiver = receiver; oldSender = sender;
                    receiver = null; sender = null; joined.Clear();
                    locals = CopyInterfaces(current ?? new LanInterface[0]);
                    devices.Clear();
                    if (target != null)
                    { target.LastSeenUtc = DateTime.MinValue; target.LastStatusUtc = DateTime.MinValue; target.PowerOn = null; target.Brightness = null; target.IdentityGeneration = -1; }
                }
                SafeClose(oldReceiver); SafeClose(oldSender);
                ILanDatagramSocket newReceiver = null, newSender = null;
                try
                {
                    newReceiver = socketFactory(true); newSender = socketFactory(false);
                    foreach (LanInterface local in locals)
                    {
                        try { newReceiver.Join(local); joined.Add(local.Key); }
                        catch (SocketException ex) { ReportError("Multicast join unavailable on " + local.Address + ": " + ex.SocketErrorCode); }
                    }
                    lock (gate)
                    {
                        ThrowIfDisposed();
                        receiver = newReceiver; sender = newSender; generation++;
                        retryAt = utcNow(); interfaceCheckAt = utcNow().AddSeconds(5); lastError = "";
                    }
                }
                catch { SafeClose(newReceiver); SafeClose(newSender); throw; }
            }
        }
        private void FaultTransport(string reason) { FaultTransport(reason, null, -1); }
        private void FaultTransport(string reason, ILanDatagramSocket expectedReceiver, int expectedGeneration)
        {
            lock (sendGate)
            {
                ILanDatagramSocket oldReceiver, oldSender;
                lock (gate)
                {
                    if (disposed) return;
                    // Check after waiting for any rebuild, not before its lock.
                    if (expectedReceiver != null && (!Object.ReferenceEquals(receiver, expectedReceiver) || generation != expectedGeneration)) return;
                    oldReceiver = receiver; oldSender = sender;
                    receiver = null; sender = null; joined.Clear(); retryAt = utcNow().AddSeconds(2);
                    devices.Clear();
                    if (target != null)
                    { target.LastSeenUtc = DateTime.MinValue; target.LastStatusUtc = DateTime.MinValue; target.PowerOn = null; target.Brightness = null; target.IdentityGeneration = -1; }
                }
                SafeClose(oldReceiver); SafeClose(oldSender);
            }
            ReportError(reason + "; recreating transport after a bounded retry.");
        }
        private static void SafeClose(ILanDatagramSocket socket) { if (socket != null) try { socket.Dispose(); } catch { } }
        private static LanInterface[] CopyInterfaces(LanInterface[] values)
        {
            LanInterface[] result = new LanInterface[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = new LanInterface { Id = values[i].Id, Address = values[i].Address, Mask = (byte[])values[i].Mask.Clone(), Broadcast = values[i].Broadcast, Route = values[i].Route };
            return result;
        }
        private static bool SameInterfaces(LanInterface[] a, LanInterface[] b)
        {
            if (a.Length != b.Length) return false;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (LanInterface local in a) keys.Add(local.Key);
            foreach (LanInterface local in b) if (!keys.Contains(local.Key)) return false;
            return true;
        }
        private static LanInterface[] GetLocalInterfaces()
        {
            List<LanInterface> result = new List<LanInterface>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)) continue;
                string name = (nic.Name + " " + nic.Description).ToLowerInvariant();
                if (name.Contains("virtual") || name.Contains("vethernet") || name.Contains("hyper-v") || name.Contains("vpn") || name.Contains("tunnel") || name.Contains("tap-") || name.Contains("wsl")) continue;
                IPInterfaceProperties properties = nic.GetIPProperties();
                List<string> routes = new List<string>();
                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork) routes.Add(gateway.Address.ToString());
                routes.Sort(StringComparer.Ordinal); string route = String.Join(",", routes.ToArray());
                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (!IsPrivateIPv4(unicast.Address) || unicast.IPv4Mask == null) continue;
                    byte[] address = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes(), broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | (byte)~mask[i]);
                    result.Add(new LanInterface { Id = nic.Id, Address = unicast.Address, Mask = mask, Broadcast = new IPAddress(broadcast), Route = route });
                }
            }
            return result.ToArray();
        }
        private static bool IsPrivateIPv4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
        private static bool IsLocalSource(IPAddress address, LanInterface[] interfaces)
        {
            if (!IsPrivateIPv4(address)) return false;
            byte[] source = address.GetAddressBytes();
            foreach (LanInterface local in interfaces)
            {
                byte[] own = local.Address.GetAddressBytes(); bool matches = true, networkAddress = true;
                for (int i = 0; i < 4; i++)
                {
                    if ((own[i] & local.Mask[i]) != (source[i] & local.Mask[i])) matches = false;
                    if ((source[i] & (byte)~local.Mask[i]) != 0) networkAddress = false;
                }
                if (matches && !networkAddress && !address.Equals(local.Address) && !address.Equals(local.Broadcast)) return true;
            }
            return false;
        }
        private void EnsureStarted() { if (!started) throw new InvalidOperationException("Start the Govee LAN receiver before use."); }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("GoveeLan"); }
        private void ReportError(string message)
        {
            Action<string> handler;
            lock (gate) { if (disposed || lastError == message) return; lastError = message; handler = Error; }
            if (handler != null) try { handler(message); } catch { }
        }
        private void BackgroundCompleted()
        {
            Interlocked.Decrement(ref backgroundWorkers);
            CloseStopHandleIfFinished();
        }
        private void CloseStopHandleIfFinished()
        {
            lock (gate)
            {
                if (!disposed || backgroundWorkers != 0 || stopHandleClosed) return;
                stopHandleClosed = true;
                stopping.Close();
            }
        }

        public void Dispose()
        {
            Thread worker;
            lock (gate)
            {
                if (disposed) return;
                disposed = true; started = false; stopping.Set(); worker = receiveThread; receiveThread = null;
            }
            lock (sendGate)
            {
                ILanDatagramSocket oldReceiver, oldSender;
                lock (gate) { oldReceiver = receiver; oldSender = sender; receiver = null; sender = null; joined.Clear(); }
                SafeClose(oldReceiver); SafeClose(oldSender);
            }
            if (worker != null && worker != Thread.CurrentThread && !worker.Join(1500))
            {
                // Socket receive is bounded and closing the socket normally wakes it.
                // A blocking external event callback can outlive this join; never
                // claim it has stopped or dispose its wait handle underneath it.
                // The last background worker closes the handle when it exits.
            }
            CloseStopHandleIfFinished();
        }
    }
}
