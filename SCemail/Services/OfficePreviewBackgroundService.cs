namespace SCemail.Services;

public sealed class OfficePreviewBackgroundService
    : BackgroundService
{
    private readonly IOfficePreviewQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OfficePreviewBackgroundService> _logger;

    public OfficePreviewBackgroundService(
        IOfficePreviewQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<OfficePreviewBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Office Preview Background Service avviato.");

        while (!stoppingToken.IsCancellationRequested)
        {
            OfficePreviewJob? job = null;

            try
            {
                job = await _queue.DequeueAsync(
                    stoppingToken);

                await ProcessAsync(
                    job,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Errore nel worker Office Preview.");
            }
            finally
            {
                /*
                 * Fondamentale:
                 * libera la chiave del job nella queue,
                 * così in futuro lo stesso allegato
                 * può essere accodato di nuovo se necessario.
                 */
                if (job is not null)
                {
                    _queue.Complete(job);
                }
            }
        }
    }

    private async Task ProcessAsync(
        OfficePreviewJob job,
        CancellationToken ct)
    {
        using var scope =
            _scopeFactory.CreateScope();

        var previewService =
            scope.ServiceProvider
                .GetRequiredService<OfficePreviewService>();

        /*
         * Sul server LibreOffice potrebbe
         * non essere ancora installato.
         *
         * Non deve essere considerato
         * un errore dell'applicazione.
         */
        if (!previewService.IsAvailable())
        {
            _logger.LogDebug(
                "LibreOffice non disponibile. Preview Office saltata. AttachmentId={AttachmentId}",
                job.AttachmentId);

            return;
        }

        try
        {
            _logger.LogInformation(
                "Generazione preventiva preview Office. AttachmentId={AttachmentId}, Source={Source}, File={File}",
                job.AttachmentId,
                job.SourceType,
                job.FullPath);

            await previewService.GetOrCreatePdfAsync(
                job.FullPath,
                job.AttachmentId,
                job.SourceType,
                ct);

            _logger.LogInformation(
                "Preview Office pronta. AttachmentId={AttachmentId}",
                job.AttachmentId);
        }
        catch (LibreOfficeUnavailableException)
        {
            _logger.LogDebug(
                "LibreOffice non disponibile durante la conversione. AttachmentId={AttachmentId}",
                job.AttachmentId);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            /*
             * IMPORTANTISSIMO:
             * una preview fallita non deve
             * bloccare download mail o invio.
             */
            _logger.LogWarning(
                ex,
                "Generazione preventiva preview Office fallita. AttachmentId={AttachmentId}",
                job.AttachmentId);
        }
    }
}