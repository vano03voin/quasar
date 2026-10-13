using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

// Offline tests: the only child is this executable's inert --self-test-child mode.
internal static class WindowsNodeAffinitySelfTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        Validation();
        if (OperatingSystem.IsWindows()) await NativeAsync();
        else Console.WriteLine("Native affinity checks skipped: not Windows.");
        Console.WriteLine($"Host affinity self-test passed: {_checks} checks; no game or web server launched.");
        return 0;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Affinity self-test failed: " + description);
        _checks++;
    }

    private static void Reject(Action action, string description)
    {
        try { action(); }
        catch (ArgumentException) { _checks++; return; }
        throw new InvalidOperationException("Affinity self-test accepted invalid policy: " + description);
    }

    private static void Validation()
    {
        WindowsNodeAffinitySpec valid = new("cluster", "slot", "0xFFFF");
        var parsed = WindowsNodeAffinity.Validate([valid], true, 1, 0xFFFFFFFF);
        Check(parsed[("cluster", "slot")] == 0xFFFF, "first sixteen CPUs");
        Check(!parsed.ContainsKey(("CLUSTER", "slot")) && !parsed.ContainsKey(("cluster", "SLOT")), "exact identity match");
        Check(WindowsNodeAffinity.Validate([], false, 2, 0).Count == 0, "absent policy is portable");
        Reject(() => WindowsNodeAffinity.Validate([valid], false, 1, uint.MaxValue), "Linux policy");
        Reject(() => WindowsNodeAffinity.Validate([valid], true, 2, ulong.MaxValue), "multiple processor groups");
        Reject(() => WindowsNodeAffinity.Validate([valid], true, 1, 0), "missing system mask");
        Reject(() => WindowsNodeAffinity.Validate([valid, valid], true, 1, uint.MaxValue), "duplicate identity");
        foreach (string text in new[] { "", "0", "0x0", "FFFF", "0x", "0x-1", "0x+1", "0xGG", "0x10000000000000000", "0x F", " 0xF", "0xF ", "0x100000000" })
            Reject(() => WindowsNodeAffinity.Validate([valid with { CpuMask = text }], true, 1, uint.MaxValue), text);
        foreach (string id in new[] { "", " ", " cluster", "cluster " })
        {
            Reject(() => WindowsNodeAffinity.Validate([valid with { ClusterId = id }], true, 1, uint.MaxValue), "cluster ID " + id);
            Reject(() => WindowsNodeAffinity.Validate([valid with { SlotKey = id }], true, 1, uint.MaxValue), "slot ID " + id);
        }
        Reject(() => WindowsNodeAffinity.Validate([null!], true, 1, uint.MaxValue), "null item");
        Reject(() => WindowsNodeAffinity.Validate([valid with { CpuMask = null! }], true, 1, uint.MaxValue), "null mask");
        Reject(() => WindowsNodeAffinity.Validate([valid with { CpuMask = "0x2" }], true, 1, 0x5), "unavailable CPU in sparse mask");
        Check(WindowsNodeAffinity.Validate([valid with { CpuMask = "0x8000000000000000" }], true, 1, ulong.MaxValue)
            [("cluster", "slot")] == 0x8000000000000000, "CPU 63 unsigned mask");
        Check(WindowsNodeAffinity.Validate([valid with { CpuMask = "0Xa" }], true, 1, uint.MaxValue)
            [("cluster", "slot")] == 0xA, "hexadecimal case");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task NativeAsync()
    {
        using var self = Process.GetCurrentProcess();
        ulong inherited = unchecked((ulong)self.ProcessorAffinity.ToInt64());
        ulong desired = inherited & (~inherited + 1);
        Check(desired != 0 && (desired & ~WindowsNodeAffinity.GetSystemMask()) == 0, "native available mask");
        ulong other = inherited & ~desired;
        other &= ~other + 1;
        string root = Path.Combine(Path.GetTempPath(), "quasar-affinity-selftest-" + Guid.NewGuid().ToString("N"));
        Process? child = null;
        try
        {
            string bundleRoot = Path.Combine(root, "bundle"), stateRoot = Path.Combine(root, "state"), runRoot = Path.Combine(root, "runs");
            Directory.CreateDirectory(bundleRoot);
            var files = new List<BundleFile>();
            foreach (string source in Directory.GetFiles(AppContext.BaseDirectory))
            {
                string name = Path.GetFileName(source), target = Path.Combine(bundleRoot, name);
                File.Copy(source, target);
                files.Add(new BundleFile(name, Hash(target)));
            }
            const string executable = "Quasar.Host.exe";
            Check(File.Exists(Path.Combine(bundleRoot, executable)), "native Host apphost is present");
            int reservedPort;
            using (var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                       System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
            {
                probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                reservedPort = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
            }
            var manifest = new BundleManifest(1, "affinity-self-test", files.ToArray(),
                [new NodeSpawnSpec("slot-a", Admin.NodeRole.Regular, "node-a", executable, "", ["--self-test-child"], [], [reservedPort], 120)]);
            string manifestPath = Path.Combine(bundleRoot, "manifest.json");
            Write(manifestPath, manifest);
            var attachment = new HostContract.HostAttachmentSpec("demo", "http://127.0.0.1:1", "UNUSED_AFFINITY_TEST_TOKEN",
                manifestPath, Hash(manifestPath), runRoot);
            var wanted = new Admin.NodePlan("slot-a", "host-a", Admin.NodeRole.Regular, Admin.NodeGoal.Wanted,
                Admin.NodeObservation.Missing, null, Admin.IncumbentAction.None, null, 0, null, null, null, null, 0, false, true);
            WindowsNodeAffinitySpec[] policy = [new("demo", "slot-a", $"0x{desired:X}")];
            var actualizer = new NodeActualizer(stateRoot, "host-a", windowsNodeAffinities: policy);
            async Task<NodeExecutionObservation> Poll(NodeActualizer host, HostContract.HostAttachmentSpec? spec = null) =>
                (await host.ReconcileAsync(spec ?? attachment, [wanted], CancellationToken.None)).Single();
            NodeExecutionObservation launched = await Poll(actualizer);
            Check(launched.State == Admin.NodeObservation.Spawning, "owned inert child launch: " + launched.Failure);
            string recordPath = Directory.GetFiles(Path.Combine(stateRoot, "launch-records"), "*.json").Single();
            LaunchRecord record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllBytes(recordPath), Json)!;
            child = Process.GetProcessById(record.ProcessId!.Value);
            ulong ActualMask()
            {
                Process target = child ?? throw new InvalidOperationException("Inert child is missing.");
                target.Refresh();
                return unchecked((ulong)target.ProcessorAffinity.ToInt64());
            }
            Check(ActualMask() == desired && record.AppliedCpuMask == $"0x{desired:X16}", "launch applies and records policy");

            string readyPath = Path.Combine(record.RunDirectory, ".quasar-node-ready.json");
            Write(readyPath, new ReadyReceipt(1, "demo", "slot-a", record.AttemptKey, "node-a", 7,
                "127.0.0.1:1", child.Id, record.BundleRevision));
            actualizer = new NodeActualizer(stateRoot, "host-a", windowsNodeAffinities: policy);
            Check((await Poll(actualizer)).State == Admin.NodeObservation.Ready && ActualMask() == desired,
                "restart adoption verifies persisted mask and readiness");
            record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllBytes(recordPath), Json)!;

            if (other != 0)
            {
                child.ProcessorAffinity = unchecked((nint)(long)other);
                var drift = await Poll(actualizer);
                Check(drift.State == Admin.NodeObservation.Failed && drift.Failure!.StartsWith("node_affinity_failed:")
                    && ActualMask() == other && !child.HasExited, "drift reported without affinity fight or kill");
                Check((await Poll(new NodeActualizer(stateRoot, "host-a"))).State == Admin.NodeObservation.Ready
                    && ActualMask() == other, "no-policy adoption preserves existing affinity");

                Write(recordPath, record with { ExecutableSha256 = new string('0', 64), AppliedCpuMask = null });
                var conflict = await Poll(actualizer);
                Check(conflict.Failure!.StartsWith("unmanaged_conflict:") && ActualMask() == other,
                    "executable identity fence precedes affinity mutation");
                Write(recordPath, record with { ProcessStartedAt = record.ProcessStartedAt!.Value.AddSeconds(-10), AppliedCpuMask = null });
                Check((await Poll(actualizer)).State == Admin.NodeObservation.Failed && ActualMask() == other && !child.HasExited,
                    "process creation identity fence precedes affinity mutation");
                Write(recordPath, record with { AppliedCpuMask = null });
                var mismatch = await Poll(actualizer, attachment with { BundleManifestSha256 = new string('0', 64) });
                Check(mismatch.Failure == "running_deployment_mismatch" && ActualMask() == other,
                    "deployment fence precedes affinity mutation");
                Check((await Poll(actualizer)).State == Admin.NodeObservation.Ready && ActualMask() == desired,
                    "legacy owned launch without a mask adopts policy once");

                var changed = new NodeActualizer(stateRoot, "host-a", windowsNodeAffinities: [new("demo", "slot-a", $"0x{other:X}")]);
                Check((await Poll(changed)).Failure!.StartsWith("node_affinity_failed:") && ActualMask() == desired,
                    "changed policy requires restart without silently moving a running node");
            }
            else Console.WriteLine("Native drift checks skipped: only one CPU is available to the test process.");

            var unrelated = new WindowsNodeAffinity([new("other-cluster", "slot-a", $"0x{desired:X}")]);
            Check(unrelated.ApplyOrVerify(child, "demo", "slot-a", null) is null && ActualMask() == desired,
                "unrelated cluster policy has no effect");
            unrelated = new WindowsNodeAffinity([new("demo", "other-slot", $"0x{desired:X}")]);
            Check(unrelated.ApplyOrVerify(child, "demo", "slot-a", null) is null && ActualMask() == desired,
                "unrelated slot policy has no effect");

            var kill = wanted with { Observed = Admin.NodeObservation.Ready, ObservedNode = "node-a",
                IncumbentAction = Admin.IncumbentAction.Kill, IncumbentNode = "node-a", IncumbentEpoch = 7, SpawnAllowed = false };
            Check((await actualizer.ReconcileAsync(attachment, [kill], CancellationToken.None)).Single().State == Admin.NodeObservation.Gone,
                "existing fenced stop remains available");
            Check(child.WaitForExit(5000), "inert child exited");
            child.Dispose();
            child = null;

            var defaults = new NodeActualizer(stateRoot, "host-a");
            Check((await Poll(defaults)).State == Admin.NodeObservation.Spawning, "no-policy launch");
            LaunchRecord defaultRecord = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllBytes(recordPath), Json)!;
            child = Process.GetProcessById(defaultRecord.ProcessId!.Value);
            Check(ActualMask() == inherited && defaultRecord.AppliedCpuMask is null, "no-policy launch preserves inherited mask");
            var stopDefault = wanted with { Goal = Admin.NodeGoal.Kill, SpawnAllowed = false };
            Check((await defaults.ReconcileAsync(attachment, [stopDefault], CancellationToken.None)).Single().State == Admin.NodeObservation.Gone,
                "no-policy child cleaned up");

            // Configuration is loaded locally, without starting Host's run loop or opening a socket.
            string configPath = Path.Combine(root, "host.json");
            var config = new HostExecutorConfig("executor-a", "host-a", 2, [attachment], WindowsNodeAffinities: policy);
            Write(configPath, config);
            Check(Program.Load(configPath).WindowsNodeAffinities!.Single().CpuMask == policy[0].CpuMask, "config policy round-trip");
            Write(configPath, config with { WindowsNodeAffinities = [policy[0] with { CpuMask = "0x0" }] });
            Reject(() => Program.Load(configPath), "config fails before run loop");
            Write(configPath, config with { WindowsNodeAffinities = null });
            Check(Program.Load(configPath).WindowsNodeAffinities is null, "old config remains compatible");
        }
        finally
        {
            // Retained owned process handle, never a new PID lookup for cleanup.
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
                child.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Write<T>(string path, T data) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(data, Json));
}
