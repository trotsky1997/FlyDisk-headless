using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FlyDisk.Headless;

internal sealed class VolumeQuiesce : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    public static VolumeQuiesce Lock(int diskNumber)
    {
        var result = new VolumeQuiesce();
        try
        {
            string script = "$ErrorActionPreference='Stop'; ConvertTo-Json -InputObject @(Get-Partition -DiskNumber " + diskNumber +
                " | Get-Volume | Select-Object -ExpandProperty Path -Unique) -Compress";
            string output = External.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
            foreach (string path in JsonSerializer.Deserialize<string[]>(output) ?? throw new IOException("Volume enumeration failed."))
            {
                var handle = CreateFileW(path.TrimEnd('\\'), 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open source volume for safe takeover."); }
                result.handles.Add(handle);
                // FSCTL_LOCK_VOLUME flushes lazy writes and refuses volumes containing open files.
                if (!DeviceIoControl(handle, 0x00090018, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Source volume is busy; safe takeover refused.");
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlap);
}
