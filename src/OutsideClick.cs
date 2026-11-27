using System;
using System.Runtime.InteropServices;

namespace DualMeter
{
    // While details or the menu are open, check button-downs without capturing the pointer.
    // The other application's first click goes straight to its original target.
    internal static class OutsideClick
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Device { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Header { public uint Type, Size; public IntPtr Device, Param; }
        [StructLayout(LayoutKind.Explicit, Size=24)]
        private struct Mouse { [FieldOffset(4)] public ushort ButtonFlags; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Packet { public Header Header; public Mouse Mouse; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint { public int X, Y; }

        private static readonly uint DeviceSize = (uint)Marshal.SizeOf(typeof(Device));
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(Header));
        private static readonly uint PacketSize = (uint)Marshal.SizeOf(typeof(Packet));
        [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(ref Device device, uint count, uint size);
        [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr data, uint command, out Packet packet, ref uint size, uint headerSize);
        [DllImport("user32.dll")] private static extern uint GetMessagePos();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint point);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

        public static bool SetReceiver(IntPtr hwnd)
        {
            const uint remove = 1, inputSink = 0x100;
            var device = new Device { Page = 1, Usage = 2, Flags = hwnd == IntPtr.Zero ? remove : inputSink, Target = hwnd };
            return RegisterRawInputDevices(ref device, 1, DeviceSize);
        }

        public static bool IsExternalPress(IntPtr meter, IntPtr input)
        {
            const uint readInput = 0x10000003;
            const ushort buttonDown = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100;
            uint size = PacketSize;
            Packet packet;
            if (GetRawInputData(input, readInput, out packet, ref size, HeaderSize) != PacketSize ||
                packet.Header.Type != 0 || (packet.Mouse.ButtonFlags & buttonDown) == 0) return false;
            uint position = GetMessagePos();
            var point = new ScreenPoint { X = unchecked((short)position), Y = unchecked((short)(position >> 16)) };
            IntPtr target = WindowFromPoint(point);
            if (target == IntPtr.Zero || target == meter) return false;
            uint owner, targetOwner;
            return GetWindowThreadProcessId(meter, out owner) != 0 &&
                   GetWindowThreadProcessId(target, out targetOwner) != 0 && owner != targetOwner;
        }
    }
}
