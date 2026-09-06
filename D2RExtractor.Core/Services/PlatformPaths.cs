using System.Collections.Concurrent;

namespace D2RExtractor.Services;

/// <summary>
/// Bridges CASC's path conventions to the host filesystem.
///
/// CASC virtual paths are backslash-canonical and case-insensitive, which matches Windows
/// exactly — so every method here is a no-op there. On Linux they do not match, in three
/// distinct ways, and each needs handling at a different boundary:
///
/// <list type="number">
///   <item>Separators, CASC → filesystem: <c>data\global\x.dc6</c> must become
///         <c>data/global/x.dc6</c> before it reaches <see cref="Path.Combine(string, string)"/>,
///         or it produces a single file with backslashes in its name.</item>
///   <item>Separators, filesystem → CASC: paths read back off disk (via
///         <see cref="Path.GetRelativePath"/>) arrive with <c>/</c> and must be converted back
///         before being compared against manifest keys, which stay backslash-canonical so the
///         on-disk manifest format is identical on both platforms.</item>
///   <item>Casing: the game ships a <c>Data</c> directory while CASC paths say <c>data</c>.
///         On Windows these are the same directory; on Linux, using the wrong one either
///         misses the existing tree or creates a second one beside it.</item>
/// </list>
/// </summary>
public static class PlatformPaths
{
    /// <summary>True when '\' is not the host separator, i.e. everywhere except Windows.</summary>
    private static readonly bool NeedsTranslation = Path.DirectorySeparatorChar != '\\';

    /// <summary>Converts a backslash-canonical CASC relative path to a host-native one.</summary>
    public static string ToNative(string cascRelativePath) =>
        NeedsTranslation ? cascRelativePath.Replace('\\', Path.DirectorySeparatorChar) : cascRelativePath;

    /// <summary>
    /// Converts a host-native relative path back to the backslash-canonical form used as the
    /// key in manifests and lookup dictionaries.
    /// </summary>
    public static string ToCasc(string nativeRelativePath) =>
        NeedsTranslation ? nativeRelativePath.Replace(Path.DirectorySeparatorChar, '\\') : nativeRelativePath;

    /// <summary>
    /// Combines an install root with a backslash-canonical CASC relative path, translating
    /// separators and resolving the leading directory to whatever casing is already on disk.
    ///
    /// <para>
    /// The casing step is not cosmetic. CASC paths say <c>data\</c> while the game ships
    /// <c>Data</c>; on Windows those are one directory, so extraction merges into the existing
    /// tree. Combining naively on Linux instead creates a second <c>data/</c> beside it and
    /// splits the extraction from the install it belongs to.
    /// </para>
    /// </summary>
    public static string Combine(string root, string cascRelativePath)
    {
        if (!NeedsTranslation)
            return Path.Combine(root, cascRelativePath);

        int sep = cascRelativePath.IndexOf('\\');
        if (sep <= 0)
            return Path.Combine(root, ToNative(cascRelativePath));

        return Path.Combine(ResolveDirectory(root, cascRelativePath[..sep]),
                            ToNative(cascRelativePath[(sep + 1)..]));
    }

    /// <summary>
    /// Cache of resolved directory casings.
    ///
    /// <para>
    /// This is what makes resolution <b>stable</b>, and that matters more than the speed. Probing
    /// the filesystem every time is not merely slow, it is wrong: an extraction creates
    /// directories as it runs, so an uncached probe can answer "Data" before the first write and
    /// "data" after, scattering files and manifests across both. Deciding once per process keeps
    /// a whole operation self-consistent.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<(string Parent, string Name), string> CasingCache = new();

    /// <summary>
    /// Returns the path to a child directory of <paramref name="parent"/>, preferring whatever
    /// casing already exists on disk, and falling back to <paramref name="name"/> unchanged when
    /// nothing matches so a create-then-use flow still works.
    /// </summary>
    public static string ResolveDirectory(string parent, string name)
    {
        if (!NeedsTranslation)
            return Path.Combine(parent, name);

        return CasingCache.GetOrAdd(
            (Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), name),
            static key => Probe(key.Parent, key.Name));
    }

    private static string Probe(string parent, string name)
    {
        string direct = Path.Combine(parent, name);
        if (Directory.Exists(direct))
            return direct;

        try
        {
            foreach (string candidate in Directory.EnumerateDirectories(parent))
            {
                if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }

        return direct;
    }
}
