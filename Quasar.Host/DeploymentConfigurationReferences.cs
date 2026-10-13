using System.Text.Json;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

// Only generated configurations registered by preparation are eligible for removal.
// Runtime directories and unregistered operator/recovery files are never swept.
internal static class DeploymentConfigurationReferences
{
    private static readonly object Sync = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsDescendant(string directory, string root) => directory.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
    internal static void Track(string state, string cluster, string directory)
    {
        lock (Sync)
        {
            string root = Path.GetFullPath(Path.Combine(state, "conversions"));
            directory = Path.GetFullPath(directory);
            if (!IsDescendant(directory, root)) return;
            string path = Path.Combine(root, ".configuration-references.json");
            var references = Read(path);
            if (!references.TryGetValue(cluster, out var paths)) references[cluster] = paths = new(PathComparer);
            if (paths.Add(directory)) Write(path, references);
        }
    }

    internal static void Retain(string state, string cluster, IEnumerable<string> retained, IEnumerable<string> active)
    {
        lock (Sync)
        {
            string root = Path.GetFullPath(Path.Combine(state, "conversions"));
            var keep = retained.Concat(active).Select(Path.GetFullPath).ToHashSet(PathComparer);
            string path = Path.Combine(root, ".configuration-references.json");
            var references = Read(path);
            if (!references.TryGetValue(cluster, out var paths)) return;
            foreach (string directory in paths.Except(keep, PathComparer).ToArray())
            {
                if (!Path.IsPathFullyQualified(directory) || !PathComparer.Equals(Path.GetFullPath(directory), directory)
                    || !IsDescendant(directory, root))
                    throw new InvalidDataException("Configuration reference escapes managed storage.");
                if (Directory.Exists(directory))
                {
                    ClusterWorldFiles.Private(directory);
                    string manifest = Path.Combine(directory, "bundle.json");
                    var bundle = ExecutionBundle.ReadManifest(manifest, ExecutionBundle.Hash(File.ReadAllBytes(manifest)));
                    if (bundle.ClusterId != cluster || bundle.RuntimeRoot is null || !Path.IsPathFullyQualified(bundle.RuntimeRoot))
                        throw new InvalidDataException("Configuration ownership does not match its reference.");
                    string runtime = Path.GetFullPath(bundle.RuntimeRoot);
                    if (PathComparer.Equals(Path.TrimEndingDirectorySeparator(runtime), Path.TrimEndingDirectorySeparator(directory))
                        || IsDescendant(runtime, directory))
                        throw new InvalidDataException("Configuration contains live runtime storage.");
                    ClusterContentStore.DeleteTree(directory);
                }
                paths.Remove(directory);
            }
            Write(path, references);
        }
    }

    private static Dictionary<string, HashSet<string>> Read(string path)
    {
        if (!File.Exists(path)) return [];
        ClusterWorldFiles.Private(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Configuration references cannot be a link.");
        var references = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Configuration references are unreadable.");
        return references.ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, PathComparer), StringComparer.Ordinal);
    }
    private static void Write(string path, Dictionary<string, HashSet<string>> value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(value)); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
