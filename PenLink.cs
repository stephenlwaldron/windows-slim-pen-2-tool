using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SlimPenHaptics;

// Asks Windows whether the pen's Bluetooth link is currently up. This reads a device
// property Windows already tracks, so it costs no radio traffic and can't keep the pen awake.
public static class PenLink
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_PropertyW(
        string deviceInterface, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

    private const int    CR_SUCCESS         = 0;
    private const string BluetoothLePrefix  = @"BTHLE\DEV_";

    private static DEVPROPKEY InstanceIdKey  = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    private static DEVPROPKEY IsConnectedKey = new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 15 };

    // Walk up from the pen's HID collection to the Bluetooth LE device that owns it.
    public static string? FindBluetoothDevice(string hidInterfacePath)
    {
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(hidInterfacePath, ref InstanceIdKey, out _, null, ref size, 0);
        if (size == 0) return null;

        var buffer = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(hidInterfacePath, ref InstanceIdKey, out _, buffer, ref size, 0) != CR_SUCCESS)
            return null;

        string id = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out uint node, id, 0) != CR_SUCCESS) return null;

        var name = new char[512];
        for (int depth = 0; depth < 10; depth++)
        {
            Array.Clear(name);
            if (CM_Get_Device_IDW(node, name, (uint)name.Length, 0) != CR_SUCCESS) return null;

            string current = new string(name).TrimEnd('\0');
            if (current.StartsWith(BluetoothLePrefix, StringComparison.OrdinalIgnoreCase)) return current;

            if (CM_Get_Parent(out node, node, 0) != CR_SUCCESS) return null;
        }
        return null;
    }

    // null when Windows can't say (device gone, property missing).
    public static bool? IsConnected(string bluetoothDeviceId)
    {
        if (CM_Locate_DevNodeW(out uint node, bluetoothDeviceId, 0) != CR_SUCCESS) return null;

        var value = new byte[4];
        uint size = (uint)value.Length;
        if (CM_Get_DevNode_PropertyW(node, ref IsConnectedKey, out _, value, ref size, 0) != CR_SUCCESS || size == 0)
            return null;

        return value[0] != 0;
    }
}
