using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DisplayBrightness;

internal readonly record struct Brightness(uint Minimum, uint Current, uint Maximum)
{
    public int Percent => (int)Math.Round(100.0 * (Current - Minimum) / (Maximum - Minimum));

    public uint FromPercent(int percent) =>
        Minimum + (uint)Math.Round((Maximum - Minimum) * (percent / 100.0));
}

internal sealed class DdcMonitor(string name, string description, string id, PhysicalMonitorHandle handle) : IDisposable
{
    private const byte Luminance = 0x10;
    private const int Attempts = 3;
    private const int ReplyDelayMs = 120;
    private bool useVcp;
    public string Name => $"{Description} · {Position}";
    public string Position { get; } = name;
    public string Description { get; } = description;
    public string Id { get; } = id;

    public static List<DdcMonitor> Discover()
    {
        var displays = new List<(IntPtr Handle, int X, int Y)>();
        bool Collect(IntPtr monitor, IntPtr dc, ref Native.Rect bounds, IntPtr data)
        {
            displays.Add((monitor, bounds.Left, bounds.Top));
            return true;
        }

        if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero))
            throw new Win32Exception();

        var monitors = new List<DdcMonitor>();
        int number = 0;
        var ordered = displays.OrderBy(d => d.X).ThenBy(d => d.Y).ToArray();
        foreach (var display in ordered)
        {
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(display.Handle, out uint count) || count == 0)
                continue;

            var physical = new Native.PhysicalMonitor[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(display.Handle, count, physical))
                continue;

            // A cloned desktop can map one logical display to multiple physical monitors.
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            string? deviceName = Native.GetMonitorInfoW(display.Handle, ref info) ? info.DeviceName : null;
            string? id = count == 1 && deviceName is not null ? GetMonitorId(deviceName) : null;
            string? model = id is null ? null : ReadModelName(id);
            int physicalIndex = 0;
            foreach (var monitor in physical)
            {
                number++;
                string description = string.IsNullOrWhiteSpace(monitor.Description)
                    ? "显示器" : monitor.Description.Trim();
                string position = "";
                if (ordered.Length == 2 && count == 1)
                {
                    bool first = display == ordered[0];
                    position = ordered[0].X != ordered[1].X
                        ? (first ? "（左侧）" : "（右侧）")
                        : (first ? "（上方）" : "（下方）");
                }

                string key = id ?? $"output:{deviceName ?? $"{display.X},{display.Y}"}:{physicalIndex}:{description}";
                monitors.Add(new DdcMonitor($"屏幕 {number}{position}", model ?? description, key,
                    new PhysicalMonitorHandle(monitor.Handle)));
                physicalIndex++;
            }
        }

        return monitors;
    }

    private static string? ReadModelName(string deviceId)
    {
        // A monitor interface path contains the DISPLAY hardware ID and instance ID.
        string[] parts = deviceId.Split('#');
        if (parts.Length < 3 || !parts[0].Equals(@"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] { Length: >= 128 } edid)
                return null;

            // The four base-block descriptors begin at byte 54. Tag FC identifies the display name.
            for (int offset = 54; offset < 126; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != 0xFC)
                    continue;

                string model = Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n', '\0')[0].Trim();
                if (model.Length == 0)
                    return null;

                string manufacturer = parts[1].Length >= 3 ? parts[1][..3] : "";
                return manufacturer.Length == 0 || model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)
                    ? model : $"{manufacturer} {model}";
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or System.IO.IOException)
        {
            // Use the driver description when EDID is unavailable.
        }

        return null;
    }

    private static string? GetMonitorId(string deviceName)
    {
        const uint GetDeviceInterfaceName = 1;
        var device = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
        return Native.EnumDisplayDevicesW(deviceName, 0, ref device, GetDeviceInterfaceName)
            && !string.IsNullOrWhiteSpace(device.DeviceId) ? device.DeviceId : null;
    }

    public Brightness Read()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return ReadOnce();
            }
            catch (Win32Exception) when (attempt < Attempts)
            {
                Thread.Sleep(ReplyDelayMs);
            }
        }
    }

    private Brightness ReadOnce()
    {
        if (!useVcp && Native.GetMonitorBrightness(handle, out uint min, out uint current, out uint max)
            && max > min && current >= min && current <= max)
            return new Brightness(min, current, max);

        if (!Native.GetVCPFeatureAndVCPFeatureReply(handle, Luminance, IntPtr.Zero, out current, out max))
            throw new Win32Exception();
        if (max == 0 || current > max)
            throw new InvalidOperationException("显示器返回了无效的亮度范围。");

        useVcp = true;
        return new Brightness(0, current, max);
    }

    public Brightness Set(int percent, Brightness previous)
    {
        uint target = previous.FromPercent(percent);
        for (int attempt = 1; ; attempt++)
        {
            bool accepted = useVcp
                ? Native.SetVCPFeature(handle, Luminance, target)
                : Native.SetMonitorBrightness(handle, target);
            if (accepted)
                break;
            if (attempt == Attempts)
                throw new Win32Exception();
            Thread.Sleep(ReplyDelayMs);
        }

        // Some displays need time before the next DDC/CI read reflects the new value.
        for (int attempt = 1; ; attempt++)
        {
            Thread.Sleep(ReplyDelayMs);
            Brightness result = Read();
            if (result.Current == target || attempt == Attempts)
                return result;
        }
    }

    public void Dispose() => handle.Dispose();
}

internal sealed class PhysicalMonitorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PhysicalMonitorHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle() => Native.DestroyPhysicalMonitor(handle);
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    internal delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern bool EnumDisplayDevicesW(string name, uint index, ref DisplayDevice device, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physical);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetMonitorBrightness(PhysicalMonitorHandle monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool SetMonitorBrightness(PhysicalMonitorHandle monitor, uint brightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetVCPFeatureAndVCPFeatureReply(PhysicalMonitorHandle monitor, byte code, IntPtr type, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool SetVCPFeature(PhysicalMonitorHandle monitor, byte code, uint value);
}
