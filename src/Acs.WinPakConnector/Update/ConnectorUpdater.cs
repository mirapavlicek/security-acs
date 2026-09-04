using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Acs.WinPakConnector.Update;

/// <summary>Nastavení samoaktualizace (sekce <c>Update</c> v appsettings).</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>Název Windows služby, kterou aktualizační skript zastaví a spustí.</summary>
    public string ServiceName { get; set; } = "AcsWinPakConnector";

    /// <summary>Kam skript po startu volá, aby ověřil, že nová verze běží.</summary>
    public string HealthUrl { get; set; } = "http://localhost:52001/health";

    /// <summary>Kam se ukládají přijaté balíky a protokol aktualizace; prázdné = <c>updates</c> vedle programu.</summary>
    public string? StagingDirectory { get; set; }
}

/// <summary>Přijatý balík připravený k instalaci.</summary>
public sealed record StagedUpdate(string Version, string Path, long Size, string Sha256, DateTime ReceivedUtc);

/// <summary>Stav samoaktualizace pro API i administraci.</summary>
public sealed record UpdateStatusDto(
    string CurrentVersion,
    bool Supported,
    StagedUpdate? Staged,
    DateTime? StartedUtc,
    string? Log);

/// <summary>
/// Aktualizace konektoru bez přístupu WIN-PAK serveru na internet: balík releasu
/// pošle ACS (nebo správce z prohlížeče) na port konektoru, konektor ho ověří
/// a spustí výměnu souborů. Sám si soubory přepsat nemůže (běží z nich), proto
/// spustí odpojený PowerShell s <c>Update-WinPakConnector.ps1</c> — ten službu
/// zastaví, zazálohuje, přepíše, spustí a při neúspěchu vrátí zálohu. Skript
/// je vložený v konektoru, aby se nespoléhalo na verzi v balíku.
/// </summary>
public sealed class ConnectorUpdater(IOptions<UpdateOptions> options, ILogger<ConnectorUpdater> logger)
{
    private const string ScriptResource = "Update-WinPakConnector.ps1";
    private const string ExeInPackage = "Acs.WinPakConnector.exe";
    private const string DllInPackage = "Acs.WinPakConnector.dll";

    private readonly object _gate = new();
    private StagedUpdate? _staged;
    private DateTime? _startedUtc;

    public static string CurrentVersion
        => typeof(ConnectorUpdater).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public string StagingDirectory
        => string.IsNullOrWhiteSpace(options.Value.StagingDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "updates")
            : options.Value.StagingDirectory;

    public string LogPath => Path.Combine(StagingDirectory, "update.log");

    /// <summary>Výměnu souborů umí skript jen na Windows se službou; jinde se balík jen přijme.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    public UpdateStatusDto Status()
    {
        lock (_gate)
        {
            return new UpdateStatusDto(CurrentVersion, Supported, _staged, _startedUtc, ReadLogTail());
        }
    }

    /// <summary>
    /// Uloží balík, ověří SHA-256 (je-li zadán) a obsah, přečte verzi z assembly
    /// v balíku. Nic nespouští.
    /// </summary>
    public async Task<StagedUpdate> StageAsync(Stream package, string? expectedSha256, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingDirectory);
        var temp = Path.Combine(StagingDirectory, $"incoming-{Guid.NewGuid():N}.zip");

        string sha256;
        await using (var file = File.Create(temp))
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await package.ReadAsync(buffer, ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256)
                && !string.Equals(Normalize(expectedSha256), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA-256 balíku nesouhlasí: očekáváno {Normalize(expectedSha256)}, spočteno {sha256}. Balík se zahazuje.");
            }

            var version = ReadPackageVersion(temp);
            var final = Path.Combine(StagingDirectory, $"AcsWinPakConnector-{version}-win-x64.zip");
            File.Move(temp, final, overwrite: true);

            var staged = new StagedUpdate(version, final, new FileInfo(final).Length, sha256, DateTime.UtcNow);
            lock (_gate)
            {
                _staged = staged;
                _startedUtc = null;
            }

            logger.LogInformation("Přijat balík konektoru {Version} ({Size} B, SHA-256 {Sha}).", version, staged.Size, sha256);
            return staged;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>Spustí výměnu souborů z připraveného balíku. Služba se během ní zastaví — volání se nevrátí „hotovo“, jen „spuštěno“.</summary>
    public StagedUpdate Start()
    {
        StagedUpdate staged;
        lock (_gate)
        {
            staged = _staged ?? throw new InvalidOperationException("Není připravený žádný balík — nejdřív ho nahrajte.");
            if (_startedUtc is { } started && DateTime.UtcNow - started < TimeSpan.FromMinutes(5))
                throw new InvalidOperationException($"Aktualizace už běží od {started:HH:mm:ss} UTC.");
            _startedUtc = DateTime.UtcNow;
        }

        if (!Supported)
            throw new PlatformNotSupportedException("Výměnu souborů umí konektor jen jako Windows služba; tady balík zůstává připravený v " + staged.Path);

        var script = WriteEmbeddedScript();
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var command = $"& '{script}' -ZipPath '{staged.Path}' -InstallDir '{installDir}' -ServiceName '{options.Value.ServiceName}' "
                      + $"-HealthUrl '{options.Value.HealthUrl}' *> '{LogPath}'";

        File.WriteAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Spouštím aktualizaci na {staged.Version} z {staged.Path}{Environment.NewLine}");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = StagingDirectory,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        // Odpojený proces: skript službu zastaví, čímž skončí i tento proces — skript musí žít dál.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell se nepodařilo spustit.");
        logger.LogWarning("Aktualizace konektoru na {Version} spuštěna; služba se za okamžik zastaví a znovu spustí.", staged.Version);
        return staged;
    }

    /// <summary>Vloží aktualizační skript z prostředků vedle balíku (vždy verze odpovídající tomuto konektoru).</summary>
    internal string WriteEmbeddedScript()
    {
        var path = Path.Combine(StagingDirectory, ScriptResource);
        using var resource = typeof(ConnectorUpdater).Assembly.GetManifestResourceStream(ScriptResource)
                             ?? throw new InvalidOperationException("Aktualizační skript není v konektoru přibalený.");
        using var file = File.Create(path);
        resource.CopyTo(file);
        return path;
    }

    /// <summary>Verze konektoru v balíku — z metadat assembly, bez načítání kódu.</summary>
    internal static string ReadPackageVersion(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var exe = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeInPackage, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"Balík neobsahuje {ExeInPackage} — není to balík konektoru.");
        var dll = zip.Entries.FirstOrDefault(e => e.Name.Equals(DllInPackage, StringComparison.OrdinalIgnoreCase)
                                                  && Path.GetDirectoryName(e.FullName) == Path.GetDirectoryName(exe.FullName))
                  ?? throw new InvalidOperationException($"Balík neobsahuje {DllInPackage} vedle {ExeInPackage}.");

        using var stream = new MemoryStream();
        using (var entry = dll.Open())
            entry.CopyTo(stream);
        stream.Position = 0;

        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var version = metadata.GetAssemblyDefinition().Version;
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private string? ReadLogTail()
    {
        try
        {
            if (!File.Exists(LogPath))
                return null;
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.Length <= 8000 ? text : text[^8000..];
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Normalize(string sha256)
        => sha256.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
}
