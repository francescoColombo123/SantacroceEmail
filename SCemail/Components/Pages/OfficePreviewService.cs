using System.Collections.Concurrent;
using System.Diagnostics;

namespace SCemail.Services;

public class OfficePreviewService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OfficePreviewService> _logger;

    /*
     * Evitiamo che due richieste dello stesso allegato
     * eseguano contemporaneamente la stessa conversione.
     */
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        ConversionLocks = new();

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx",
            ".xls",
            ".xlsx"
        };

    public OfficePreviewService(
        IConfiguration configuration,
        ILogger<OfficePreviewService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    // ============================================================
    // DISPONIBILITÀ LIBREOFFICE
    // ============================================================

    public OfficePreviewAvailability GetAvailability()
    {
        var executablePath =
            _configuration["LibreOffice:ExecutablePath"];

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new OfficePreviewAvailability(
                false,
                null,
                "Percorso LibreOffice non configurato.");
        }

        /*
         * Nel nostro caso Windows usa un percorso assoluto:
         *
         * C:\Program Files\LibreOffice\program\soffice.com
         */
        if (Path.IsPathRooted(executablePath))
        {
            if (!File.Exists(executablePath))
            {
                return new OfficePreviewAvailability(
                    false,
                    executablePath,
                    "LibreOffice non è installato oppure l'eseguibile configurato non esiste.");
            }

            return new OfficePreviewAvailability(
                true,
                executablePath,
                null);
        }

        /*
         * Caso futuro Linux/Docker:
         * se viene configurato semplicemente "libreoffice"
         * o "soffice", non possiamo verificare con File.Exists.
         *
         * In quel caso proviamo ad avviarlo.
         */
        try
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--version");

            using var process =
                Process.Start(startInfo);

            if (process == null)
            {
                return new OfficePreviewAvailability(
                    false,
                    executablePath,
                    "Impossibile avviare LibreOffice.");
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Ignoriamo errore di kill durante il check
                }

                return new OfficePreviewAvailability(
                    false,
                    executablePath,
                    "Timeout durante il controllo di LibreOffice.");
            }

            return new OfficePreviewAvailability(
                process.ExitCode == 0,
                executablePath,
                process.ExitCode == 0
                    ? null
                    : $"LibreOffice ha restituito ExitCode {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LibreOffice non disponibile. Executable={Executable}",
                executablePath);

            return new OfficePreviewAvailability(
                false,
                executablePath,
                "LibreOffice non disponibile.");
        }
    }

    public bool IsAvailable()
        => GetAvailability().Available;

    // ============================================================
    // SUPPORTO FORMATO
    // ============================================================

    public bool IsSupported(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var extension =
            Path.GetExtension(filePath);

        return SupportedExtensions.Contains(extension);
    }

    // ============================================================
    // CONVERSIONE
    // ============================================================

    public async Task<string> GetOrCreatePdfAsync(
        string sourcePath,
        int attachmentId,
        string sourceType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException(
                "Path sorgente non valorizzato.",
                nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "File allegato non trovato.",
                sourcePath);
        }

        var extension =
            Path.GetExtension(sourcePath);

        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException(
                $"Formato Office {extension} non supportato.");
        }

        var availability =
            GetAvailability();

        if (!availability.Available ||
            string.IsNullOrWhiteSpace(availability.ExecutablePath))
        {
            throw new LibreOfficeUnavailableException(
                availability.Message
                ?? "LibreOffice non disponibile.");
        }

        var previewRoot =
            _configuration["Attachments:PreviewPath"];

        if (string.IsNullOrWhiteSpace(previewRoot))
        {
            previewRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "SCemail",
                    "OfficePreview");
        }

        var safeSourceType =
            string.Equals(
                sourceType,
                "sent",
                StringComparison.OrdinalIgnoreCase)
                ? "sent"
                : "received";

        var outputDirectory =
            Path.Combine(
                previewRoot,
                safeSourceType,
                attachmentId.ToString());

        Directory.CreateDirectory(
            outputDirectory);

        var fileNameWithoutExtension =
            Path.GetFileNameWithoutExtension(
                sourcePath);

        var expectedPdf =
            Path.Combine(
                outputDirectory,
                fileNameWithoutExtension + ".pdf");

        /*
         * La lock è specifica per il PDF finale.
         */
        var conversionLock =
            ConversionLocks.GetOrAdd(
                expectedPdf,
                _ => new SemaphoreSlim(1, 1));

        await conversionLock.WaitAsync(ct);

        try
        {
            /*
             * CACHE
             *
             * Se il PDF esiste ed è più recente del file Office
             * non facciamo ripartire LibreOffice.
             */
            if (IsCachedPreviewValid(
                sourcePath,
                expectedPdf))
            {
                _logger.LogDebug(
                    "Utilizzata preview Office da cache. AttachmentId={AttachmentId}, Pdf={Pdf}",
                    attachmentId,
                    expectedPdf);

                return expectedPdf;
            }

            /*
             * Directory temporanea differente per ogni conversione.
             *
             * Questo evita interferenze se più conversioni
             * LibreOffice vengono eseguite contemporaneamente.
             */
            var conversionId =
                Guid.NewGuid()
                    .ToString("N");

            var tempRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "SCemail",
                    "LibreOffice",
                    conversionId);

            var tempOutputDirectory =
                Path.Combine(
                    tempRoot,
                    "output");

            var libreOfficeProfile =
                Path.Combine(
                    tempRoot,
                    "profile");

            Directory.CreateDirectory(
                tempOutputDirectory);

            Directory.CreateDirectory(
                libreOfficeProfile);

            try
            {
                var generatedPdf =
                    await ConvertToPdfAsync(
                        availability.ExecutablePath,
                        sourcePath,
                        tempOutputDirectory,
                        libreOfficeProfile,
                        ct);

                /*
                 * La conversione è completata:
                 * copiamo il PDF nella nostra cache definitiva.
                 */
                File.Copy(
                    generatedPdf,
                    expectedPdf,
                    overwrite: true);

                _logger.LogInformation(
                    "Preview Office generata. AttachmentId={AttachmentId}, Source={Source}, Pdf={Pdf}",
                    attachmentId,
                    sourcePath,
                    expectedPdf);

                return expectedPdf;
            }
            finally
            {
                TryDeleteDirectory(
                    tempRoot);
            }
        }
        finally
        {
            conversionLock.Release();
        }
    }

    // ============================================================
    // LIBREOFFICE
    // ============================================================

    private async Task<string> ConvertToPdfAsync(
        string executablePath,
        string sourcePath,
        string outputDirectory,
        string libreOfficeProfile,
        CancellationToken ct)
    {
        /*
         * LibreOffice usa normalmente un profilo utente globale.
         *
         * Se il backend riceve più richieste contemporaneamente,
         * possono verificarsi lock.
         *
         * Per questo assegniamo un profilo temporaneo dedicato.
         */
        var profileUri =
            new Uri(
                libreOfficeProfile)
                .AbsoluteUri;

        var startInfo =
            new ProcessStartInfo
            {
                FileName = executablePath,

                UseShellExecute = false,

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                CreateNoWindow = true,

                WorkingDirectory =
                    Path.GetDirectoryName(sourcePath)
                    ?? Environment.CurrentDirectory
            };

        startInfo.ArgumentList.Add(
            "--headless");

        startInfo.ArgumentList.Add(
            "--nologo");

        startInfo.ArgumentList.Add(
            "--nodefault");

        startInfo.ArgumentList.Add(
            "--nofirststartwizard");

        startInfo.ArgumentList.Add(
            "--nolockcheck");

        startInfo.ArgumentList.Add(
            $"-env:UserInstallation={profileUri}");

        startInfo.ArgumentList.Add(
            "--convert-to");

        startInfo.ArgumentList.Add(
            "pdf");

        startInfo.ArgumentList.Add(
            "--outdir");

        startInfo.ArgumentList.Add(
            outputDirectory);

        startInfo.ArgumentList.Add(
            sourcePath);

        _logger.LogInformation(
            "Avvio LibreOffice. Executable={Executable}, Source={Source}, Output={Output}",
            executablePath,
            sourcePath,
            outputDirectory);

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Impossibile avviare LibreOffice.");

        var stdoutTask =
            process.StandardOutput
                .ReadToEndAsync();

        var stderrTask =
            process.StandardError
                .ReadToEndAsync();

        /*
         * Limite massimo di conversione.
         *
         * Evitiamo processi soffice appesi per sempre.
         */
        using var timeoutCts =
            CancellationTokenSource
                .CreateLinkedTokenSource(ct);

        timeoutCts.CancelAfter(
            TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(
                timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (Exception killEx)
            {
                _logger.LogWarning(
                    killEx,
                    "Errore durante la terminazione di LibreOffice.");
            }

            if (ct.IsCancellationRequested)
                throw;

            throw new TimeoutException(
                "Timeout durante la conversione del documento Office.");
        }

        var stdout =
            await stdoutTask;

        var stderr =
            await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "LibreOffice conversion error. ExitCode={ExitCode}, StdOut={StdOut}, StdErr={StdErr}",
                process.ExitCode,
                stdout,
                stderr);

            throw new InvalidOperationException(
                $"LibreOffice ha terminato la conversione con codice {process.ExitCode}.");
        }

        /*
         * Non ci fidiamo completamente del nome previsto.
         * Recuperiamo il PDF effettivamente prodotto.
         */
        var pdfFiles =
            Directory.GetFiles(
                outputDirectory,
                "*.pdf",
                SearchOption.TopDirectoryOnly);

        if (pdfFiles.Length == 0)
        {
            _logger.LogError(
                "LibreOffice non ha prodotto PDF. Source={Source}, StdOut={StdOut}, StdErr={StdErr}",
                sourcePath,
                stdout,
                stderr);

            throw new InvalidOperationException(
                "LibreOffice non ha generato il PDF.");
        }

        if (pdfFiles.Length > 1)
        {
            _logger.LogWarning(
                "LibreOffice ha prodotto più PDF. Verrà utilizzato il primo. Source={Source}",
                sourcePath);
        }

        return pdfFiles[0];
    }

    // ============================================================
    // CACHE
    // ============================================================

    private static bool IsCachedPreviewValid(
        string sourcePath,
        string pdfPath)
    {
        if (!File.Exists(pdfPath))
            return false;

        var sourceLastWrite =
            File.GetLastWriteTimeUtc(
                sourcePath);

        var previewLastWrite =
            File.GetLastWriteTimeUtc(
                pdfPath);

        return previewLastWrite >=
               sourceLastWrite;
    }

    // ============================================================
    // CLEANUP
    // ============================================================

    private void TryDeleteDirectory(
        string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(
                    directory,
                    recursive: true);
            }
        }
        catch (Exception ex)
        {
            /*
             * Non deve fallire la preview solo perché
             * Windows sta ancora rilasciando un file temporaneo.
             */
            _logger.LogWarning(
                ex,
                "Impossibile eliminare directory temporanea LibreOffice {Directory}",
                directory);
        }
    }
}

public sealed record OfficePreviewAvailability(
    bool Available,
    string? ExecutablePath,
    string? Message);

public sealed class LibreOfficeUnavailableException
    : Exception
{
    public LibreOfficeUnavailableException(
        string message)
        : base(message)
    {
    }
}