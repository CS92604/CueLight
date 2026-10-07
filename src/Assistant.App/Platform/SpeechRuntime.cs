using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Assistant.Core;
using Whisper.net.LibraryLoader;

namespace Assistant.App.Platform;

/// <summary>
/// The app is one .exe, so the speech engine's native libraries (and the Visual C++ runtime they need)
/// travel inside it. The speech engine looks for them as ordinary files, so the first time speech
/// recognition starts they are unpacked into a folder of the user's own (under Local AppData) and the
/// engine is pointed there. After that, starting the app just checks the files are all still there.
///
/// Why bring the Visual C++ runtime along: a freshly installed Windows doesn't have it, and the
/// system-wide copy on some PCs is older than the one the engine was built with. The private copies are
/// loaded first, so the engine always runs against a runtime it was built for.
/// </summary>
public static class SpeechRuntime
{
    private const string Prefix = "native/";
    private const string VcPrefix = "native/vc/";

    /// <summary>Lets tests unpack somewhere other than the real data folder.</summary>
    internal static string? FolderOverride { get; set; }

    private static readonly object Gate = new();
    private static bool _prepared;

    /// <summary>Where this build's libraries are unpacked. Each build gets its own folder, so files of two
    /// versions are never mixed.</summary>
    public static string Folder => FolderOverride ?? Path.Combine(Parent, BuildId);

    private static string Parent => Path.Combine(AppPaths.DataDirectory, "runtime");

    private static string BuildId => typeof(SpeechRuntime).Module.ModuleVersionId.ToString("N")[..12];

    private static IReadOnlyList<string> Resources(Assembly assembly) =>
        assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>True when this build carries the speech libraries inside it (a published .exe does).</summary>
    public static bool IsEmbedded => Resources(typeof(SpeechRuntime).Assembly).Any(n => !n.StartsWith(VcPrefix, StringComparison.Ordinal));

    /// <summary>True when this build also carries the Visual C++ runtime files.</summary>
    public static bool HasVisualCppRuntime => Resources(typeof(SpeechRuntime).Assembly).Any(n => n.StartsWith(VcPrefix, StringComparison.Ordinal));

    /// <summary>Makes the speech engine findable. Safe to call repeatedly and from several threads.</summary>
    public static void Prepare()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_prepared) return;
            if (!IsEmbedded) { _prepared = true; return; } // a developer build: the engine sits beside the app as usual

            var folder = Folder;
            Extract(folder, typeof(SpeechRuntime).Assembly);
            LoadVisualCppRuntime(folder);
            // Only the folder part of this is used: the engine looks for "runtimes\win-x64" etc. beside it.
            RuntimeOptions.LibraryPath = Path.Combine(folder, "whisper.dll");
            if (FolderOverride is null) RemoveOlderBuilds(folder);
            _prepared = true;
            AppLog.Info($"Speech engine files are in {folder}");
        }
    }

    /// <summary>Writes every embedded file under <paramref name="folder"/>\runtimes\… unless an intact copy is
    /// already there. Returns the full paths of all the files that belong to this build.</summary>
    public static IReadOnlyList<string> Extract(string folder, Assembly assembly) =>
        Extract(folder, Resources(assembly).ToDictionary(n => n, n => (Func<Stream>)(() =>
            assembly.GetManifestResourceStream(n) ?? throw new FileNotFoundException($"{n} is missing from the app."))));

    internal static IReadOnlyList<string> Extract(string folder, IReadOnlyDictionary<string, Func<Stream>> resources)
    {
        var names = resources.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var vc = names.Where(n => n.StartsWith(VcPrefix, StringComparison.Ordinal)).ToList();
        var engine = names.Except(vc).ToList();
        var files = new List<string>();
        var folders = new HashSet<string>();

        foreach (var name in engine)
        {
            var relative = name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(folder, "runtimes", relative);
            WriteIfNeeded(resources[name], target);
            files.Add(target);
            folders.Add(Path.GetDirectoryName(target)!);
        }
        // Each engine folder gets its own copy, so it is complete wherever the engine loads it from.
        foreach (var dir in folders)
            foreach (var name in vc)
            {
                var target = Path.Combine(dir, name[VcPrefix.Length..]);
                WriteIfNeeded(resources[name], target);
                files.Add(target);
            }
        return files;
    }

    private static void WriteIfNeeded(Func<Stream> open, string target)
    {
        using var source = open();
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length == source.Length) return; // already there and whole
        }
        catch (IOException) { /* look again below */ }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Write beside it, then rename: a crash or a full disk can't leave a half-written library under its real name.
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(output);
            File.Move(temp, target, overwrite: true);
        }
        catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == source.Length)
        {
            // Someone else (a second copy starting at the same moment) put the same file there first.
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Loads our copies of the runtime first: a library that is already loaded under a name is the
    /// one every later import of that name gets.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void LoadVisualCppRuntime(string folder)
    {
        var dir = Path.Combine(folder, "runtimes", "win-x64");
        if (!Directory.Exists(dir)) return;
        foreach (var name in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue; // not shipped with this build: the PC's own copy is used
            if (!NativeLibrary.TryLoad(path, out _))
                AppLog.Warn($"Couldn't load {name} from {dir}; the PC's own copy will be used if it has one.");
        }
    }

    /// <summary>Deletes the folders of earlier builds (best effort: one that's in use stays).</summary>
    private static void RemoveOlderBuilds(string current)
    {
        try
        {
            if (!Directory.Exists(Parent)) return;
            foreach (var dir in Directory.GetDirectories(Parent))
            {
                if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Where each speech-engine and Visual C++ library the process really has loaded came from.
    /// For the self-test: it shows the private copies are the ones in use.</summary>
    public static IEnumerable<string> LoadedModules()
    {
        using var me = Process.GetCurrentProcess();
        foreach (ProcessModule m in me.Modules)
        {
            var name = m.ModuleName ?? "";
            if (name.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("vcomp", StringComparison.OrdinalIgnoreCase))
                yield return $"{name} ← {m.FileName}";
        }
    }
}
