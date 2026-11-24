using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AirPods Audio Keep-Alive")]
[assembly: AssemblyDescription("Keeps the selected AirPods playback stream open while the desktop is in use.")]

internal static class Program
{
    private const string InstanceName = @"Local\AirPodsAudioKeepAlive.Instance";
    private const string ActiveName = @"Local\AirPodsAudioKeepAlive.Rendering";
    private const int RenderFlow = 0;
    private const int MultimediaRole = 1;
    private const int DeviceActive = 1;
    private const int SharedMode = 0;
    private const uint EventCallback = 0x00040000;
    private const uint NoPersist = 0x00080000;
    private const float SignalAmplitude = 0.002f;
    private static readonly Guid FloatSubformat = new Guid("00000003-0000-0010-8000-00aa00389b71");

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length != 1 || String.IsNullOrWhiteSpace(arguments[0]))
            return 2;

        bool created;
        using (var instance = new Mutex(false, InstanceName, out created))
        {
            if (!created)
                return 0;

            try
            {
                using (var active = new EventWaitHandle(false, EventResetMode.ManualReset, ActiveName))
                using (var power = new DesktopActivity())
                {
                    string deviceName = arguments[0];
                    var audioThread = new Thread(() => TrackOutput(deviceName, power, active));
                    audioThread.IsBackground = true;
                    audioThread.SetApartmentState(ApartmentState.STA);
                    audioThread.Start();
                    Application.Run();
                }
            }
            catch (Win32Exception error)
            {
                return error.NativeErrorCode == 0 ? 1 : error.NativeErrorCode;
            }
        }
        return 0;
    }

    private static void TrackOutput(string deviceName, DesktopActivity power, EventWaitHandle active)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        while (true)
        {
            IMMDevice output = null;
            try
            {
                if (power.AllowsAudio &&
                    enumerator.GetDefaultAudioEndpoint(RenderFlow, MultimediaRole, out output) == 0 &&
                    output != null &&
                    String.Equals(deviceName, ReadEndpointName(output), StringComparison.OrdinalIgnoreCase))
                {
                    KeepOpen(enumerator, output, power, active);
                }
            }
            catch (COMException)
            {
                // A reconnect or an exclusive stream can invalidate the shared stream.
            }
            finally
            {
                if (output != null)
                    Marshal.ReleaseComObject(output);
            }
            Thread.Sleep(800);
        }
    }

    private static string ReadEndpointName(IMMDevice output)
    {
        IPropertyStore properties;
        Marshal.ThrowExceptionForHR(output.OpenPropertyStore(0, out properties));
        var nameKey = new PropertyKey
        {
            Format = new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"),
            Id = 6
        };
        PropVariant value = new PropVariant();
        try
        {
            Marshal.ThrowExceptionForHR(properties.GetValue(ref nameKey, out value));
            return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
            Marshal.ReleaseComObject(properties);
        }
    }

    private static bool IsCurrentOutput(IMMDeviceEnumerator enumerator, string id)
    {
        IMMDevice current;
        if (enumerator.GetDefaultAudioEndpoint(RenderFlow, MultimediaRole, out current) != 0 || current == null)
            return false;

        try
        {
            string currentId;
            int state;
            return current.GetId(out currentId) == 0 &&
                String.Equals(id, currentId, StringComparison.OrdinalIgnoreCase) &&
                current.GetState(out state) == 0 && state == DeviceActive;
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }
    }

    private static void KeepOpen(IMMDeviceEnumerator enumerator, IMMDevice output,
        DesktopActivity power, EventWaitHandle active)
    {
        string id;
        int state;
        if (output.GetState(out state) != 0 || state != DeviceActive || output.GetId(out id) != 0)
            return;

        var clientId = typeof(IAudioClient).GUID;
        object clientObject;
        Marshal.ThrowExceptionForHR(output.Activate(ref clientId, 23, IntPtr.Zero, out clientObject));
        var client = (IAudioClient)clientObject;
        object rendererObject = null;
        bool started = false;

        using (var ready = new AutoResetEvent(false))
        {
            try
            {
                IntPtr format;
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
                int rate;
                int channels;
                try
                {
                    int tag = (ushort)Marshal.ReadInt16(format);
                    channels = (ushort)Marshal.ReadInt16(format, 2);
                    rate = Marshal.ReadInt32(format, 4);
                    int blockAlign = (ushort)Marshal.ReadInt16(format, 12);
                    int bits = (ushort)Marshal.ReadInt16(format, 14);
                    bool isFloat = tag == 3 ||
                        (tag == 0xfffe && Marshal.ReadInt16(format, 16) >= 22 &&
                         (Guid)Marshal.PtrToStructure(IntPtr.Add(format, 24), typeof(Guid)) == FloatSubformat);
                    if (!isFloat || channels != 2 || bits != 32 || blockAlign != 8 || rate < 8000 || rate > 192000)
                        return;

                    Marshal.ThrowExceptionForHR(client.Initialize(SharedMode, EventCallback | NoPersist,
                        0, 0, format, IntPtr.Zero));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(format);
                }

                Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
                uint capacity;
                Marshal.ThrowExceptionForHR(client.GetBufferSize(out capacity));
                var rendererId = typeof(IAudioRenderClient).GUID;
                Marshal.ThrowExceptionForHR(client.GetService(ref rendererId, out rendererObject));
                var renderer = (IAudioRenderClient)rendererObject;
                var waveform = new LowFrequencyPcm(rate, channels, capacity);
                waveform.Write(renderer, capacity);

                if (!power.AllowsAudio || !IsCurrentOutput(enumerator, id))
                    return;

                Marshal.ThrowExceptionForHR(client.Start());
                started = true;

                var health = Stopwatch.StartNew();
                long lastPacket = 0;
                long lastRouteCheck = 0;
                bool reportedActive = false;
                while (power.AllowsAudio)
                {
                    if (ready.WaitOne(400))
                    {
                        uint padding;
                        Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out padding));
                        if (padding < capacity)
                        {
                            waveform.Write(renderer, capacity - padding);
                            lastPacket = health.ElapsedMilliseconds;
                            if (!reportedActive)
                            {
                                active.Set();
                                reportedActive = true;
                            }
                        }
                    }

                    long elapsed = health.ElapsedMilliseconds;
                    if (elapsed - lastPacket > 2400)
                        break;
                    if (elapsed - lastRouteCheck >= 500)
                    {
                        if (!IsCurrentOutput(enumerator, id))
                            break;
                        lastRouteCheck = elapsed;
                    }
                }
            }
            finally
            {
                active.Reset();
                if (started)
                    client.Stop();
                if (rendererObject != null)
                    Marshal.ReleaseComObject(rendererObject);
                Marshal.ReleaseComObject(clientObject);
            }
        }
    }

    private sealed class LowFrequencyPcm
    {
        private readonly float[] cycle;
        private readonly float[] samples;
        private readonly int channels;
        private int phase;

        public LowFrequencyPcm(int rate, int channels, uint frames)
        {
            this.channels = channels;
            cycle = new float[rate];
            samples = new float[checked((int)frames * channels)];
            // A 1 Hz signal keeps the stream active at a level below normal audibility.
            for (int i = 0; i < cycle.Length; i++)
                cycle[i] = SignalAmplitude * (float)Math.Sin(2.0 * Math.PI * i / rate);
        }

        public void Write(IAudioRenderClient renderer, uint frames)
        {
            int count = checked((int)frames);
            for (int frame = 0; frame < count; frame++)
            {
                float sample = cycle[phase];
                for (int channel = 0; channel < channels; channel++)
                    samples[frame * channels + channel] = sample;
                phase++;
                if (phase == cycle.Length)
                    phase = 0;
            }

            IntPtr destination;
            Marshal.ThrowExceptionForHR(renderer.GetBuffer(frames, out destination));
            Marshal.Copy(samples, 0, destination, count * channels);
            Marshal.ThrowExceptionForHR(renderer.ReleaseBuffer(frames, 0));
        }
    }

    private sealed class DesktopActivity : NativeWindow, IDisposable
    {
        private const int SessionChange = 0x02b1;
        private const int SessionLock = 7;
        private const int SessionUnlock = 8;
        private const int PowerBroadcast = 0x0218;
        private const int PowerSettingChange = 0x8013;
        private const int Suspend = 4;
        private const int ResumeAutomatic = 18;
        private static readonly Guid DisplayStatus = new Guid("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
        private readonly IntPtr displayRegistration;
        private readonly bool sessionRegistered;
        private volatile bool locked;
        private volatile bool displayOff;
        private volatile bool suspended;

        public DesktopActivity()
        {
            CreateHandle(new CreateParams { Caption = "AirPods Audio Keep-Alive" });
            var setting = DisplayStatus;
            displayRegistration = RegisterPowerSettingNotification(Handle, ref setting, 0);
            if (displayRegistration == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new Win32Exception(error);
            }
            sessionRegistered = WTSRegisterSessionNotification(Handle, 0);
            if (!sessionRegistered)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
        }

        public bool AllowsAudio { get { return !locked && !displayOff && !suspended; } }

        protected override void WndProc(ref Message message)
        {
            long change = message.WParam.ToInt64();
            if (message.Msg == SessionChange)
            {
                if (change == SessionLock)
                    locked = true;
                else if (change == SessionUnlock)
                    locked = false;
            }
            else if (message.Msg == PowerBroadcast)
            {
                if (change == Suspend)
                    suspended = true;
                else if (change == ResumeAutomatic)
                    suspended = false;
                else if (change == PowerSettingChange && message.LParam != IntPtr.Zero)
                {
                    Guid setting = (Guid)Marshal.PtrToStructure(message.LParam, typeof(Guid));
                    if (setting == DisplayStatus && Marshal.ReadInt32(message.LParam, 16) >= 4)
                        displayOff = Marshal.ReadInt32(message.LParam, 20) == 0;
                }
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (sessionRegistered)
                WTSUnRegisterSessionNotification(Handle);
            if (displayRegistration != IntPtr.Zero)
                UnregisterPowerSettingNotification(displayRegistration);
            DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
        [DllImport("user32.dll")]
        private static extern bool UnregisterPowerSettingNotification(IntPtr registration);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
        [DllImport("wtsapi32.dll")]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr alternative);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long standard, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
