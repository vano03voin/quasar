using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Quasar.ClusterDeployment;

internal sealed partial class ClusterContentStore
{
    private const uint DeleteAccess = 0x00010000, ReadAttributes = 0x80, WriteAttributes = 0x100;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;

    private static SafeFileHandle WindowsOpen(string path, uint access)
    {
        RefuseLink(path);
        var handle = CreateFileW(Path.GetFullPath(path), access, 7, IntPtr.Zero, 3,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("Cannot open content storage entry.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("Cannot inspect content storage entry.", new Win32Exception(error));
        }
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException("Content storage must not contain reparse points.");
        }
        return handle;
    }

    private static WindowsFileInformation WindowsInformation(string path)
    {
        using var handle = WindowsOpen(path, ReadAttributes);
        var filesystem = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out uint flags,
                filesystem, filesystem.Capacity))
            throw new IOException("Cannot inspect content filesystem.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (!filesystem.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase) || (flags & 0x00400000) == 0)
            throw new PlatformNotSupportedException("Windows cluster content storage requires NTFS hard links.");
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Cannot inspect content references.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return info;
    }

    private static void WindowsLink(string source, string destination)
    {
        _ = WindowsInformation(source);
        if (!CreateHardLinkW(Path.GetFullPath(destination), Path.GetFullPath(source), IntPtr.Zero))
            throw new IOException("Cannot create content reference.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    private static void WindowsDelete(string path)
    {
        using var handle = WindowsOpen(path, DeleteAccess | ReadAttributes | WriteAttributes);
        // FILE_DISPOSITION_DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE.
        // Unsupported filesystems/OS versions fail here; no writable-attribute fallback.
        uint flags = 0x1 | 0x2 | 0x10;
        if (!SetDisposition(handle, 21, ref flags, sizeof(uint)))
            throw new IOException("Cannot unlink immutable content reference.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    private static void WindowsReplace(string source, string destination)
    {
        RefuseLink(destination);
        using var handle = WindowsOpen(source, DeleteAccess | ReadAttributes | WriteAttributes);
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        int nameOffset = Marshal.OffsetOf<WindowsRenameInformation>(nameof(WindowsRenameInformation.FirstCharacter)).ToInt32();
        IntPtr buffer = Marshal.AllocHGlobal(nameOffset + name.Length + sizeof(char));
        try
        {
            // REPLACE_IF_EXISTS | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE.
            Marshal.StructureToPtr(new WindowsRenameInformation { Flags = 0x1 | 0x2 | 0x40,
                NameBytes = (uint)name.Length }, buffer, false);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetRename(handle, 22, buffer, (uint)(nameOffset + name.Length + sizeof(char))))
                throw new IOException("Cannot atomically replace immutable content reference.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsRenameInformation
    {
        public uint Flags;
        public IntPtr Root;
        public uint NameBytes;
        public char FirstCharacter;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? name, int nameSize,
        out uint serial, out uint maxComponent, out uint flags, StringBuilder filesystem, int filesystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string destination, string source, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int informationClass, ref uint flags, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRename(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
}
