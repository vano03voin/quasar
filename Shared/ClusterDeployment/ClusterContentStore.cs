using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Quasar.Host.Contract.V1;

namespace Quasar.ClusterDeployment;

// Snapshots are manifests and hard-link references, never private copies of payloads.
// Only immutable input trees may use this store. Mutable worlds/runtimes must copy.
internal sealed partial class ClusterContentStore : IDisposable
{
    // Set once by each service. Offline commands/tests use a store beside their inputs.
    internal static string? RootDirectory { get; set; }
    private readonly string root;
    private readonly FileStream gate;
    private readonly HashSet<string> verified = new(StringComparer.Ordinal);

    private ClusterContentStore(string root, FileStream gate) => (this.root, this.gate) = (root, gate);

    internal static async Task<ClusterContentStore> OpenAsync(string snapshots, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Cluster content storage requires Linux or Windows 10 1809 / Server 2019 or later with NTFS.");
        snapshots = Path.GetFullPath(snapshots);
        RefuseLink(snapshots, allowMissing: true);
        Directory.CreateDirectory(snapshots);
        ClusterWorldFiles.Private(snapshots);
        string storage = RootDirectory ?? snapshots;
        RefuseLink(storage, allowMissing: true);
        Directory.CreateDirectory(storage);
        // Hard links cannot cross devices. A separate volume owns its own objects.
        if (Device(storage) != Device(snapshots)) storage = snapshots;
        string root = Path.Combine(Path.GetFullPath(storage), ".objects");
        RefuseLink(root, allowMissing: true);
        Directory.CreateDirectory(root);
        ClusterWorldFiles.Private(root);
        string lockFile = Path.Combine(root, ".lock");
        if (File.Exists(lockFile)) RefuseLink(lockFile);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(root, new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(100, token); }
        }
    }

    internal async Task ReferenceAsync(string source, string destination, DeploymentFile pin, CancellationToken token)
    {
        if (pin.Bytes < 0 || pin.Sha256.Length != 64 || pin.Sha256 != pin.Sha256.ToLowerInvariant()
            || !pin.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid content object pin.");
        string key = pin.Sha256 + (pin.Executable ? "-x" : "-r");
        string path = Path.Combine(root, key);
        if (!File.Exists(path))
        {
            RefuseLink(source);
            string temporary = Path.Combine(root, ".write-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(source, temporary);
                await VerifyAsync(temporary, pin, token);
                if (OperatingSystem.IsWindows()) File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.ReadOnly);
                else File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    | (pin.Executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
                File.Move(temporary, path);
                verified.Add(key);
            }
            finally { DeleteFile(temporary); }
        }
        else
        {
            if (new FileInfo(path).Length != pin.Bytes)
                throw new InvalidDataException("Content object does not match its verified pin.");
            if (!verified.Contains(key))
            {
                await VerifyAsync(path, pin, token);
                verified.Add(key);
            }
        }
        token.ThrowIfCancellationRequested();
        RefuseLink(Path.GetDirectoryName(destination)!, allowMissing: true);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ClusterWorldFiles.Private(Path.GetDirectoryName(destination)!);
        if (OperatingSystem.IsWindows()) WindowsLink(path, destination);
        else if (Link(path, destination) != 0) throw new IOException("Cannot create content reference.", new Win32Exception(Marshal.GetLastPInvokeError()));
        ClusterStorageActivity.MarkChanged();
    }

    // Used for legacy snapshots and newly received files. Publish each replacement
    // atomically; interruption leaves either the old verified file or its reference.
    internal async Task AdoptAsync(string path, DeploymentFile pin, CancellationToken token)
    {
        await VerifyAsync(path, pin, token);
        string temporary = path + ".reference-" + Guid.NewGuid().ToString("N");
        try
        {
            await ReferenceAsync(path, temporary, pin, token);
            if (OperatingSystem.IsWindows()) WindowsReplace(temporary, path);
            else File.Move(temporary, path, true);
        }
        finally { DeleteFile(temporary); }
    }

    internal void Prune()
    {
        foreach (string path in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith(".write-", StringComparison.Ordinal)) { DeleteFile(path); continue; }
            if (name.Length != 66 || name[64] != '-' || name[65] is not ('r' or 'x')
                || !name[..64].All(Uri.IsHexDigit)) continue;
            RefuseLink(path);
            if (ReferenceCount(path) == 1) DeleteFile(path);
        }
    }

    internal static uint ReferenceCount(string path)
    {
        if (OperatingSystem.IsWindows()) return WindowsInformation(path).Links;
        if (Statx(-100, path, 0x100, 4, out var data) != 0 || (data.Mask & 4) == 0)
            throw new IOException("Cannot inspect content references.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return data.Links;
    }

    private static (uint Major, uint Minor) Device(string path)
    {
        if (OperatingSystem.IsWindows()) return (WindowsInformation(path).VolumeSerial, 0);
        if (Statx(-100, path, 0x100, 0x7ff, out var data) != 0)
            throw new IOException("Cannot inspect content filesystem.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return (data.DeviceMajor, data.DeviceMinor);
    }

    private static async Task VerifyAsync(string path, DeploymentFile pin, CancellationToken token)
    {
        RefuseLink(path);
        await using var file = File.OpenRead(path);
        if (file.Length != pin.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant() != pin.Sha256)
            throw new InvalidDataException("Content object does not match its verified pin.");
    }

    private static void RefuseLink(string path, bool allowMissing = false)
    {
        for (string? parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Content storage must not contain symbolic links.");
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }

    public void Dispose() { try { Prune(); } finally { gate.Dispose(); } }

    // A Windows hard link shares the read-only attribute with every other name.
    // Never clear that attribute to remove a snapshot or an unpublished reference.
    internal static void DeleteFile(string path)
    {
        if (!OperatingSystem.IsWindows()) { File.Delete(path); return; }
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        RefuseLink(path);
        WindowsDelete(path);
    }

    internal static void DeleteTree(string path)
    {
        if (!OperatingSystem.IsWindows()) { Directory.Delete(path, true); return; }
        path = Path.GetFullPath(path);
        if (path == Path.GetPathRoot(path)) throw new InvalidDataException("Cannot remove a filesystem root.");
        // Validate the whole tree before removing any name, without following junctions.
        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out string? directory))
        {
            RefuseLink(directory);
            directories.Add(directory);
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                RefuseLink(child);
                if (Directory.Exists(child)) pending.Push(child);
                else files.Add(child);
            }
        }
        foreach (string file in files) DeleteFile(file);
        foreach (string directory in directories.AsEnumerable().Reverse())
        {
            RefuseLink(directory);
            Directory.Delete(directory);
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxData data);
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
}
