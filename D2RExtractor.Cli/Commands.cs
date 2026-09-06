using D2RExtractor.Models;
using D2RExtractor.Services;

namespace D2RExtractor.Cli;

/// <summary>
/// The CLI verbs. State lives in the same settings.json the GUI uses (ManifestService), so an
/// install added here shows up there and vice versa.
/// </summary>
internal static class Commands
{
    // ---------------------------------------------------------------- install management

    public static int Add(CommandLine cli)
    {
        if (cli.Target == null)
            return Fail("add needs a path: d2rextractor add <folder>");

        string path = Path.GetFullPath(cli.Target);
        if (!Directory.Exists(path))
            return Fail($"No such folder: {path}");

        string? problem = CascExtractorService.ValidateInstallationFolder(path);
        if (problem != null)
            return Fail(problem);

        var installs = ManifestService.LoadInstallations();
        if (installs.Any(i => PathsEqual(i.FolderPath, path)))
        {
            Console.WriteLine($"Already added: {path}");
            return 0;
        }

        installs.Add(new D2RInstallation
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n
                ? n
                : "Diablo II Resurrected",
            FolderPath = path,
        });
        ManifestService.SaveInstallations(installs);
        Console.WriteLine($"Added: {path}");
        return 0;
    }

    public static int List()
    {
        var installs = ManifestService.LoadInstallations();
        if (installs.Count == 0)
        {
            Console.WriteLine("No installations. Add one with: d2rextractor add <folder>");
            return 0;
        }

        for (int i = 0; i < installs.Count; i++)
        {
            var install = installs[i];
            Refresh(install);
            Console.WriteLine($"[{i + 1}] {install.Name}  ({StateOf(install)})");
            Console.WriteLine($"    {install.FolderPath}");
        }
        return 0;
    }

    public static int Forget(CommandLine cli)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc))
            return rc;

        installs.RemoveAll(i => PathsEqual(i.FolderPath, install.FolderPath));
        ManifestService.SaveInstallations(installs);
        Console.WriteLine($"Forgot: {install.FolderPath}");
        Console.WriteLine("Extracted files were left alone. Use 'undo' first to remove them.");
        return 0;
    }

    // ---------------------------------------------------------------- inspection

    public static int Status(CommandLine cli)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc))
            return rc;

        var prefs = Preferences(cli);
        var manifest = ManifestService.LoadManifest(install);
        Refresh(install);

        Console.WriteLine($"{install.Name}");
        Console.WriteLine($"  folder        : {install.FolderPath}");
        Console.WriteLine($"  state         : {StateOf(install)}");

        if (manifest != null)
        {
            Console.WriteLine($"  extracted at  : {manifest.ExtractedAt:u}");
            Console.WriteLine($"  bytes on disk : {ConsoleProgress.Bytes(manifest.TotalBytesExtracted)}");
            Console.WriteLine($"  international : {(manifest.InternationalExtracted == true ? manifest.InternationalLanguage ?? "yes" : "no")}");
        }

        var planned = CascExtractorService.PlanOperation(
            manifest, prefs.ExtractInternationalFiles, prefs.InternationalLanguage);
        Console.WriteLine($"  next action   : {planned.ToString().ToLowerInvariant()}");

        string? space = CascExtractorService.CheckDiskSpace(install.FolderPath);
        if (space != null) Console.WriteLine($"  warning       : {space}");
        return 0;
    }

    // ---------------------------------------------------------------- operations

    public static int Extract(CommandLine cli, CancellationToken ct)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc))
            return rc;

        var prefs = Preferences(cli);

        string? space = CascExtractorService.CheckDiskSpace(install.FolderPath);
        if (space != null)
        {
            Console.WriteLine(space);
            if (!Confirm(cli, "Continue anyway?")) return 1;
        }

        if (!Confirm(cli, $"Extract D2R game files into {install.FolderPath}? This writes roughly 45-70 GB."))
            return 1;

        var service = new CascExtractorService();
        using var progress = new ConsoleProgress();
        var manifest = service.Extract(install, prefs.ExtractInternationalFiles,
            prefs.InternationalLanguage, progress, Log, ct);
        progress.Dispose();

        Console.WriteLine($"Extraction complete: {ConsoleProgress.Bytes(manifest.TotalBytesExtracted)} written.");
        Console.WriteLine("Launch D2R with -direct -txt to use the extracted files.");
        return 0;
    }

    public static int Update(CommandLine cli, CancellationToken ct)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc))
            return rc;

        var manifest = ManifestService.LoadManifest(install);
        if (manifest == null)
            return Fail("Nothing extracted yet for this installation. Run 'extract' first.");

        var prefs = Preferences(cli);
        var service = new CascExtractorService();
        using var progress = new ConsoleProgress();
        var summary = service.UpdateExtraction(install, manifest, prefs.ExtractInternationalFiles,
            prefs.InternationalLanguage, cli.Verify || prefs.VerifyFileContents, progress, Log, ct);
        progress.Dispose();

        Console.WriteLine($"Update complete: {summary.FilesWritten:N0} written " +
                          $"({ConsoleProgress.Bytes(summary.BytesWritten)}), " +
                          $"{summary.FilesRemoved:N0} removed, {summary.FilesUnchanged:N0} unchanged.");
        return 0;
    }

    public static int Undo(CommandLine cli, CancellationToken ct)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc))
            return rc;

        if (ManifestService.LoadManifest(install) == null)
            return Fail("Nothing extracted yet for this installation.");

        if (!Confirm(cli, $"Remove all extracted files from {install.FolderPath}?"))
            return 1;

        var service = new CascExtractorService();
        using var progress = new ConsoleProgress();
        service.UndoExtraction(install, progress, Log, ct);
        progress.Dispose();

        Console.WriteLine("Undo complete. The installation is back to its pre-extraction state.");
        return 0;
    }

    // ---------------------------------------------------------------- help

    public static int Version()
    {
        var v = typeof(CascExtractorService).Assembly.GetName().Version;
        Console.WriteLine($"d2rextractor {v?.ToString(3) ?? "unknown"}");
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine("""
            d2rextractor — extract Diablo II: Resurrected game files for faster loading

            USAGE
              d2rextractor <command> [target] [options]

            A target is either an installation number from 'list' or a folder path.
            When exactly one installation is configured, the target may be omitted.

            COMMANDS
              add <folder>        Register a D2R installation
              list                Show registered installations and their state
              forget [target]     Unregister an installation (leaves files on disk)
              status [target]     Show extraction state and what would happen next
              extract [target]    Extract game files (~45-70 GB)
              update [target]     Re-sync after a patch, writing only what changed
              undo [target]       Delete extracted files, restoring the install

            OPTIONS
              --international <lang>   Also extract this language (e.g. dede, frfr, eses)
              --no-international       Skip international files
              --verify                 Hash file contents during update, not just sizes
              -y, --yes                Do not prompt for confirmation
              -h, --help               Show this help
                  --version            Show the version

            NOTES
              Settings are shared with the GUI (settings.json), so installations added
              here appear there and vice versa.

              Battle.net installs need the native CascLib (libcasc.so) on the library
              path. Steam installs use the built-in reader and need nothing extra.
            """);
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private static void Log(string message) => Console.WriteLine($"  {message}");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"d2rextractor: {message}");
        return 1;
    }

    private static bool Confirm(CommandLine cli, string question)
    {
        if (cli.Yes) return true;
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"{question} Refusing to assume; pass --yes to proceed non-interactively.");
            return false;
        }
        Console.Write($"{question} [y/N] ");
        string? answer = Console.ReadLine();
        return answer != null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                               || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies command-line overrides on top of the stored preferences without persisting them,
    /// so a one-off '--international dede' does not silently change what the GUI does next.
    /// </summary>
    private static AppPreferences Preferences(CommandLine cli)
    {
        var prefs = ManifestService.LoadPreferences();
        if (cli.NoInternational)
        {
            prefs.ExtractInternationalFiles = false;
        }
        else if (cli.International != null)
        {
            prefs.ExtractInternationalFiles = true;
            prefs.InternationalLanguage = cli.International;
        }
        if (cli.Verify) prefs.VerifyFileContents = true;
        return prefs;
    }

    private static void Refresh(D2RInstallation install)
    {
        var prefs = ManifestService.LoadPreferences();
        var manifest = ManifestService.LoadManifest(install);
        install.RefreshState(manifest?.IsComplete, manifest?.InternationalExtracted,
            prefs.ExtractInternationalFiles, manifest?.InternationalLanguage, prefs.InternationalLanguage);
    }

    private static string StateOf(D2RInstallation install) =>
        install.IsExtracted ? "extracted"
        : install.IsPartiallyExtracted ? "incomplete — run update"
        : "not extracted";

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Resolves a target given as a 1-based index from 'list', a folder path, or omitted
    /// entirely when only one installation is configured.
    /// </summary>
    private static bool TryResolve(string? target, List<D2RInstallation> installs,
                                   out D2RInstallation install, out int exitCode)
    {
        install = null!;
        exitCode = 0;

        if (installs.Count == 0)
        {
            exitCode = Fail("No installations configured. Add one with: d2rextractor add <folder>");
            return false;
        }

        if (target == null)
        {
            if (installs.Count == 1) { install = installs[0]; return true; }
            exitCode = Fail($"{installs.Count} installations configured — name one (see 'list').");
            return false;
        }

        if (int.TryParse(target, out int index))
        {
            if (index < 1 || index > installs.Count)
            {
                exitCode = Fail($"No installation [{index}]. There are {installs.Count}.");
                return false;
            }
            install = installs[index - 1];
            return true;
        }

        string path = Path.GetFullPath(target);
        var match = installs.FirstOrDefault(i => PathsEqual(i.FolderPath, path));
        if (match == null)
        {
            exitCode = Fail($"Not a registered installation: {path}");
            return false;
        }
        install = match;
        return true;
    }
}
