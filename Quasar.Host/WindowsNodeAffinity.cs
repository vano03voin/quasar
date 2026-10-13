using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Quasar.Host;

// Local machine policy: never part of the Registry contract or a portable deployment bundle.
internal sealed record WindowsNodeAffinitySpec(string ClusterId, string SlotKey, string CpuMask);

internal sealed class WindowsNodeAffinity
{
    private readonly IReadOnlyDictionary<(string ClusterId, string SlotKey), ulong> _masks;

    internal WindowsNodeAffinity(WindowsNodeAffinitySpec[]? specifications)
    {
        if (specifications is not { Length: > 0 })
        {
            _masks = new Dictionary<(string, string), ulong>();
            return;
        }
        if (!OperatingSystem.IsWindows())
            throw new ArgumentException("Windows node affinity requires a Windows host.");
        try
        {
            _masks = Validate(specifications, true, GetActiveProcessorGroupCount(), GetSystemMask());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ArgumentException("Cannot validate Windows node affinity: " + exception.Message, exception);
        }
    }

    // Explicit machine facts keep validation deterministic and independent of this test machine's CPU count.
    internal static IReadOnlyDictionary<(string ClusterId, string SlotKey), ulong> Validate(
        WindowsNodeAffinitySpec[] specifications, bool windows, int processorGroups, ulong systemMask)
    {
        var masks = new Dictionary<(string, string), ulong>();
        if (specifications.Length == 0) return masks;
        if (!windows || processorGroups != 1 || systemMask == 0)
            throw new ArgumentException("Windows node affinity requires one active processor group with an available CPU mask.");
        foreach (var specification in specifications)
        {
            if (specification is null || string.IsNullOrWhiteSpace(specification.ClusterId)
                || string.IsNullOrWhiteSpace(specification.SlotKey)
                || specification.ClusterId != specification.ClusterId.Trim()
                || specification.SlotKey != specification.SlotKey.Trim())
                throw new ArgumentException("Windows node affinity requires exact nonblank cluster and slot IDs without surrounding whitespace.");
            string? text = specification.CpuMask;
            if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || text.Length is < 3 or > 18 || text[2..].Any(character => !Uri.IsHexDigit(character))
                || !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong mask)
                || mask == 0 || (mask & ~systemMask) != 0)
                throw new ArgumentException($"Affinity for '{specification.ClusterId}/{specification.SlotKey}' must be a nonzero 0x-prefixed mask contained in the system CPU mask.");
            if (!masks.TryAdd((specification.ClusterId, specification.SlotKey), mask))
                throw new ArgumentException($"Duplicate Windows node affinity for '{specification.ClusterId}/{specification.SlotKey}'.");
        }
        return masks;
    }

    internal void Preflight(string clusterId, string slotKey)
    {
        if (!_masks.TryGetValue((clusterId, slotKey), out ulong mask)) return;
        if (!OperatingSystem.IsWindows() || GetActiveProcessorGroupCount() != 1
            || (mask & ~GetSystemMask()) != 0)
            throw new InvalidOperationException("Configured node affinity is unavailable on this host.");
    }

    // The caller must first prove ownership using the launch record and deployment identity.
    // A recorded policy is verified, never repeatedly imposed on an external affinity manager.
    internal string? ApplyOrVerify(Process process, string clusterId, string slotKey, string? recordedMask)
    {
        if (!_masks.TryGetValue((clusterId, slotKey), out ulong mask)) return recordedMask;
        Preflight(clusterId, slotKey);
        string canonical = $"0x{mask:X16}";
        if (recordedMask is not null && !canonical.Equals(recordedMask, StringComparison.Ordinal))
            throw new InvalidOperationException("Configured node affinity changed; restart this node to apply the new policy.");
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows node affinity requires Windows.");
        if (recordedMask is null)
            process.ProcessorAffinity = unchecked((nint)(long)mask);
        // Refresh clears cached process information before each readback, including adoption and drift checks.
        process.Refresh();
        ulong actual = unchecked((ulong)process.ProcessorAffinity.ToInt64());
        if (actual != mask)
            throw new InvalidOperationException($"Node CPU affinity mismatch: expected {canonical}, observed 0x{actual:X16}; check external affinity policies.");
        return canonical;
    }

    [SupportedOSPlatform("windows")]
    internal static ulong GetSystemMask()
    {
        if (IntPtr.Size != 8)
            throw new InvalidOperationException("Windows node affinity requires a 64-bit Host process.");
        using var current = Process.GetCurrentProcess();
        if (!GetProcessAffinityMask(current.SafeHandle, out _, out nuint mask))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return mask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(SafeProcessHandle process, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
}
