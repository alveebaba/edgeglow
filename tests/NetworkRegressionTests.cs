using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace EdgeGlow
{
    // Compiles with the real Network.cs. No production UdpLanSocket is constructed.
    internal static class NetworkRegressionTests
    {
        private static int checks;
        private static void Check(bool condition, string name)
        { checks++; if (!condition) throw new Exception(name); }
        private static void Throws(Action action, string name)
        { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } Check(failed, name); }
        private static void Wait(Func<bool> condition, string name)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < 1500) Thread.Sleep(10);
            Check(condition(), name);
        }
        private static DeviceInfo Saved(DeviceInfo d) { return d.Copy(); }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--c02") { ConflictSurvivesRecovery(); Console.WriteLine("PASS: C02 conflict retention checks: " + checks); return 0; }
                RetiredReceiverAuthority();
                CanceledFirstActivation();
                ConflictSurvivesRecovery();
                EndpointTokenTransactions();
                ParserAndIdentity();
                SessionStateAndCommands();
                EndpointReleaseAndRecovery();
                InterfaceAndSocketLifecycle();
                StartFailureAndDispose();
                Console.WriteLine("PASS: " + checks + " production network/parser/session checks. All sockets and interfaces are in-memory fakes; no LAN, lights, capture, GUI, settings or autostart.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }

        // Compatibility dispatch preserves the exact same test bytes on the old
        // source, which has only the epoch argument. No helper substitutes the
        // current epoch/socket: both were captured before transport retirement.
        private static DeviceInfo AcceptCaptured(GoveeLan lan, ILanDatagramSocket socket, int epoch, byte[] packet, string ip)
        {
            MethodInfo bound = typeof(GoveeLan).GetMethod("AcceptDatagram", BindingFlags.NonPublic | BindingFlags.Instance, null,
                new Type[] { typeof(byte[]), typeof(IPEndPoint), typeof(int), typeof(ILanDatagramSocket) }, null);
            IPEndPoint source = new IPEndPoint(IPAddress.Parse(ip), 4003);
            if (bound == null) return lan.AcceptDatagram(packet, source, epoch);
            return (DeviceInfo)bound.Invoke(lan, new object[] { packet, source, epoch, socket });
        }
        private static object ActiveReceiver(GoveeLan lan)
        { return typeof(GoveeLan).GetField("receiver", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lan); }
        private static void RetiredReceiverAuthority()
        {
            // Four small variants: blocked construction; failed construction/no
            // receiver; explicit fault retirement; and arrival after publication.
            for (int variant = 0; variant < 4; variant++)
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                if (variant == 0 || variant == 3) f.Status(a.Ip, "{\"onOff\":0}");
                if (variant == 2) f.Status(a.Ip, "{\"onOff\":1}");
                f.Lan.Enable(); EndpointToken saved = f.Lan.CurrentEndpoint;
                string occupantSku = variant == 2 ? "H6008" : "H6609";
                f.Scan("B", a.Ip, occupantSku); f.ClearCommands();
                Throws(delegate { f.Lan.Disable(saved); }, "D01 setup retains blocked original release.");
                FakeSocket retired = f.Factory.LastReceiver; int capturedEpoch = f.Lan.Generation;
                byte[] heldPacket = Fixture.ScanBytes("A", a.Ip, "H6609", a.Ip);
                int auditBefore = f.Lan.ConflictAudit.Length; DeviceInfo parsed = null; bool noReceiver = false;
                if (variant < 2)
                {
                    using (ManualResetEvent constructionEntered = new ManualResetEvent(false))
                    using (ManualResetEvent publishAllowed = new ManualResetEvent(false))
                    {
                        if (variant == 1) f.Factory.FailReplyCreations = 1;
                        f.Factory.BeforeReplyCreate = delegate { constructionEntered.Set(); if (!publishAllowed.WaitOne(2000)) throw new Exception("D01 publication barrier timed out."); };
                        f.Interfaces = new LanInterface[] { Fixture.Interface("replacement", "192.168.2.10") };
                        Thread replacement = new Thread(delegate() { f.Lan.MaintainTransport(true); }); replacement.Start();
                        try
                        {
                            Check(constructionEntered.WaitOne(1500), "D01 factory paused after retirement and before publication.");
                            noReceiver = ActiveReceiver(f.Lan) == null;
                            parsed = AcceptCaptured(f.Lan, retired, capturedEpoch, heldPacket, a.Ip);
                        }
                        finally { f.Factory.BeforeReplyCreate = null; publishAllowed.Set(); Check(replacement.Join(1500), "D01 replacement worker completed."); }
                    }
                    if (variant == 1)
                    {
                        Check(ActiveReceiver(f.Lan) == null, "D01 construction failure leaves no active receiver.");
                        DeviceInfo failedWindow = AcceptCaptured(f.Lan, retired, capturedEpoch, heldPacket, a.Ip);
                        if (failedWindow != null) parsed = failedWindow;
                        f.Advance(2.1); f.Lan.MaintainTransport(true);
                    }
                }
                else if (variant == 2)
                {
                    typeof(GoveeLan).GetMethod("FaultTransport", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(string) }, null).Invoke(f.Lan, new object[] { "D01 injected retirement" });
                    noReceiver = ActiveReceiver(f.Lan) == null;
                    parsed = AcceptCaptured(f.Lan, retired, capturedEpoch, heldPacket, a.Ip);
                    f.Advance(2.1); f.Lan.MaintainTransport(true);
                }
                else
                {
                    f.Interfaces = new LanInterface[] { Fixture.Interface("replacement", "192.168.2.10") }; f.Lan.MaintainTransport(true);
                    parsed = AcceptCaptured(f.Lan, retired, capturedEpoch, heldPacket, a.Ip);
                }
                bool blocked = false; try { f.Lan.Disable(saved); } catch (InvalidOperationException) { blocked = true; }
                Console.WriteLine("D01 component trace: variant=" + variant + " capturedEpoch=" + capturedEpoch + " currentEpoch=" + f.Lan.Generation + " receiverAbsentAtDelivery=" + noReceiver + " retiredAccepted=" + (parsed != null) + " releaseBlocked=" + blocked + " commands=" + String.Join(";", f.Commands.ToArray()));
                Check(parsed == null && blocked && f.Commands.Count == 0, "D01 retired receiver cannot clear a conflict or authorize release.");
                Check(f.Lan.LastSeenUtc == DateTime.MinValue && f.Lan.CurrentTarget.IdentityGeneration == -1 && f.Lan.ConflictAudit.Length == auditBefore, "D01 retired bytes cannot update reachability, target authority or audit.");
                // Even a forged current numeric epoch cannot substitute receiver identity.
                Check(AcceptCaptured(f.Lan, retired, f.Lan.Generation, heldPacket, a.Ip) == null, "D01 wrong receiver rejected even if supplied epoch equals current.");
                f.Scan("B", a.Ip, occupantSku);
                Throws(delegate { f.Lan.Disable(saved); }, "D01 current conflicting occupant cannot authorize old release.");
                f.Advance(1); Check(f.Scan("A", a.Ip) != null, "D01 active receiver matching scan can revalidate.");
                f.Lan.Disable(saved);
                Check(f.Commands.Count == ((variant == 0 || variant == 3) ? 2 : 1) && f.Lan.ConflictAudit.Length == auditBefore, "D01 positive matching revalidation permits original release and retains audit.");
            }
        }

        private static void CanceledFirstActivation()
        {
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                bool canceled = false;
                try { f.Lan.Enable(f.Lan.CurrentEndpoint, delegate { return false; }); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled && f.Commands.Count == 0, "Canceled initial activation attempts no datagram.");
                f.Advance(1); f.Status(a.Ip, "{\"onOff\":0}");
                f.Lan.Enable(f.Lan.CurrentEndpoint, delegate { return true; });
                Console.WriteLine("Canceled-first trace: " + String.Join(";", f.Commands.ToArray()));
                Check(f.Commands.Count == 2 && f.Commands[0].Contains(GoveeLan.EnablePacket) && f.Commands[1].Contains("\"value\":1"), "Canceled unsent unknown snapshot cannot poison fresh known-off activation.");
                f.Lan.Disable(f.Lan.CurrentEndpoint);
                Check(f.Commands[f.Commands.Count - 2].Contains("\"value\":0"), "New authorized known-off session retains correct restoration obligation.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); EndpointToken saved = f.Lan.CurrentEndpoint;
                f.Factory.AfterSend = delegate { f.Factory.AfterSend = null; throw new SocketException((int)SocketError.NetworkDown); };
                bool failed = false;
                try { f.Lan.Enable(saved, delegate { return true; }); } catch (SocketException) { failed = true; }
                Check(failed && f.Commands.Count == 1 && f.Commands[0].Contains(GoveeLan.EnablePacket), "Possible first mode delivery is preserved as a partial activation.");
                f.Advance(2.1); f.Lan.MaintainTransport(true); f.Scan("A", a.Ip); f.Status(a.Ip, "{\"onOff\":0}");
                f.ClearCommands(); f.Lan.Enable(f.Lan.CurrentEndpoint, delegate { return true; });
                Check(f.Commands.Count == 1 && f.Count("\"cmd\":\"turn\"") == 0, "Actual partial activation retains original unknown snapshot instead of guessing later state.");
                f.Lan.Disable(saved);
                Check(f.Commands.Count == 2 && f.Commands[1].Contains(GoveeLan.DisablePacket), "Partial activation still permits original-endpoint mode release.");
            }
        }

        private static void ConflictSurvivesRecovery()
        {
            foreach (bool knownOff in new bool[] { true, false })
            foreach (bool fault in new bool[] { false, true })
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                if (knownOff) f.Status(a.Ip, "{\"onOff\":0}");
                f.Lan.Enable(); DeviceInfo saved = Saved(f.Lan.CurrentTarget);
                f.Scan("B", a.Ip); f.ClearCommands();
                Throws(delegate { f.Lan.Disable(saved); }, "Conflict blocks release before recovery.");
                Check(f.Commands.Count == 0, "Conflict emitted no control before recovery.");
                if (fault)
                {
                    f.Factory.LastReceiver.InjectReceiveFault();
                    Wait(delegate { return f.Factory.LastReceiver.Closed; }, "Injected receive fault closed transport.");
                    f.Advance(2.1); f.Lan.MaintainTransport(true);
                }
                else
                {
                    f.Interfaces = new LanInterface[] { Fixture.Interface("replacement", "192.168.2.10") };
                    f.Lan.MaintainTransport(true);
                }
                bool blocked = false;
                try { f.Lan.Disable(saved); } catch (InvalidOperationException) { blocked = true; }
                Console.WriteLine("C02 trace: knownOff=" + knownOff + " fault=" + fault + " generation=" + f.Lan.Generation + " blocked=" + blocked + " commands=" + String.Join(";", f.Commands.ToArray()) + " destinations=" + String.Join(";", f.Destinations.ToArray()));
                Check(blocked && f.Commands.Count == 0, "C02: known conflict survives transport recovery with zero control sends.");
                f.Scan("B", a.Ip);
                Throws(delegate { f.Lan.Disable(saved); }, "Repeated conflicting scan never reauthorizes original endpoint.");
                Check(f.Commands.Count == 0, "Repeated conflicting identity sent nothing.");
                int auditCount = f.Lan.ConflictAudit.Length;
                f.Advance(1); f.Scan("A", a.Ip); f.Lan.Disable(saved);
                Check(auditCount > 0 && f.Lan.ConflictAudit.Length == auditCount, "Matching revalidation retains historical conflict audit.");
                Check(f.Commands.Count == (knownOff ? 2 : 1), "Fresh exact matching scan revalidates retained original release.");
            }
        }

        private static void EndpointTokenTransactions()
        {
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Lan.Enable(); EndpointToken saved = f.Lan.CurrentEndpoint;
                Check(f.Scan("B", a.Ip, "H6008") == null, "Unsupported SKU is never offered as a selectable target.");
                f.ClearCommands(); f.Interfaces = new LanInterface[] { Fixture.Interface("replacement", "192.168.2.10") }; f.Lan.MaintainTransport(true);
                Throws(delegate { f.Lan.Disable(saved); }, "A different bulb SKU at the owned address remains conflict evidence after reset.");
                Check(f.Commands.Count == 0, "Unsupported conflicting bulb receives no release commands.");
                f.Scan("A", a.Ip); f.Lan.Disable(saved);
                Check(f.Commands.Count == 1, "Exact supported identity may revalidate after an unsupported conflicting occupant.");
            }

            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0,\"brightness\":37}");
                EndpointToken token = f.Lan.CurrentEndpoint; DeviceInfo mutable = token.Snapshot;
                mutable.Id = "wrong"; mutable.Ip = "192.168.2.99"; mutable.PowerOn = true;
                Check(token.Id == "A" && token.Ip == a.Ip && token.Snapshot.PowerOn == false && token.Snapshot.Brightness == 37 && token.Snapshot.IdentityGeneration == f.Lan.Generation, "Token has a defensive complete status/identity snapshot.");
                f.Scan("A", "192.168.2.21");
                Throws(delegate { f.Lan.Enable(token, delegate { return true; }); }, "Readdressed activation rejects stale token.");
                Throws(delegate { f.Lan.SendColors(token, new Color[10], false, delegate { return true; }); }, "Readdressed color operation rejects stale token.");
                Check(f.Commands.Count == 0, "Stale token never silently redirects a datagram.");
                EndpointToken b = f.Lan.CurrentEndpoint; int authorized = 0;
                f.Lan.Enable(b, delegate { authorized++; return true; });
                f.Lan.SendColors(b, new Color[10], false, delegate { authorized++; return true; });
                Check(authorized == 2 && f.Commands.Count == 2 && f.Destinations.TrueForAll(delegate(string ip) { return ip == b.Ip; }), "Fresh explicit token activates and colors only its endpoint.");
                f.Lan.Disable(b);
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); EndpointToken token = f.Lan.CurrentEndpoint;
                object sendGate = typeof(GoveeLan).GetField("sendGate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f.Lan);
                ManualResetEvent entered = new ManualResetEvent(false); Exception failure = null; bool permitted = true;
                Thread command = new Thread(delegate() { entered.Set(); try { f.Lan.Enable(token, delegate { return permitted; }); } catch (Exception ex) { failure = ex; } });
                Monitor.Enter(sendGate);
                try { command.Start(); entered.WaitOne(); permitted = false; }
                finally { Monitor.Exit(sendGate); }
                Check(command.Join(1500), "Blocked authorization worker completed."); entered.Close();
                Check(failure is OperationCanceledException && f.Commands.Count == 0, "Authorization expiry while waiting is checked at actual send boundary.");
                permitted = true; failure = null; entered = new ManualResetEvent(false);
                command = new Thread(delegate() { entered.Set(); try { f.Lan.Enable(token, delegate { return permitted; }); } catch (Exception ex) { failure = ex; } });
                Monitor.Enter(sendGate);
                try { command.Start(); entered.WaitOne(); f.Scan("A", "192.168.2.21"); }
                finally { Monitor.Exit(sendGate); }
                Check(command.Join(1500), "Blocked retarget worker completed."); entered.Close();
                Check(failure is InvalidOperationException && f.Commands.Count == 0, "Retarget during send-lock wait rejects immutable old token.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0}"); EndpointToken token = f.Lan.CurrentEndpoint;
                int authorized = 0;
                bool canceled = false; try { f.Lan.Enable(token, delegate { return ++authorized == 1; }); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled && authorized == 2 && f.Commands.Count == 1 && f.Commands[0].Contains(GoveeLan.EnablePacket), "Power command repeats authorization after mode attempt.");
                f.Lan.Disable(token);
                Check(f.Commands.Count == 2 && f.Commands[1].Contains(GoveeLan.DisablePacket), "Canceled power command never invents a power restoration mutation.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0}"); EndpointToken token = f.Lan.CurrentEndpoint;
                f.Factory.AfterSend = delegate { f.Factory.AfterSend = null; f.Scan("A", "192.168.2.21"); };
                Throws(delegate { f.Lan.Enable(token, delegate { return true; }); }, "Retarget between mode and power blocks remaining activation command.");
                Check(f.Commands.Count == 1 && f.Destinations[0] == a.Ip, "Partial activation retained original endpoint and never woke replacement.");
                Throws(delegate { f.Lan.SendColors(token, new Color[10], false, delegate { return true; }); }, "Retarget between activation and colors blocks stale colors.");
                f.Lan.Disable(token);
                Check(f.Commands.Count == 2 && f.Destinations[1] == a.Ip, "Partial activation release remains at original endpoint.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); EndpointToken token = f.Lan.CurrentEndpoint;
                Throws(delegate { f.Lan.Enable(token, delegate { f.Scan("B", a.Ip); return true; }); }, "Reentrant authorization conflict is rechecked before send.");
                Check(f.Commands.Count == 0, "Authorization callback cannot slip a conflicting identity past validation.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); EndpointToken token = f.Lan.CurrentEndpoint;
                f.Interfaces = new LanInterface[] { Fixture.Interface("replacement", "192.168.2.10") }; f.Lan.MaintainTransport(true);
                f.Scan("A", a.Ip);
                Throws(delegate { f.Lan.Enable(token, delegate { return true; }); }, "Same-address new-generation token cannot reuse old activation authorization.");
                Check(f.Commands.Count == 0, "Generation-stale activation sent nothing.");
                f.Lan.Enable(f.Lan.CurrentEndpoint, delegate { return true; });
                Check(f.Commands.Count == 1, "Fresh matching generation can explicitly activate.");
                f.ClearCommands(); f.Advance(30); f.Lan.Disable(f.Lan.CurrentEndpoint);
                Check(f.Commands.Count == 1 && f.Commands[0].Contains(GoveeLan.DisablePacket), "Reply-only outage without conflict still permits retained best-effort release.");
            }
        }

        private static void ParserAndIdentity()
        {
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20", "H6609", "8.8.8.8");
                Check(a != null && a.Ip == "192.168.2.20", "Actual datagram source overrides claimed IP.");
                f.Lan.SetTarget(a);
                Check(f.Scan("B", "192.168.2.21", "H6008", "192.168.2.21") == null, "Wrong SKU ignored.");
                Check(f.Scan("B", "10.2.3.4") == null, "Private but nonlocal source ignored.");
                Check(f.Scan("B", "8.8.8.8") == null, "Public source ignored.");
                Check(f.Scan("B", "192.168.2.10") == null, "Own interface source ignored.");
                Check(f.Scan("B", "192.168.2.0") == null, "Network address ignored.");
                Check(f.Scan("B", "192.168.2.255") == null, "Broadcast source ignored.");
                Check(f.Status("192.168.2.21", "{\"onOff\":1,\"brightness\":50}") == null, "Status at a new address cannot establish identity.");
                Throws(delegate { f.Lan.SetTarget(new DeviceInfo { Id = "unseen", Ip = "192.168.2.21", Sku = "H6609" }); }, "Unseen ID cannot become target.");

                DeviceInfo status = f.Status(a.Ip, "{\"onOff\":0,\"brightness\":37}");
                Check(status.PowerOn == false && status.Brightness == 37 && status.LastStatusUtc == f.Now, "Valid actual power/brightness retained.");
                f.Advance(1);
                status = f.Status(a.Ip, "{\"brightness\":44}");
                Check(status.PowerOn == null && status.Brightness == 44, "Missing power stays unknown.");
                status = f.Status(a.Ip, "{\"onOff\":1,\"brightness\":\"50\"}");
                Check(status.PowerOn == true && status.Brightness == null, "String brightness is not guessed.");
                Check(f.Status(a.Ip, "{\"onOff\":3,\"brightness\":101}") == null, "Invalid status does not refresh reachability.");
                Check(f.Lan.CurrentTarget.PowerOn == null && f.Lan.CurrentTarget.Brightness == null &&
                    f.Lan.CurrentTarget.LastStatusUtc == DateTime.MinValue, "Invalid fields clear known-state claims.");
                Check(f.Raw("{", a.Ip) == null && f.Raw("[]", a.Ip) == null && f.Raw("{\"msg\":42}", a.Ip) == null, "Malformed JSON shapes ignored.");
                Check(f.Lan.AcceptDatagram(new byte[16385], new IPEndPoint(IPAddress.Parse(a.Ip), 4003), f.Lan.Generation) == null, "Oversized datagram ignored.");
                Check(f.Lan.AcceptDatagram(null, null, f.Lan.Generation) == null, "Missing datagram ignored.");
                Check(f.Status(a.Ip, "{\"onOff\":1}") != null, "Power-only valid status accepted.");
                DateTime lastSeen = f.Lan.LastSeenUtc;
                f.Advance(2); f.Lan.Poll(); f.Lan.SendColors(new Color[10], false);
                Check(f.Lan.LastSeenUtc == lastSeen, "Sending does not invent a received timestamp.");

                f.Scan("B", "192.168.2.21");
                Check(f.Lan.CurrentTarget.Id == "A", "Unrelated scan cannot retarget selected identity.");
                f.Lan.Enable();
                DeviceInfo saved = Saved(f.Lan.CurrentTarget);
                f.Scan("B", a.Ip);
                Check(f.Lan.LastSeenUtc == DateTime.MinValue, "Conflicting identity invalidates current reachability.");
                Check(f.Status(a.Ip, "{\"onOff\":1}") == null, "Conflicting address status rejected.");
                Throws(delegate { f.Lan.SendColors(new Color[10], false); }, "Conflicting identity blocks stream sends.");
                Throws(delegate { f.Lan.Disable(saved); }, "Release cannot target another observed identity at old IP.");
            }
        }

        private static void SessionStateAndCommands()
        {
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                f.Lan.Enable();
                Check(f.Commands.Count == 1 && f.Commands[0].Contains(GoveeLan.EnablePacket), "Unknown power activation is mode-only.");
                f.Lan.Disable();
                Check(f.Commands.Count == 2 && f.Commands[1].Contains(GoveeLan.DisablePacket), "Unknown state release invents no power/brightness.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                f.Status(a.Ip, "{\"onOff\":0,\"brightness\":17}");
                f.Lan.PrepareSession();
                Check(f.Commands.Count == 0, "Preparation only snapshots.");
                f.Lan.Enable();
                Check(f.Commands.Count == 2 && f.Commands[0].Contains(GoveeLan.EnablePacket) && f.Commands[1].Contains("\"value\":1"), "Stream entered before waking a fresh known-off strip.");
                f.Lan.Enable(); f.Lan.EnableStream();
                Check(f.Commands.Count == 4 && f.Count("\"cmd\":\"turn\"") == 1, "Repeated activation does not repeat power for the same observation.");
                f.Advance(1); f.Status(a.Ip, "{\"onOff\":1,\"brightness\":17}");
                f.Lan.PrepareSession();
                f.Lan.Disable();
                Check(f.Commands[f.Commands.Count - 2].Contains("\"value\":0") && f.Commands[f.Commands.Count - 1].Contains(GoveeLan.DisablePacket), "Original known-off restored before mode release.");
                Check(f.Count("\"cmd\":\"brightness\"") == 0, "No global brightness mutation.");
                f.ClearCommands(); f.Lan.Enable(); f.Lan.Disable();
                Check(f.Count("\"cmd\":\"turn\"") == 0, "A completed session does not leak old restoration state into the next session.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                f.Status(a.Ip, "{\"onOff\":0,\"brightness\":23}"); f.Advance(6);
                f.Lan.Enable(); f.Lan.Disable();
                Check(f.Count("\"cmd\":\"turn\"") == 0, "Stale off observation cannot change power.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                f.Status(a.Ip, "{\"onOff\":1,\"brightness\":5}"); f.Lan.Enable(); f.Lan.Disable();
                Check(f.Count("\"cmd\":\"turn\"") == 0 && f.Count("\"cmd\":\"brightness\"") == 0, "Known-on dim state preserved without guesses.");
            }
        }

        private static void EndpointReleaseAndRecovery()
        {
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0}");
                DeviceInfo saved = Saved(f.Lan.CurrentTarget);
                f.Factory.FailSendContaining = "\"value\":1";
                bool failure = false; try { f.Lan.Enable(); } catch (SocketException) { failure = true; }
                Check(failure, "Uncertain turn-on send failure surfaces.");
                f.Advance(2.1); f.Lan.MaintainTransport(true); f.ClearCommands(); f.Lan.Disable(saved);
                Check(f.Commands.Count == 2 && f.Commands[0].Contains("\"value\":0"), "Restoration obligation predates uncertain turn-on send.");
            }

            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0,\"brightness\":25}"); f.Lan.Enable();
                DeviceInfo saved = Saved(f.Lan.CurrentTarget);
                DeviceInfo b = f.Scan("B", "192.168.2.21"); f.Lan.SetTarget(b);
                f.ClearCommands(); f.Lan.Disable(saved);
                Check(f.Commands.Count == 2 && f.Destinations[0] == a.Ip && f.Destinations[1] == a.Ip, "Saved endpoint release does not use current target B.");
                Check(f.Lan.CurrentTarget.Id == "B", "Saved release does not change current target.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0}"); f.Lan.Enable();
                DeviceInfo saved = Saved(f.Lan.CurrentTarget);
                f.Factory.FailSendContaining = GoveeLan.DisablePacket;
                bool failure = false; try { f.Lan.Disable(saved); } catch (SocketException) { failure = true; }
                Check(failure, "Injected release failure surfaced.");
                f.Advance(2.1); f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == 2, "Transport replaced after release send fault.");
                Check(f.Lan.LastSeenUtc == DateTime.MinValue, "Replacement does not invent liveness.");
                Check(f.Status(a.Ip, "{\"onOff\":1}") == null, "New transport requires matching scan before status.");
                Throws(delegate { f.Lan.EnableStream(); }, "New transport blocks unverified stream activation.");
                f.ClearCommands(); f.Lan.Disable(saved);
                Check(f.Commands.Count == 2 && f.Commands[0].Contains("\"value\":0") && f.Commands[1].Contains(GoveeLan.DisablePacket), "Pending restoration survived generation and failed disable.");
            }
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a); f.Status(a.Ip, "{\"onOff\":0}"); f.Lan.Enable();
                DeviceInfo saved = Saved(f.Lan.CurrentTarget);
                f.Interfaces = new LanInterface[] { Fixture.Interface("other", "192.168.3.10") };
                f.Lan.MaintainTransport(true);
                f.ClearCommands();
                Throws(delegate { f.Lan.Disable(saved); }, "Off-subnet pending release retained instead of sent.");
                Check(f.Commands.Count == 0, "Off-subnet release sent nothing.");
                f.Interfaces = new LanInterface[] { Fixture.Interface("restored", "192.168.2.10") };
                f.Lan.MaintainTransport(true); f.Lan.Disable(saved);
                Check(f.Commands.Count == 2, "Restoration obligation survives interface disappearance.");
            }
        }

        private static void InterfaceAndSocketLifecycle()
        {
            using (Fixture f = new Fixture())
            {
                DeviceInfo a = f.Scan("A", "192.168.2.20"); f.Lan.SetTarget(a);
                FakeSocket first = f.Factory.LastReceiver;
                f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == 1, "Unchanged interfaces do not recreate sockets.");
                f.Interfaces = new LanInterface[0]; f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == 2 && first.Closed, "Removed interface closes its old memberships/socket.");
                f.Interfaces = new LanInterface[] { Fixture.Interface("replacement-adapter", "192.168.2.10") };
                f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == 3 && f.Factory.LastReceiver.Joined.Contains("replacement-adapter|"), "Same-address new adapter gets a fresh socket/membership.");
                int generation = f.Lan.Generation;
                Check(f.Lan.AcceptDatagram(Fixture.ScanBytes("A", a.Ip, "H6609", a.Ip), new IPEndPoint(IPAddress.Parse(a.Ip), 4003), generation - 1) == null, "Old socket datagram cannot update new generation.");
                Thread.Sleep(80);
                f.Advance(2.1); f.Lan.MaintainTransport(false);
                Check(f.Lan.Generation == generation, "Old socket close does not tear down its replacement.");
                f.Interfaces[0].Route = "192.168.2.254"; f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == generation + 1, "Gateway change replaces socket generation.");
            }
            using (Fixture f = new Fixture())
            {
                FakeSocket first = f.Factory.LastReceiver;
                first.InjectReceiveFault();
                Wait(delegate { return first.Closed; }, "Fatal receive fault closes socket.");
                int creates = f.Factory.Created;
                f.Lan.MaintainTransport(true); f.Lan.MaintainTransport(true);
                Check(f.Factory.Created == creates, "Receive failure recovery is throttled before deadline.");
                f.Advance(2.1); f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == 2 && f.Factory.LastReceiver != first, "Fatal receiver gets a new socket.");
                f.Factory.LastReceiver.InjectReceiveFault();
                Wait(delegate { return f.Factory.LastReceiver.Closed; }, "Second fatal receiver closed.");
                f.Factory.FailReplyCreations = 2;
                f.Advance(2.1); f.Lan.MaintainTransport(true);
                int afterFailure = f.Factory.Created;
                for (int i = 0; i < 20; i++) f.Lan.MaintainTransport(true);
                Check(f.Factory.Created == afterFailure, "Persistent bind failure does not spin/create retry storm.");
                f.Advance(2.1); f.Lan.MaintainTransport(true);
                Check(f.Factory.Created == afterFailure + 1, "Bind retries once at next deadline.");
                f.Advance(2.1); f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation == 3, "Repeated bind failures eventually recover.");
            }
        }

        private static void StartFailureAndDispose()
        {
            FakeFactory factory = new FakeFactory(); factory.FailReplyCreations = 1;
            LanInterface[] interfaces = { Fixture.Interface("fake", "192.168.2.10") };
            GoveeLan lan = new GoveeLan(factory.Create, delegate { return interfaces; }, delegate { return new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc); });
            bool failed = false; try { lan.Start(); } catch (IOException) { failed = true; }
            Check(failed, "Initial bind conflict surfaces to engine.");
            lan.Start(); Check(lan.Generation == 1, "Repeated Start succeeds after initial bind failure.");
            Thread worker = (Thread)typeof(GoveeLan).GetField("receiveThread", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lan);
            Stopwatch timer = Stopwatch.StartNew(); lan.Dispose(); lan.Dispose();
            Check(timer.ElapsedMilliseconds < 1500 && !worker.IsAlive, "Repeated Dispose stops fake receiver within bound.");
            Check(factory.AllClosed, "No fake socket leaked by start/fail/dispose.");
            Check((bool)typeof(GoveeLan).GetField("stopHandleClosed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lan), "Disposed transport releases stop handle after receiver exits.");
            using (Fixture f = new Fixture())
            {
                f.Lan.Discover(); f.Lan.Dispose();
                Wait(delegate { return (bool)typeof(GoveeLan).GetField("stopHandleClosed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f.Lan); }, "Discovery disposal defers handle close until every worker exits.");
            }


            FakeFactory senderFailure = new FakeFactory(); senderFailure.FailSenderCreations = 1;
            using (GoveeLan failedLan = new GoveeLan(senderFailure.Create, delegate { return interfaces; }, delegate { return DateTime.UtcNow; }))
            {
                failed = false; try { failedLan.Start(); } catch (IOException) { failed = true; }
                Check(failed && senderFailure.AllClosed, "Sender creation failure closes already-bound receiver.");
            }
        }

        private sealed class Fixture : IDisposable
        {
            private long ticks = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            public DateTime Now { get { return new DateTime(Interlocked.Read(ref ticks), DateTimeKind.Utc); } }
            public void Advance(double seconds) { Interlocked.Add(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond)); }
            public LanInterface[] Interfaces = { Interface("fake-adapter", "192.168.2.10") };
            public readonly FakeFactory Factory = new FakeFactory();
            public readonly GoveeLan Lan;
            public Fixture()
            {
                Lan = new GoveeLan(Factory.Create, delegate { return Interfaces; }, delegate { return Now; }); Lan.Start();
            }
            public List<string> Commands { get { return Factory.Commands; } }
            public List<string> Destinations { get { return Factory.Destinations; } }
            public int Count(string text) { int count = 0; foreach (string command in Commands) if (command.Contains(text)) count++; return count; }
            public void ClearCommands() { Factory.Commands.Clear(); Factory.Destinations.Clear(); }
            public DeviceInfo Scan(string id, string ip, string sku = "H6609", string advertised = null)
            { return Lan.AcceptDatagram(ScanBytes(id, ip, sku, advertised ?? ip), new IPEndPoint(IPAddress.Parse(ip), 4003), Lan.Generation); }
            public static byte[] ScanBytes(string id, string ip, string sku, string advertised)
            { return Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"ip\":\"" + advertised + "\",\"device\":\"" + id + "\",\"sku\":\"" + sku + "\"}}}"); }
            public DeviceInfo Status(string ip, string data) { return Raw("{\"msg\":{\"cmd\":\"devStatus\",\"data\":" + data + "}}", ip); }
            public DeviceInfo Raw(string json, string ip)
            { return Lan.AcceptDatagram(Encoding.UTF8.GetBytes(json), new IPEndPoint(IPAddress.Parse(ip), 4003), Lan.Generation); }
            public static LanInterface Interface(string id, string ip)
            {
                byte[] bytes = IPAddress.Parse(ip).GetAddressBytes(); bytes[3] = 255;
                return new LanInterface { Id = id, Address = IPAddress.Parse(ip), Mask = new byte[] { 255, 255, 255, 0 }, Broadcast = new IPAddress(bytes), Route = "192.168.2.1" };
            }
            public void Dispose() { Lan.Dispose(); Check(Factory.AllClosed, "Fixture closed every created fake socket."); }
        }
        private sealed class FakeFactory
        {
            public int Created, FailReplyCreations, FailSenderCreations;
            public string FailSendContaining;
            public Action AfterSend, BeforeReplyCreate;
            public readonly List<string> Commands = new List<string>(), Destinations = new List<string>();
            private readonly List<FakeSocket> sockets = new List<FakeSocket>();
            public FakeSocket LastReceiver;
            public bool AllClosed { get { lock (sockets) { foreach (FakeSocket s in sockets) if (!s.Closed) return false; return true; } } }
            public ILanDatagramSocket Create(bool reply)
            {
                Interlocked.Increment(ref Created);
                Action before = BeforeReplyCreate; if (reply && before != null) before();
                if (reply && FailReplyCreations > 0) { FailReplyCreations--; throw new IOException("simulated bind conflict"); }
                if (!reply && FailSenderCreations > 0) { FailSenderCreations--; throw new IOException("simulated sender creation failure"); }
                FakeSocket socket = new FakeSocket(this);
                lock (sockets) sockets.Add(socket);
                if (reply) LastReceiver = socket;
                return socket;
            }
        }
        private sealed class FakeSocket : ILanDatagramSocket
        {
            private readonly FakeFactory owner;
            private readonly ManualResetEvent ready = new ManualResetEvent(false);
            public volatile bool Closed;
            private int failReceive;
            public string Joined = "";
            public FakeSocket(FakeFactory factory) { owner = factory; }
            public void InjectReceiveFault() { Interlocked.Exchange(ref failReceive, 1); ready.Set(); }
            public void Send(byte[] bytes, IPEndPoint destination)
            {
                if (Closed) throw new ObjectDisposedException("fake socket");
                string json = Encoding.UTF8.GetString(bytes);
                if (owner.FailSendContaining != null && json.Contains(owner.FailSendContaining))
                { owner.FailSendContaining = null; throw new SocketException((int)SocketError.NetworkDown); }
                owner.Commands.Add(json); owner.Destinations.Add(destination.Address.ToString());
                Action after = owner.AfterSend; if (after != null) after();
            }
            public byte[] Receive(out IPEndPoint source)
            {
                source = null; ready.WaitOne(25);
                if (Closed) throw new ObjectDisposedException("fake socket");
                if (Interlocked.Exchange(ref failReceive, 0) != 0) { ready.Reset(); throw new SocketException((int)SocketError.NotSocket); }
                throw new SocketException((int)SocketError.TimedOut);
            }
            public void Join(LanInterface local) { Joined += local.Key + "\n"; }
            public void ConfigureMulticast(LanInterface local) { }
            public void Dispose() { Closed = true; ready.Set(); }
        }
    }
}
