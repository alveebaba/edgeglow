using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EdgeGlow
{
    // Compiles with the actual Network.cs. Every socket and interface is an
    // injected in-memory implementation; this suite never constructs UDP sockets.
    internal static class StandbyNetworkTests
    {
        private static int checks, cases;
        private static readonly Func<bool> Allowed = delegate { return true; };
        private static void Check(bool condition, string name)
        { checks++; if (!condition) throw new Exception(name); }
        private static void Invalid(Action action, string name)
        {
            bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; }
            Check(failed, name);
        }
        private static void Canceled(Action action, string name)
        {
            bool failed = false; try { action(); } catch (OperationCanceledException) { failed = true; }
            Check(failed, name);
        }
        private static void Failed(Action action, string name)
        {
            bool failed = false; try { action(); } catch (SocketException) { failed = true; }
            Check(failed, name);
        }
        private static void Command(Fixture f, int index, string marker, string ip)
        {
            Check(index < f.Commands.Count && f.Commands[index].Contains(marker), "Expected command " + index + ": " + marker);
            Check(index < f.Destinations.Count && f.Destinations[index] == ip, "Expected destination " + index + ": " + ip);
        }
        private static void StandbyPair(Fixture f, string ip)
        {
            Check(f.Commands.Count == 2, "Standby sends exactly two commands.");
            Command(f, 0, "\"value\":0", ip); Command(f, 1, GoveeLan.DisablePacket, ip);
        }
        private static void WakePair(Fixture f, string ip)
        {
            Check(f.Commands.Count == 2, "Wake sends exactly two commands.");
            Command(f, 0, GoveeLan.EnablePacket, ip); Command(f, 1, "\"value\":1", ip);
        }
        private static void FinalRelease(Fixture f, bool? original, string ip)
        {
            f.Clear(); f.Lan.Disable(f.Token);
            Check(f.Commands.Count == (original == false ? 2 : 1), "Final release retains the original power baseline.");
            if (original == false) Command(f, 0, "\"value\":0", ip);
            Command(f, f.Commands.Count - 1, GoveeLan.DisablePacket, ip);
        }

        public static int Main(string[] args)
        {
            try
            {
                BaselinesAndOrder(); NormalMethodsUnchanged(); OwnershipAndFreshness();
                StandbyAuthorization(); WakeAuthorization(); Cancellation();
                StandbySendFailures(); WakeSendFailures(); DhcpAndCleanup();
                DhcpBeforeStandby(); DifferentIdentity(); CommandBoundaryIdentity();
                GenerationAndSubnet(); ReentrantCancellation(); NewStandbyDuringWake(); CancellationLockOrder();
                Console.WriteLine("PASS: " + cases + " standby network cases, " + checks + " checks. Actual GoveeLan with in-memory sockets; no LAN, device, desktop, GUI, settings or autostart.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }

        private static void BaselinesAndOrder()
        {
            cases++;
            foreach (bool? original in new bool?[] { true, false, null })
            using (Fixture f = new Fixture())
            {
                f.Open(original); f.Clear(); f.Lan.Standby(f.Token, Allowed); StandbyPair(f, Fixture.Ip);
                Check(f.Lan.HasStandby("aLpHa") && !f.Lan.HasStandby("Beta") && !f.Lan.HasStandby(null), "Standby lookup is identity-based and case insensitive.");
                f.Advance(0.1); f.Status(Fixture.Ip, false); f.Clear();
                // A fresh off observation cannot replace an originally-on or
                // unknown baseline with the temporary standby state.
                f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
                Check(!f.Lan.HasStandby("Alpha"), "Completed wake clears the lookup.");
                f.Clear(); Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "Completed wake consumes its separate intent.");
                Check(f.Commands.Count == 0, "No duplicate wake commands.");
                FinalRelease(f, original, Fixture.Ip);
            }
        }
        private static void NormalMethodsUnchanged()
        {
            cases++;
            foreach (bool? original in new bool?[] { true, false, null })
            using (Fixture f = new Fixture())
            {
                f.Select("Alpha", Fixture.Ip); if (original.HasValue) f.Status(Fixture.Ip, original.Value);
                f.Lan.Enable(f.Token, Allowed);
                Check(f.Commands.Count == (original == false ? 2 : 1), "Normal activation power policy is unchanged.");
                Command(f, 0, GoveeLan.EnablePacket, Fixture.Ip);
                if (original == false) Command(f, 1, "\"value\":1", Fixture.Ip);
                FinalRelease(f, original, Fixture.Ip);
            }
        }
        private static void OwnershipAndFreshness()
        {
            cases++;
            using (Fixture f = new Fixture())
            {
                f.Select("Alpha", Fixture.Ip);
                Invalid(delegate { f.Lan.Standby(f.Token, Allowed); }, "A scan alone does not grant stream ownership for off.");
                Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "No off attempt means no wake authority.");
                Check(f.Commands.Count == 0, "Unowned operations send nothing.");
                Check(!f.Lan.HasStandby("Alpha"), "Rejected unowned off creates no intent.");
                f.Lan.Enable(f.Token, Allowed); f.Clear(); EndpointToken saved = f.Token;
                f.Advance(20);
                Invalid(delegate { f.Lan.Standby(saved, Allowed); }, "Exactly twenty-second-old replies cannot authorize standby.");
                Check(f.Commands.Count == 0, "Stale standby sends nothing.");
                f.Advance(-20.1);
                Invalid(delegate { f.Lan.Standby(saved, Allowed); }, "Future reply timestamps cannot authorize standby.");
                Check(f.Commands.Count == 0, "Future standby sends nothing.");
            }
            using (Fixture f = new Fixture())
            {
                f.Open(true); f.Lan.Standby(f.Token, Allowed); f.Clear(); EndpointToken saved = f.Token;
                f.Advance(20);
                Invalid(delegate { f.Lan.Wake(saved, Allowed); }, "Wake also requires a fresh reply.");
                Check(f.Commands.Count == 0, "Stale wake sends nothing and retains its record.");
                f.Select("Alpha", Fixture.Ip); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
            }
        }
        private static void StandbyAuthorization()
        {
            cases++;
            for (int reject = 1; reject <= 2; reject++)
            using (Fixture f = new Fixture())
            {
                f.Open(false); f.Clear(); int calls = 0; int boundary = reject;
                Canceled(delegate { f.Lan.Standby(f.Token, delegate { return ++calls != boundary; }); }, "Standby checks authorization at each boundary.");
                Check(calls == reject && f.Commands.Count == reject - 1, "Canceled standby sends no later command.");
                if (reject == 1)
                {
                    Check(!f.Lan.HasStandby("Alpha"), "First-boundary cancellation creates no off intent.");
                    Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "Cancellation before off retains no wake record.");
                }
                else
                {
                    Check(f.Lan.HasStandby("Alpha"), "An attempted off survives second-boundary cancellation.");
                    Command(f, 0, "\"value\":0", Fixture.Ip); f.Clear();
                    f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
                }
                FinalRelease(f, false, Fixture.Ip);
            }
        }
        private static void WakeAuthorization()
        {
            cases++;
            for (int reject = 1; reject <= 2; reject++)
            using (Fixture f = new Fixture())
            {
                f.Open(false); f.Lan.Standby(f.Token, Allowed); f.Clear(); int calls = 0; int boundary = reject;
                Canceled(delegate { f.Lan.Wake(f.Token, delegate { return ++calls != boundary; }); }, "Wake checks authorization at each boundary.");
                Check(calls == reject && f.Commands.Count == reject - 1, "Canceled wake sends no later command.");
                if (reject == 2) Command(f, 0, GoveeLan.EnablePacket, Fixture.Ip);
                f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
                FinalRelease(f, false, Fixture.Ip);
            }
        }
        private static void Cancellation()
        {
            cases++;
            using (Fixture f = new Fixture())
            {
                f.Open(false); f.Lan.Standby(f.Token, Allowed); f.Clear();
                f.Lan.CancelStandby(); f.Lan.CancelStandby();
                Check(f.Commands.Count == 0, "Cancellation never sends UDP.");
                Check(!f.Lan.HasStandby("Alpha"), "Cancel clears diagnostic intent state.");
                Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "Cancellation removes wake intent.");
                FinalRelease(f, false, Fixture.Ip);
            }
        }
        private static void StandbySendFailures()
        {
            cases++;
            for (int fail = 1; fail <= 2; fail++)
            using (Fixture f = new Fixture())
            {
                f.Open(false); f.Clear(); EndpointToken retired = f.Token; f.Factory.FailAttempt = fail;
                Failed(delegate { f.Lan.Standby(retired, Allowed); }, "Standby socket failure surfaces.");
                Check(f.Commands.Count == fail - 1, "Standby failure stops the remaining transaction.");
                Check(f.Lan.HasStandby("Alpha"), "Off intent predates an uncertain send failure.");
                f.Recover("Alpha", Fixture.Ip); f.Clear();
                Invalid(delegate { f.Lan.Wake(retired, Allowed); }, "Retired endpoint cannot wake after a send fault.");
                Check(f.Commands.Count == 0, "Retired wake sends nothing.");
                // The off attempt's record survived even when no send reported
                // success; transport exceptions do not prove non-delivery.
                f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
                FinalRelease(f, false, Fixture.Ip);
            }
            using (Fixture f = new Fixture())
            {
                f.Open(true); f.Clear(); f.Factory.FailAttempt = 2;
                Failed(delegate { f.Lan.Standby(f.Token, Allowed); }, "Partial standby fails at stream-disable.");
                f.Recover("Alpha", Fixture.Ip); f.Clear(); f.Lan.Standby(f.Token, Allowed); StandbyPair(f, Fixture.Ip);
                f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip); FinalRelease(f, true, Fixture.Ip);
            }
        }
        private static void WakeSendFailures()
        {
            cases++;
            for (int fail = 1; fail <= 2; fail++)
            using (Fixture f = new Fixture())
            {
                f.Open(false); f.Lan.Standby(f.Token, Allowed); f.Clear(); f.Factory.FailAttempt = fail;
                Failed(delegate { f.Lan.Wake(f.Token, Allowed); }, "Wake socket failure surfaces.");
                Check(f.Commands.Count == fail - 1, "Failed wake stops its remaining commands.");
                Check(f.Lan.HasStandby("Alpha"), "Partial wake retains intent until every local send succeeds.");
                f.Recover("Alpha", Fixture.Ip); f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
                f.Clear(); Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "Only a completed wake consumes its record.");
                FinalRelease(f, false, Fixture.Ip);
            }
        }
        private static void DhcpAndCleanup()
        {
            cases++;
            foreach (bool? original in new bool?[] { true, false, null })
            using (Fixture f = new Fixture())
            {
                f.Open(original); EndpointToken old = f.Token; f.Lan.Standby(old, Allowed);
                f.Select("alpha", Fixture.OtherIp); f.Status(Fixture.OtherIp, false); f.Clear();
                Invalid(delegate { f.Lan.Wake(old, Allowed); }, "DHCP invalidates the old current-control token.");
                Check(f.Commands.Count == 0, "Old-address wake sends nothing.");
                f.Lan.Disable(old);
                Check(f.Lan.HasStandby("Alpha"), "Old normal cleanup cannot erase a same-ID standby record.");
                Command(f, f.Commands.Count - 1, GoveeLan.DisablePacket, Fixture.Ip);
                f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.OtherIp);
                FinalRelease(f, original, Fixture.OtherIp);
            }
        }
        private static void DhcpBeforeStandby()
        {
            cases++;
            foreach (bool? original in new bool?[] { true, false, null })
            using (Fixture f = new Fixture())
            {
                f.Open(original); EndpointToken old = f.Token;
                f.Select("alpha", Fixture.OtherIp); f.Status(Fixture.OtherIp, false); f.Clear();
                f.Lan.Standby(f.Token, Allowed); StandbyPair(f, Fixture.OtherIp);
                f.Clear(); f.Lan.Disable(old);
                Check(f.Commands.Count == (original == false ? 2 : 1), "DHCP standby leaves the old normal release record intact.");
                Command(f, f.Commands.Count - 1, GoveeLan.DisablePacket, Fixture.Ip);
                f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.OtherIp);
                FinalRelease(f, original, Fixture.OtherIp);
            }
        }
        private static void DifferentIdentity()
        {
            cases++;
            using (Fixture f = new Fixture())
            {
                f.Open(true); EndpointToken original = f.Token; f.Lan.Standby(original, Allowed);
                f.Select("Beta", Fixture.Ip); f.Clear();
                Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "A different identity cannot inherit wake ownership.");
                Invalid(delegate { f.Lan.Wake(original, Allowed); }, "A retained off record cannot bypass a conflicting current identity.");
                Check(f.Commands.Count == 0, "Conflicting identity receives no wake.");
                f.Select("Alpha", Fixture.OtherIp); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.OtherIp);
                f.Clear(); Invalid(delegate { f.Lan.Disable(original); }, "Old conflicting cleanup remains blocked after wake at the new address.");
                Check(f.Commands.Count == 0, "Wake never clears the old endpoint conflict tombstone.");
                FinalRelease(f, true, Fixture.OtherIp);
            }
        }
        private static void CommandBoundaryIdentity()
        {
            cases++;
            for (int operation = 0; operation < 2; operation++)
            using (Fixture f = new Fixture())
            {
                f.Open(true); if (operation == 1) f.Lan.Standby(f.Token, Allowed);
                EndpointToken old = f.Token; f.Clear();
                f.Factory.AfterSend = delegate { f.Factory.AfterSend = null; f.Select("Beta", Fixture.Ip); };
                if (operation == 0) Invalid(delegate { f.Lan.Standby(old, Allowed); }, "Identity is revalidated between off and stream-disable.");
                else Invalid(delegate { f.Lan.Wake(old, Allowed); }, "Identity is revalidated between stream-enable and on.");
                Check(f.Commands.Count == 1, "A conflicting scan prevents the second command.");
                Command(f, 0, operation == 0 ? "\"value\":0" : GoveeLan.EnablePacket, Fixture.Ip);
                f.Select("Alpha", Fixture.Ip); f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
            }
        }
        private static void GenerationAndSubnet()
        {
            cases++;
            using (Fixture f = new Fixture())
            {
                f.Open(true); EndpointToken old = f.Token; f.Lan.Standby(old, Allowed);
                int generation = f.Lan.Generation;
                f.Interfaces = new LanInterface[] { Fixture.Interface("replacement", "192.168.3.10") };
                f.Lan.MaintainTransport(true); f.Clear();
                Invalid(delegate { f.Lan.Wake(old, Allowed); }, "Removed subnet and generation block old wake.");
                Check(f.Scan("Alpha", Fixture.Ip) == null && f.Commands.Count == 0, "Off-subnet scan grants no wake authority.");
                f.Interfaces = new LanInterface[] { Fixture.Interface("restored", "192.168.2.10") }; f.Lan.MaintainTransport(true);
                Check(f.Lan.AcceptDatagram(Fixture.ScanBytes("Alpha", Fixture.Ip), new IPEndPoint(IPAddress.Parse(Fixture.Ip), 4003), generation) == null,
                    "A retired receive generation cannot revalidate wake.");
                Invalid(delegate { f.Lan.Wake(old, Allowed); }, "Old token remains rejected after subnet restoration.");
                f.Select("Alpha", Fixture.Ip); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
                FinalRelease(f, true, Fixture.Ip);
            }
        }
        private static void ReentrantCancellation()
        {
            cases++;
            using (Fixture f = new Fixture())
            {
                f.Open(false); f.Clear();
                Canceled(delegate { f.Lan.Standby(f.Token, delegate { f.Lan.CancelStandby(); return true; }); }, "Authorization cannot cancel then recreate off ownership.");
                Check(f.Commands.Count == 0, "Reentrant cancellation before off sends nothing.");
                Invalid(delegate { f.Lan.Wake(f.Token, Allowed); }, "Reentrant cancellation leaves no off record.");
                f.Factory.AfterSend = delegate { f.Factory.AfterSend = null; f.Lan.CancelStandby(); };
                Canceled(delegate { f.Lan.Standby(f.Token, Allowed); }, "Cancellation between standby commands stops stream-disable.");
                Check(f.Commands.Count == 1, "Only off preceded cancellation.");
                f.Clear(); f.Lan.Standby(f.Token, Allowed); f.Clear();
                f.Factory.AfterSend = delegate { f.Factory.AfterSend = null; f.Lan.CancelStandby(); };
                Canceled(delegate { f.Lan.Wake(f.Token, Allowed); }, "Cancellation between wake commands stops turn-on.");
                Check(f.Commands.Count == 1 && f.Commands[0].Contains(GoveeLan.EnablePacket), "Canceled wake sends no power-on.");
                FinalRelease(f, false, Fixture.Ip);
            }
            using (Fixture f = new Fixture())
            {
                f.Open(true); f.Clear();
                Invalid(delegate { f.Lan.Standby(f.Token, delegate { f.Advance(21); return true; }); }, "Reply freshness is rechecked after authorization.");
                Check(f.Commands.Count == 0, "A token made stale by the callback sends nothing.");
            }
        }
        private static void NewStandbyDuringWake()
        {
            cases++;
            using (Fixture f = new Fixture())
            {
                f.Open(true); f.Lan.Standby(f.Token, Allowed); f.Clear();
                f.Factory.AfterSend = delegate { f.Factory.AfterSend = null; f.Lan.Standby(f.Token, Allowed); };
                Canceled(delegate { f.Lan.Wake(f.Token, Allowed); }, "A newer standby invalidates the old wake transaction.");
                Check(f.Commands.Count == 3, "New standby follows mode-enable without a late old power-on.");
                Command(f, 0, GoveeLan.EnablePacket, Fixture.Ip); Command(f, 1, "\"value\":0", Fixture.Ip); Command(f, 2, GoveeLan.DisablePacket, Fixture.Ip);
                f.Clear(); f.Lan.Wake(f.Token, Allowed); WakePair(f, Fixture.Ip);
            }
        }
        private static void CancellationLockOrder()
        {
            cases++;
            using (Fixture f = new Fixture())
            using (ManualResetEvent authorizing = new ManualResetEvent(false))
            using (ManualResetEvent canceled = new ManualResetEvent(false))
            {
                f.Open(true); f.Clear(); EndpointToken token = f.Token; object control = new object(); Exception error = null;
                Thread operation = new Thread(delegate()
                {
                    try { f.Lan.Standby(token, delegate { authorizing.Set(); lock (control) return true; }); }
                    catch (Exception ex) { error = ex; }
                });
                Thread cancellation = new Thread(delegate() { f.Lan.CancelStandby(); canceled.Set(); });
                try
                {
                    lock (control)
                    {
                        operation.Start(); Check(authorizing.WaitOne(1500), "Transport reached authorization while the engine control lock was held.");
                        cancellation.Start(); Check(canceled.WaitOne(1500), "Cancellation does not wait for the transport identity gate or engine control lock.");
                    }
                }
                finally
                {
                    if (operation.ThreadState != ThreadState.Unstarted) Check(operation.Join(1500), "Canceled standby worker finished.");
                    if (cancellation.ThreadState != ThreadState.Unstarted) Check(cancellation.Join(1500), "Cancellation worker finished.");
                }
                Check(error is OperationCanceledException && f.Commands.Count == 0 && !f.Lan.HasStandby("Alpha"), "Canceled authorization sends nothing and cannot recreate intent.");
            }
        }

        private sealed class Fixture : IDisposable
        {
            public const string Ip = "192.168.2.20", OtherIp = "192.168.2.21";
            private long ticks = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            public DateTime Now { get { return new DateTime(Interlocked.Read(ref ticks), DateTimeKind.Utc); } }
            public void Advance(double seconds) { Interlocked.Add(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond)); }
            public LanInterface[] Interfaces = { Interface("in-memory", "192.168.2.10") };
            public readonly FakeFactory Factory = new FakeFactory();
            public readonly GoveeLan Lan;
            public Fixture()
            { Lan = new GoveeLan(Factory.Create, delegate { return Interfaces; }, delegate { return Now; }); Lan.Start(); }
            public EndpointToken Token { get { return Lan.CurrentEndpoint; } }
            public List<string> Commands { get { return Factory.Commands; } }
            public List<string> Destinations { get { return Factory.Destinations; } }
            public void Clear() { Commands.Clear(); Destinations.Clear(); Factory.Attempts = 0; }
            public void Open(bool? original)
            { Select("Alpha", Ip); if (original.HasValue) Status(Ip, original.Value); Lan.Enable(Token, Allowed); }
            public DeviceInfo Scan(string id, string ip)
            { return Lan.AcceptDatagram(ScanBytes(id, ip), new IPEndPoint(IPAddress.Parse(ip), 4003), Lan.Generation); }
            public static byte[] ScanBytes(string id, string ip)
            { return Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"ip\":\"" + ip + "\",\"device\":\"" + id + "\",\"sku\":\"H6609\"}}}"); }
            public void Select(string id, string ip)
            { DeviceInfo device = Scan(id, ip); Check(device != null, "In-memory matching scan accepted."); Lan.SetTarget(device); }
            public void Status(string ip, bool on)
            {
                string json = "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":" + (on ? "1" : "0") + ",\"brightness\":17}}}";
                Check(Lan.AcceptDatagram(Encoding.UTF8.GetBytes(json), new IPEndPoint(IPAddress.Parse(ip), 4003), Lan.Generation) != null, "In-memory status accepted.");
            }
            public void Recover(string id, string ip)
            { Advance(2.1); Lan.MaintainTransport(true); Select(id, ip); }
            public static LanInterface Interface(string id, string ip)
            {
                byte[] bytes = IPAddress.Parse(ip).GetAddressBytes(); bytes[3] = 255;
                return new LanInterface { Id = id, Address = IPAddress.Parse(ip), Mask = new byte[] { 255, 255, 255, 0 }, Broadcast = new IPAddress(bytes), Route = "192.168.2.1" };
            }
            public void Dispose()
            {
                Lan.Dispose(); Check(Factory.AllClosed, "Every in-memory socket closed.");
                foreach (string command in Factory.History)
                    Check(!command.Contains("\"cmd\":\"brightness\""), "No global brightness mutation.");
            }
        }
        private sealed class FakeFactory
        {
            public int Attempts, FailAttempt;
            public Action AfterSend;
            public readonly List<string> Commands = new List<string>(), Destinations = new List<string>(), History = new List<string>();
            private readonly List<FakeSocket> sockets = new List<FakeSocket>();
            public bool AllClosed { get { lock (sockets) { foreach (FakeSocket socket in sockets) if (!socket.Closed) return false; return true; } } }
            public ILanDatagramSocket Create(bool reply)
            { FakeSocket socket = new FakeSocket(this); lock (sockets) sockets.Add(socket); return socket; }
        }
        private sealed class FakeSocket : ILanDatagramSocket
        {
            private readonly FakeFactory owner;
            private readonly ManualResetEvent closed = new ManualResetEvent(false);
            public volatile bool Closed;
            public FakeSocket(FakeFactory factory) { owner = factory; }
            public void Send(byte[] bytes, IPEndPoint destination)
            {
                if (Closed) throw new ObjectDisposedException("in-memory socket");
                owner.Attempts++;
                if (owner.FailAttempt != 0 && owner.Attempts == owner.FailAttempt)
                { owner.FailAttempt = 0; throw new SocketException((int)SocketError.NetworkDown); }
                string json = Encoding.UTF8.GetString(bytes);
                owner.Commands.Add(json); owner.History.Add(json); owner.Destinations.Add(destination.Address.ToString());
                Action after = owner.AfterSend; if (after != null) after();
            }
            public byte[] Receive(out IPEndPoint source)
            {
                source = null; closed.WaitOne(10);
                if (Closed) throw new ObjectDisposedException("in-memory socket");
                throw new SocketException((int)SocketError.TimedOut);
            }
            public void Join(LanInterface local) { }
            public void ConfigureMulticast(LanInterface local) { }
            public void Dispose() { Closed = true; closed.Set(); }
        }
    }
}
