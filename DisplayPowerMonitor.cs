using System;
using System.Runtime.InteropServices;

namespace EdgeGlow
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint DisplayPowerNotificationCallback(IntPtr context, uint type, IntPtr setting);

    internal interface IDisplayPowerNative
    {
        uint Register(Guid setting, uint flags, DisplayPowerNotificationCallback callback, out IntPtr registration);
        uint Unregister(IntPtr registration);
    }

    internal sealed class DisplayPowerMonitor : IDisposable
    {
        internal static readonly Guid SessionDisplayStatus = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
        internal const uint PowerSettingChange = 0x8013;
        internal const uint CallbackRecipient = 2;
        private readonly object gate = new object();
        private readonly IDisplayPowerNative native;
        private readonly Action<int> changed;
        private readonly Action<string> warning;
        private readonly DisplayPowerNotificationCallback callback;
        private GCHandle callbackRoot;
        private IntPtr registration;
        private bool disposed, warned, accepting = true;
        private int state = -1;

        public DisplayPowerMonitor(Action<int> changed, Action<string> warning)
            : this(new WindowsDisplayPowerNative(), changed, warning) { }

        internal DisplayPowerMonitor(IDisplayPowerNative native, Action<int> changed, Action<string> warning)
        {
            if (native == null) throw new ArgumentNullException("native");
            if (changed == null) throw new ArgumentNullException("changed");
            this.native = native; this.changed = changed; this.warning = warning;
            callback = OnNotification;
            // The native function pointer does not keep a managed delegate alive.
            // Root it before Register, which may deliver the current state immediately.
            callbackRoot = GCHandle.Alloc(callback);
            try {
                IntPtr token;
                uint error = native.Register(SessionDisplayStatus, CallbackRecipient, callback, out token);
                if (error == 0 && token != IntPtr.Zero) {
                    lock (gate) registration = token;
                    return;
                }
                lock (gate) accepting = false;
                FreeCallbackRoot();
                WarnOnce("Display power notifications unavailable: registration returned " + error +
                    (token == IntPtr.Zero ? " with no registration handle." : "."));
            }
            catch (Exception ex) {
                lock (gate) accepting = false;
                FreeCallbackRoot();
                WarnOnce("Display power notifications unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal int State { get { lock (gate) return state; } }
        internal bool Registered { get { lock (gate) return registration != IntPtr.Zero; } }
        internal bool IsDisposed { get { lock (gate) return disposed; } }

        private uint OnNotification(IntPtr context, uint type, IntPtr setting)
        {
            lock (gate) {
                if (disposed || !accepting) return 0;
                int value;
                if (!TryReadState(type, setting, out value) || value == state) return 0;
                state = value;
                // The receiver only changes engine flags. Never perform capture,
                // LAN sends or logging on this native callback thread.
                try { changed(value); }
                catch { /* A managed exception must never cross the native callback boundary. */ }
            }
            return 0;
        }

        internal static bool TryReadState(uint type, IntPtr setting, out int value)
        {
            value = -1;
            if (type != PowerSettingChange || setting == IntPtr.Zero) return false;
            // POWERBROADCAST_SETTING is GUID (16), DWORD length (4), then data.
            // Its DWORD data starts at offset 20 on both x86 and x64.
            PowerSettingHeader header = (PowerSettingHeader)Marshal.PtrToStructure(setting, typeof(PowerSettingHeader));
            if (header.Setting != SessionDisplayStatus || header.DataLength != 4) return false;
            uint raw = unchecked((uint)Marshal.ReadInt32(setting, 20));
            if (raw > 2) return false;
            value = (int)raw;
            return true;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerSettingHeader
        {
            public Guid Setting;
            public uint DataLength;
        }

        private void WarnOnce(string text)
        {
            lock (gate) { if (warned) return; warned = true; }
            if (warning != null) {
                try { warning(text + " Screen syncing remains available."); }
                catch { /* A diagnostic sink must not make registration failure fatal. */ }
            }
        }

        private void FreeCallbackRoot()
        {
            lock (gate) {
                if (callbackRoot.IsAllocated) { callbackRoot.Free(); callbackRoot = default(GCHandle); }
            }
        }

        public void Dispose()
        {
            IntPtr token;
            lock (gate) {
                if (disposed) return;
                disposed = true; accepting = false;
                token = registration; registration = IntPtr.Zero;
            }
            if (token == IntPtr.Zero) return;
            // Unregister may wait for callbacks. Do not hold gate while calling it.
            try {
                uint error = native.Unregister(token);
                if (error == 0) FreeCallbackRoot();
                else WarnOnce("Display power notification cleanup returned " + error + ".");
            }
            catch (Exception ex) { WarnOnce("Display power notification cleanup failed: " + ex.GetType().Name + ": " + ex.Message); }
            // If removal failed, retain the GCHandle until process exit so any late
            // native call still reaches a rooted delegate and is ignored as disposed.
            GC.KeepAlive(callback);
        }
    }

    internal sealed class WindowsDisplayPowerNative : IDisplayPowerNative
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SubscribeParameters
        {
            public IntPtr Callback;
            public IntPtr Context;
        }

        // Microsoft documents an immediate current-state callback for this API:
        // https://learn.microsoft.com/windows/win32/api/powersetting/nf-powersetting-powersettingregisternotification
        [DllImport("Powrprof.dll")]
        private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags,
            ref SubscribeParameters recipient, out IntPtr registration);
        [DllImport("Powrprof.dll")]
        private static extern uint PowerSettingUnregisterNotification(IntPtr registration);

        public uint Register(Guid setting, uint flags, DisplayPowerNotificationCallback callback, out IntPtr registration)
        {
            SubscribeParameters recipient = new SubscribeParameters {
                Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero
            };
            uint result = PowerSettingRegisterNotification(ref setting, flags, ref recipient, out registration);
            GC.KeepAlive(callback);
            return result;
        }
        public uint Unregister(IntPtr registration) { return PowerSettingUnregisterNotification(registration); }
    }
}
