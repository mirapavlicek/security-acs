using System.IO.Compression;
using System.Security.Cryptography;
using Acs.WinPakConnector.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acs.WinPakConnector.Tests;

/// <summary>
/// Samoaktualizace konektoru: přijetí balíku (SHA-256, obsah, verze z assembly)
/// a stav. Výměnu souborů dělá PowerShell jen na Windows — tady se ověřuje
/// všechno před ní a to, že se mimo Windows nic nespustí.
/// </summary>
public sealed class ConnectorUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acs-updater-" + Guid.NewGuid().ToString("N"));

    private ConnectorUpdater CreateUpdater()
        => new(Options.Create(new UpdateOptions { StagingDirectory = _dir }), NullLogger<ConnectorUpdater>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Balík jako z workflow: složka winpak-connector s exe a dll konektoru (dll je ta skutečná — má verzi).</summary>
    private static byte[] Package(bool withExe = true)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (withExe)
                zip.CreateEntry("winpak-connector/Acs.WinPakConnector.exe").Open().Dispose();
            using (var dll = zip.CreateEntry("winpak-connector/Acs.WinPakConnector.dll").Open())
            using (var real = File.OpenRead(typeof(ConnectorUpdater).Assembly.Location))
                real.CopyTo(dll);
            zip.CreateEntry("winpak-connector/Update-WinPakConnector.ps1").Open().Dispose();
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task Balik_se_prijme_overi_a_precte_se_z_nej_verze()
    {
        var package = Package();
        var sha = Convert.ToHexStringLower(SHA256.HashData(package));
        var updater = CreateUpdater();

        var staged = await updater.StageAsync(new MemoryStream(package), $"{sha}  AcsWinPakConnector.zip", CancellationToken.None);

        Assert.Equal(ConnectorUpdater.CurrentVersion, staged.Version);
        Assert.Equal(sha, staged.Sha256);
        Assert.True(File.Exists(staged.Path));
        Assert.EndsWith($"AcsWinPakConnector-{staged.Version}-win-x64.zip", staged.Path);

        var status = updater.Status();
        Assert.Equal(staged, status.Staged);
        Assert.Null(status.StartedUtc);
    }

    [Fact]
    public async Task Nesouhlasici_SHA256_balik_zahodi()
    {
        var updater = CreateUpdater();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => updater.StageAsync(new MemoryStream(Package()), new string('0', 64), CancellationToken.None));

        Assert.Contains("SHA-256 balíku nesouhlasí", error.Message);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Null(updater.Status().Staged);
    }

    [Fact]
    public async Task Zip_bez_konektoru_se_odmitne()
    {
        var updater = CreateUpdater();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => updater.StageAsync(new MemoryStream(Package(withExe: false)), null, CancellationToken.None));

        Assert.Contains("není to balík konektoru", error.Message);
    }

    [Fact]
    public void Bez_pripraveneho_baliku_se_aktualizace_nespusti()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CreateUpdater().Start());

        Assert.Contains("nejdřív ho nahrajte", error.Message);
    }

    [Fact]
    public void Vlozeny_skript_se_vypise_do_staging_slozky()
    {
        var updater = CreateUpdater();
        Directory.CreateDirectory(_dir);

        var path = updater.WriteEmbeddedScript();

        var text = File.ReadAllText(path);
        Assert.Contains("Stop-Service", text);
        Assert.Contains("robocopy", text);
        // UTF-8 s BOM — Windows PowerShell 5.1 by jinak četl skript v ANSI a rozpadl se na parseru.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
    }

    [Fact]
    public async Task Mimo_Windows_se_vymena_nespusti_a_balik_zustane_pripraveny()
    {
        if (OperatingSystem.IsWindows())
            return;

        var updater = CreateUpdater();
        await updater.StageAsync(new MemoryStream(Package()), null, CancellationToken.None);

        Assert.Throws<PlatformNotSupportedException>(() => updater.Start());
        Assert.NotNull(updater.Status().Staged);
    }
}
