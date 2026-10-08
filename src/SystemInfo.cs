using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback client);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback client);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
    }

    // Avisa a la ventana anfitriona (desde un hilo COM arbitrario) cuando cambia el volumen.
    sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        readonly IntPtr hwnd;
        readonly uint msg;
        public VolumeCallback(IntPtr hwnd, uint msg) { this.hwnd = hwnd; this.msg = msg; }
        public int OnNotify(IntPtr data)
        {
            Native.PostMessageW(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }
    }

    sealed class VolumeMonitor
    {
        readonly IntPtr notifyHwnd;
        readonly uint notifyMsg;
        IAudioEndpointVolume endpoint;
        VolumeCallback callback;
        Guid ctx = Guid.Empty;

        public bool Available { get { return endpoint != null; } }
        public int Level;       // 0..100
        public bool Muted;

        public VolumeMonitor(IntPtr notifyHwnd, uint notifyMsg)
        {
            this.notifyHwnd = notifyHwnd;
            this.notifyMsg = notifyMsg;
        }

        // (Re)adquiere el dispositivo de reproducción predeterminado.
        public void Attach()
        {
            Detach();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return;
                Guid iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                object o;
                if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) return;
                endpoint = (IAudioEndpointVolume)o;
                callback = new VolumeCallback(notifyHwnd, notifyMsg);
                endpoint.RegisterControlChangeNotify(callback);
                Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(enumerator);
            }
            catch { endpoint = null; }
            Query();
        }

        public void Detach()
        {
            try
            {
                if (endpoint != null)
                {
                    if (callback != null) endpoint.UnregisterControlChangeNotify(callback);
                    Marshal.ReleaseComObject(endpoint);
                }
            }
            catch { }
            endpoint = null;
            callback = null;
        }

        public void Query()
        {
            if (endpoint == null) { Level = 0; Muted = false; return; }
            try
            {
                float v;
                bool m;
                if (endpoint.GetMasterVolumeLevelScalar(out v) == 0) Level = (int)Math.Round(v * 100);
                if (endpoint.GetMute(out m) == 0) Muted = m;
            }
            catch { }
        }

        public void Step(bool up)
        {
            if (endpoint == null) return;
            try { if (up) endpoint.VolumeStepUp(ref ctx); else endpoint.VolumeStepDown(ref ctx); }
            catch { }
            Query();
        }

        public void ToggleMute()
        {
            if (endpoint == null) return;
            try { endpoint.SetMute(!Muted, ref ctx); } catch { }
            Query();
        }
    }

    enum NetKind { None, Wifi, Ethernet }

    static class NetworkProbe
    {
        public static NetKind Detect()
        {
            try
            {
                NetKind best = NetKind.None;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    var type = nic.NetworkInterfaceType;
                    if (type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel) continue;
                    string desc = (nic.Description ?? "") + " " + (nic.Name ?? "");
                    if (desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("vEthernet", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("TAP-", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    bool hasGateway = false;
                    foreach (var g in nic.GetIPProperties().GatewayAddresses)
                    {
                        if (g.Address != null && !g.Address.Equals(System.Net.IPAddress.Any)) { hasGateway = true; break; }
                    }
                    if (!hasGateway) continue;
                    if (type == NetworkInterfaceType.Wireless80211) { if (best == NetKind.None) best = NetKind.Wifi; }
                    else best = NetKind.Ethernet;
                }
                return best;
            }
            catch { return NetKind.None; }
        }
    }

    struct BatteryState
    {
        public bool Present;
        public int Percent;
        public bool Charging;
        public bool AcPower;

        public static BatteryState Read()
        {
            var b = new BatteryState();
            SYSTEM_POWER_STATUS s;
            if (!Native.GetSystemPowerStatus(out s)) return b;
            if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255) return b; // sin batería / desconocido
            b.Present = true;
            b.Percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 100;
            b.AcPower = s.ACLineStatus == 1;
            b.Charging = (s.BatteryFlag & 8) != 0;
            return b;
        }
    }
}
