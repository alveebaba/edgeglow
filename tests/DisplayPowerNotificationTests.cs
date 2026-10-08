using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EdgeGlow
{
    // Exercises the production helper with injected native registration. These
    // tests never subscribe to Windows events or change a monitor, app or strip.
    public static class DisplayPowerNotificationTests
    {
        private static int passed, failed;
        public static int Main(string[] args)
        {
            Run("initial Off callback is accepted during registration", InitialOff);
            Run("registration without a report remains Unknown", InitiallyUnknown);
            Run("duplicates are suppressed and Dimmed is distinct", StateTransitions);
            Run("wrong type, GUID, length and DWORD are ignored", InvalidReports);
            Run("registration error warns once and remains usable", RegistrationError);
            Run("unavailable native API warns without throwing", UnavailableApi);
            Run("disposal unregisters its token once and ignores late callbacks", Disposal);
            Run("delegate is rooted for the complete native registration", DelegateRooting);
            Run("successful unregister releases the native delegate root", ReleasedAfterDisposal);
            Run("failed unregister retains a safe disposed callback", FailedUnregister);
            Run("receiver exceptions do not escape the native boundary", ThrowingReceiver);
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed. Display-power parsing and lifecycle with a fake native API; no physical monitor or strip claim.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + name + " -- " + ex.Message); }
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void CheckStates(List<int> values, params int[] expected)
        {
            Check(values.Count == expected.Length, "unexpected state notification count: " + values.Count);
            for (int i = 0; i < expected.Length; i++) Check(values[i] == expected[i], "wrong state at index " + i);
        }
        private static void InitialOff()
        {
            FakeNative native = new FakeNative(); List<int> states = new List<int>();
            native.InitialState = 0;
            native.BeforeRegisterReturns = delegate { CheckStates(states, 0); };
            using (DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, null)) {
                Check(native.RegisterCalls == 1 && monitor.Registered && monitor.State == 0, "initial Off was not established before startup");
                Check(native.Setting == DisplayPowerMonitor.SessionDisplayStatus && native.Flags == 2, "wrong session GUID or callback recipient flags");
            }
        }
        private static void InitiallyUnknown()
        {
            FakeNative native = new FakeNative(); List<int> states = new List<int>();
            using (DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, null)) {
                Check(monitor.Registered && monitor.State == -1 && states.Count == 0, "registration success falsely assumed display On");
            }
        }
        private static void StateTransitions()
        {
            FakeNative native = new FakeNative(); List<int> states = new List<int>();
            using (DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, null)) {
                foreach (int state in new int[] { 0, 0, 1, 1, 2, 2, 0 }) native.Emit(state);
                CheckStates(states, 0, 1, 2, 0);
                Check(monitor.State == 0, "latest valid state was lost");
            }
        }
        private static void InvalidReports()
        {
            FakeNative native = new FakeNative(); List<int> states = new List<int>();
            using (DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, null)) {
                native.EmitRaw(0, DisplayPowerMonitor.SessionDisplayStatus, 4, 0);
                native.EmitRaw(DisplayPowerMonitor.PowerSettingChange, Guid.Empty, 4, 0);
                foreach (uint length in new uint[] { 0, 1, 3, 5, 8, uint.MaxValue })
                    native.EmitRaw(DisplayPowerMonitor.PowerSettingChange, DisplayPowerMonitor.SessionDisplayStatus, length, 0);
                native.EmitRaw(DisplayPowerMonitor.PowerSettingChange, DisplayPowerMonitor.SessionDisplayStatus, 4, 256);
                native.EmitRaw(DisplayPowerMonitor.PowerSettingChange, DisplayPowerMonitor.SessionDisplayStatus, 4, uint.MaxValue);
                Check(native.Callback()(IntPtr.Zero, DisplayPowerMonitor.PowerSettingChange, IntPtr.Zero) == 0, "null message was not safely ignored");
                Check(monitor.State == -1 && states.Count == 0, "invalid message changed display state");
                native.Emit(1); CheckStates(states, 1);
            }
        }
        private static void RegistrationError()
        {
            FakeNative native = new FakeNative(); native.RegisterError = 5;
            List<int> states = new List<int>(); List<string> warnings = new List<string>();
            DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, warnings.Add);
            Check(!monitor.Registered && monitor.State == -1 && warnings.Count == 1, "registration failure did not remain Unknown with one warning");
            Check(warnings[0].Contains("registration returned 5"), "warning omitted the native error");
            native.Emit(0); CheckStates(states);
            monitor.Dispose(); monitor.Dispose();
            Check(native.UnregisterCalls == 0 && warnings.Count == 1, "failed registration acquired a cleanup obligation or duplicate warning");
        }
        private static void UnavailableApi()
        {
            FakeNative native = new FakeNative(); native.RegisterException = new EntryPointNotFoundException("injected unavailable API");
            List<int> states = new List<int>(); int warnings = 0;
            using (DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, delegate(string text) { warnings++; throw new InvalidOperationException("injected warning sink"); })) {
                Check(!monitor.Registered && monitor.State == -1 && warnings == 1 && states.Count == 0, "unavailable registration failed to degrade safely");
            }
            Check(native.UnregisterCalls == 0 && warnings == 1, "unavailable API retried cleanup or warning");
        }
        private static void Disposal()
        {
            FakeNative native = new FakeNative(); native.InitialState = 0;
            List<int> states = new List<int>();
            DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, null);
            DisplayPowerNotificationCallback saved = native.Callback();
            native.DuringUnregister = delegate { native.Emit(1); Check(monitor.IsDisposed && !monitor.Registered, "helper not detached before native unregister"); };
            monitor.Dispose(); monitor.Dispose();
            Check(native.UnregisterCalls == 1 && native.UnregisteredToken == native.Token, "wrong registration handle or repeated unregister");
            using (Report report = new Report(DisplayPowerMonitor.SessionDisplayStatus, 4, 2))
                Check(saved(IntPtr.Zero, DisplayPowerMonitor.PowerSettingChange, report.Pointer) == 0, "late callback returned an error");
            CheckStates(states, 0);
            Check(monitor.State == 0, "disposed callback changed the remembered state");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateUnheldMonitor(FakeNative native, List<int> states)
        { return new WeakReference(new DisplayPowerMonitor(native, states.Add, null)); }
        private static void Collect()
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateDisposedMonitor(FakeNative native, List<int> states, List<string> warnings)
        {
            DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, states.Add, warnings.Add);
            native.Emit(1); monitor.Dispose(); monitor.Dispose();
            return new WeakReference(monitor);
        }
        private static void DelegateRooting()
        {
            FakeNative native = new FakeNative(); List<int> states = new List<int>();
            WeakReference owner = CreateUnheldMonitor(native, states);
            Collect();
            Check(owner.IsAlive && native.CallbackReference.IsAlive, "registration did not root its owner and callback delegate");
            native.Emit(2); CheckStates(states, 2);
            DisplayPowerMonitor monitor = (DisplayPowerMonitor)owner.Target;
            Check(monitor != null && monitor.Registered, "rooted callback owner is unavailable");
            monitor.Dispose(); Check(native.UnregisterCalls == 1, "rooted subscription did not unregister");
            GC.KeepAlive(monitor);
        }
        private static void ReleasedAfterDisposal()
        {
            FakeNative native = new FakeNative(); List<int> states = new List<int>(); List<string> warnings = new List<string>();
            WeakReference owner = CreateDisposedMonitor(native, states, warnings);
            Collect();
            Check(!owner.IsAlive && !native.CallbackReference.IsAlive, "successful native removal retained its delegate GC root");
            Check(native.UnregisterCalls == 1 && warnings.Count == 0, "successful cleanup retried or warned");
            CheckStates(states, 1);
        }
        private static void FailedUnregister()
        {
            FakeNative native = new FakeNative(); native.UnregisterError = 5;
            List<int> states = new List<int>(); List<string> warnings = new List<string>();
            WeakReference owner = CreateDisposedMonitor(native, states, warnings);
            Collect();
            Check(owner.IsAlive && native.CallbackReference.IsAlive, "failed native removal did not retain its callback root");
            DisplayPowerMonitor monitor = (DisplayPowerMonitor)owner.Target;
            native.Emit(0);
            CheckStates(states, 1);
            Check(native.CallbackReference.IsAlive && native.UnregisterCalls == 1 && monitor.IsDisposed && !monitor.Registered,
                "failed removal lost the delegate root, retried disposal or left callbacks enabled");
            Check(warnings.Count == 1 && warnings[0].Contains("cleanup returned 5"), "failed unregister did not report its error once");
            GC.KeepAlive(monitor);
        }
        private static void ThrowingReceiver()
        {
            FakeNative native = new FakeNative(); int calls = 0, warnings = 0;
            using (DisplayPowerMonitor monitor = new DisplayPowerMonitor(native, delegate(int value) { calls++; throw new InvalidOperationException("injected receiver failure"); }, delegate(string text) { warnings++; })) {
                native.Emit(0); native.Emit(0); native.Emit(1);
                Check(calls == 2 && warnings == 0 && monitor.State == 1, "receiver failure escaped, caused logging or broke duplicate suppression");
            }
        }
        private sealed class Report : IDisposable
        {
            public readonly IntPtr Pointer;
            public Report(Guid setting, uint length, uint value)
            {
                Pointer = Marshal.AllocHGlobal(length == 4 ? 24 : 20);
                byte[] guid = setting.ToByteArray(); Marshal.Copy(guid, 0, Pointer, guid.Length);
                Marshal.WriteInt32(Pointer, 16, unchecked((int)length));
                if (length == 4) Marshal.WriteInt32(Pointer, 20, unchecked((int)value));
            }
            public void Dispose() { Marshal.FreeHGlobal(Pointer); }
        }
        private sealed class FakeNative : IDisplayPowerNative
        {
            public readonly IntPtr Token = new IntPtr(123456);
            public WeakReference CallbackReference;
            public Guid Setting;
            public uint Flags, RegisterError, UnregisterError;
            public int RegisterCalls, UnregisterCalls;
            public int? InitialState;
            public Exception RegisterException;
            public Action BeforeRegisterReturns, DuringUnregister;
            public IntPtr UnregisteredToken;
            public uint Register(Guid setting, uint flags, DisplayPowerNotificationCallback callback, out IntPtr registration)
            {
                RegisterCalls++; Setting = setting; Flags = flags;
                CallbackReference = new WeakReference(callback);
                registration = RegisterError == 0 ? Token : IntPtr.Zero;
                if (RegisterException != null) throw RegisterException;
                if (InitialState.HasValue) Emit(InitialState.Value);
                if (BeforeRegisterReturns != null) BeforeRegisterReturns();
                GC.KeepAlive(callback);
                return RegisterError;
            }
            public uint Unregister(IntPtr registration)
            {
                UnregisterCalls++; UnregisteredToken = registration;
                if (DuringUnregister != null) DuringUnregister();
                return UnregisterError;
            }
            public DisplayPowerNotificationCallback Callback()
            {
                DisplayPowerNotificationCallback callback = (DisplayPowerNotificationCallback)CallbackReference.Target;
                Check(callback != null, "native callback delegate was collected"); return callback;
            }
            public void Emit(int state)
            { EmitRaw(DisplayPowerMonitor.PowerSettingChange, DisplayPowerMonitor.SessionDisplayStatus, 4, unchecked((uint)state)); }
            public void EmitRaw(uint type, Guid setting, uint length, uint value)
            {
                using (Report report = new Report(setting, length, value))
                    Check(Callback()(IntPtr.Zero, type, report.Pointer) == 0, "native callback returned an unexpected error");
            }
        }
    }
}
