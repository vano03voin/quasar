using Quasar.ClusterDeployment;
using Quasar.Host.Contract.V1;
using System.Text.Json;

if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("Run these filesystem tests on Windows NTFS or Linux.");
string root = Path.Combine(Path.GetTempPath(), "quasar-content-native-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string? secondVolume = args.Length == 1 ? Path.Combine(Path.GetFullPath(args[0]), "quasar-content-native-" + Guid.NewGuid().ToString("N")) : null;
int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
bool ReadOnly(string path) => OperatingSystem.IsWindows()
    ? (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0
    : (File.GetUnixFileMode(path) & UnixFileMode.UserWrite) == 0;
async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (InvalidDataException) { assertions++; return; }
    throw new Exception(message);
}
try
{
    string source = Path.Combine(root, "source");
    await File.WriteAllTextAsync(source, "good");
    var pin = new DeploymentFile(ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(source)), 4, false);
    string snapshots = Path.Combine(root, "snapshots");
    string first = Path.Combine(snapshots, "one/file"), second = Path.Combine(snapshots, "two/file");
    string key = pin.Sha256 + "-r";
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
    {
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { using var secondWriter = await ClusterContentStore.OpenAsync(snapshots, timeout.Token); throw new Exception("Store admitted simultaneous writer."); }
            catch (OperationCanceledException) { assertions++; }
        }
        await content.ReferenceAsync(source, first, pin, default);
        await content.ReferenceAsync(source, second, pin, default);
        Check(ClusterContentStore.ReferenceCount(first) == 3, "Snapshots must share one object.");
        Check(ReadOnly(first) && ReadOnly(second), "Both references must be immutable.");
        await Reject(() => content.ReferenceAsync(source, Path.Combine(snapshots, "wrong/file"), pin with { Bytes = 8 }, default),
            "Cached object bypassed size verification.");
        try { await File.WriteAllTextAsync(first, "evil"); throw new Exception("Immutable write succeeded."); }
        catch (UnauthorizedAccessException) { assertions++; }
        string mutable = Path.Combine(root, "mutable");
        await ClusterWorldFiles.CopyAsync(Path.GetDirectoryName(first)!, mutable, default);
        string copy = Path.Combine(mutable, "file");
        Check(!ReadOnly(copy) && ClusterContentStore.ReferenceCount(copy) == 1, "Runtime copy must be independent/writable.");
        await File.WriteAllTextAsync(copy, "runtime");
        Check(await File.ReadAllTextAsync(first) == "good", "Runtime write damaged snapshot.");
        Check(ReadOnly(first), "Making runtime copy writable changed shared attributes.");

        // Same bytes with different executable metadata must never collapse into one object.
        string executable = Path.Combine(snapshots, "exec/file");
        await content.ReferenceAsync(source, executable, pin with { Executable = true }, default);
        Check(ClusterContentStore.ReferenceCount(executable) == 2, "Executable-mode key was collapsed.");
        Check(File.Exists(Path.Combine(snapshots, ".objects", pin.Sha256 + "-x")), "Executable object absent.");

        // Adopt an ordinary read-only legacy snapshot atomically; never mutate its attributes first.
        string legacy = Path.Combine(snapshots, "legacy/мир-файл");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.Copy(first, legacy);
        Check(ClusterContentStore.ReferenceCount(legacy) == 1, "Legacy copy unexpectedly linked.");
        await content.AdoptAsync(legacy, pin, default);
        Check(ClusterContentStore.ReferenceCount(legacy) == 4, $"Legacy adoption did not reuse object: links={ClusterContentStore.ReferenceCount(legacy)}, first={ClusterContentStore.ReferenceCount(first)}.");
        Check(ReadOnly(legacy) && ReadOnly(first), "Adoption changed read-only protection.");
        await content.AdoptAsync(legacy, pin, default);
        Check(ClusterContentStore.ReferenceCount(legacy) == 4, "Repeated adoption leaked references.");

        ClusterContentStore.DeleteTree(Path.GetDirectoryName(first)!);
        content.Prune();
        Check(ReadOnly(second) && await File.ReadAllTextAsync(second) == "good", "Prune changed retained reference.");
        Check(ClusterContentStore.ReferenceCount(second) == 3, "Deleting one snapshot removed other references.");
        ClusterContentStore.DeleteTree(Path.GetDirectoryName(legacy)!);
        ClusterContentStore.DeleteTree(Path.GetDirectoryName(second)!);
        content.Prune();
        Check(!File.Exists(Path.Combine(snapshots, ".objects", key)), "Unreferenced object was not pruned.");
        Check(File.Exists(executable) && ReadOnly(executable), "Prune touched retained executable object.");
        ClusterContentStore.DeleteTree(Path.GetDirectoryName(executable)!);
    }
    Check(Directory.GetFiles(Path.Combine(snapshots, ".objects")).Length == 1, "Objects leaked after last reference.");

    // Invalid pins must not publish an object/reference and must clean private staging.
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
    {
        await Reject(() => content.ReferenceAsync(source, first, pin with { Bytes = 7 }, default), "Wrong size was accepted.");
        await Reject(() => content.ReferenceAsync(source, first, pin with { Sha256 = new string('0', 64) }, default), "Wrong hash was accepted.");
        Check(!File.Exists(first), "Failed verification published a reference.");
        Check(!Directory.EnumerateFiles(Path.Combine(snapshots, ".objects"), ".write-*").Any(), "Failed verification left staging.");
    }

    // Reopening must verify bytes again even when a previous owner modified the object.
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
        await content.ReferenceAsync(source, first, pin, default);
    if (OperatingSystem.IsWindows()) File.SetAttributes(first, File.GetAttributes(first) & ~FileAttributes.ReadOnly);
    else File.SetUnixFileMode(first, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    await File.WriteAllTextAsync(first, "evil");
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
    {
        await Reject(() => content.ReferenceAsync(source, second, pin, default), "Corrupt cached object reused.");
        await Reject(() => content.ReferenceAsync(source, second, pin, default), "Failed verification poisoned the verified cache.");
    }
    ClusterContentStore.DeleteTree(Path.GetDirectoryName(first)!);
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default)) { }

    // Owner receipts retain a shared snapshot until the final cluster releases it.
    string ownerHash = new string('a', 64), owned = Path.Combine(snapshots, ownerHash, "file");
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
        await content.ReferenceAsync(source, owned, pin, default);
    ClusterInputOwners.Track(snapshots, ownerHash, "one");
    ClusterInputOwners.Track(snapshots, ownerHash, "two");
    await ClusterInputOwners.RetainAsync(snapshots, "one", new HashSet<string>(), default);
    Check(File.Exists(owned) && ReadOnly(owned), "First owner release deleted shared snapshot.");
    await ClusterInputOwners.RetainAsync(snapshots, "two", new HashSet<string>(), default);
    Check(!File.Exists(owned) && !File.Exists(Path.Combine(snapshots, ".objects", key)), "Last owner did not prune.");

    // Stores on the same volume share objects; a separate volume gets its own objects.
    string shared = Path.Combine(root, "global-store"), local = Path.Combine(root, "same-volume");
    ClusterContentStore.RootDirectory = shared;
    using (var content = await ClusterContentStore.OpenAsync(local, default))
        await content.ReferenceAsync(source, Path.Combine(local, "file"), pin, default);
    Check(File.Exists(Path.Combine(shared, ".objects", key)), "Same-volume configured store was not used.");
    Check(!Directory.Exists(Path.Combine(local, ".objects")), "Same-volume store duplicated objects.");
    if (secondVolume is not null)
    {
        Directory.CreateDirectory(secondVolume);
        using (var content = await ClusterContentStore.OpenAsync(secondVolume, default))
            await content.ReferenceAsync(source, Path.Combine(secondVolume, "file"), pin, default);
        Check(File.Exists(Path.Combine(secondVolume, ".objects", key)), "Cross-volume storage did not fall back locally.");
        Check(ClusterContentStore.ReferenceCount(Path.Combine(secondVolume, "file")) == 2, "Cross-volume fallback did not use hard links.");
        Check(ClusterContentStore.ReferenceCount(Path.Combine(local, "file")) == 2, "Cross-volume fallback touched first-volume references.");
    }
    ClusterContentStore.RootDirectory = null;

    // Collection must preserve an installation containing an active runtime path,
    // including native separators/casing, while rejecting a sibling-prefix match.
    string installations = Path.Combine(root, "installations");
    async Task<string> Installation(long revision)
    {
        var inputs = new ClusterDeploymentInputs("test", revision, "0.1.0", "package", "commit", "dependencies",
            Path.Combine(root, "package"), Path.Combine(root, "dependencies"), new() { ["Package/file"] = pin });
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions);
        string path = Path.Combine(installations, ClusterDeploymentFiles.Hash(metadata));
        Directory.CreateDirectory(Path.Combine(path, "Package"));
        File.Copy(source, Path.Combine(path, "Package/file"));
        await File.WriteAllBytesAsync(Path.Combine(path, "inputs.json"), metadata);
        return path;
    }
    string descendant = await Installation(1), alias = await Installation(2), sibling = await Installation(3);
    string aliasRoot = OperatingSystem.IsWindows() ? alias.ToUpperInvariant() : alias;
    int removed = await ClusterDeploymentFiles.CollectInstallationsAsync(installations, "test", new HashSet<string>(),
        [Path.Combine(descendant, "Package", "runtime"), aliasRoot + Path.DirectorySeparatorChar,
            Path.Combine(sibling + "-unrelated", "runtime")], default);
    Check(removed == 1, "Collection did not distinguish active descendants from sibling prefixes.");
    Check(File.Exists(Path.Combine(descendant, "Package/file")), "Collection deleted an active installation's ancestor.");
    Check(File.Exists(Path.Combine(alias, "Package/file")), "Collection missed a casing/trailing-separator alias.");
    Check(!Directory.Exists(sibling), "Sibling-prefix path incorrectly protected an unused installation.");
    removed = await ClusterDeploymentFiles.CollectInstallationsAsync(installations, "test", new HashSet<string>(), [], default);
    Check(removed == 2, "Inactive installations were not released.");
    Check(!File.Exists(Path.Combine(installations, ".objects", key)), "Released installations leaked content objects.");

    // No symlink privileges needed on Windows: a junction still exercises reparse rejection.
    string outside = Path.Combine(root, "outside"), unsafeRoot = Path.Combine(root, "unsafe");
    Directory.CreateDirectory(outside); Directory.CreateDirectory(unsafeRoot);
    await File.WriteAllTextAsync(Path.Combine(outside, "keep"), "untouched");
    string link = Path.Combine(unsafeRoot, "link");
    if (OperatingSystem.IsWindows())
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(outside);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "Cannot create test junction.");
    }
    else Directory.CreateSymbolicLink(link, outside);
    await Reject(async () => { using var content = await ClusterContentStore.OpenAsync(Path.Combine(link, "new-directory"), default); },
        "Opening storage traversed a reparse point.");
    Check(!Directory.Exists(Path.Combine(outside, "new-directory")), "Rejected storage created a directory through a junction.");
    using (var content = await ClusterContentStore.OpenAsync(snapshots, default))
        await Reject(() => content.ReferenceAsync(source, Path.Combine(link, "new-directory/file"), pin, default),
            "Publishing content traversed a reparse point.");
    Check(!Directory.Exists(Path.Combine(outside, "new-directory")), "Rejected reference created a directory through a junction.");
    await Reject(() => { ClusterContentStore.DeleteTree(unsafeRoot); return Task.CompletedTask; }, "Cleanup followed a reparse point.");
    Check(await File.ReadAllTextAsync(Path.Combine(outside, "keep")) == "untouched", "Outside target changed.");
    Directory.Delete(link);
    Console.WriteLine($"PASS: {assertions} native content-storage assertions on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    ClusterContentStore.RootDirectory = null;
    ClusterContentStore.DeleteTree(root);
    if (secondVolume is not null && Directory.Exists(secondVolume)) ClusterContentStore.DeleteTree(secondVolume);
}
return 0;
