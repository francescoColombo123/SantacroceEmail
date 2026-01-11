namespace SCemail.Components.Data
{
    using MailKit.Net.Imap;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using SCemail.Components.Data;

    public class ImapHealthCheckService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public ImapHealthCheckService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // attende 30s dopo lo startup
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckCaselleAsync(stoppingToken);

                // ⏱ ogni 30 minuti (puoi cambiare)
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        private async Task CheckCaselleAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();

            var caselle = await db.CasellePosta.ToListAsync(ct);

            foreach (var c in caselle)
            {
                c.StatoConn = await TestImapAsync(c);
            }

            await db.SaveChangesAsync(ct);
        }

        private async Task<string> TestImapAsync(CasellaPosta c)
        {
            try
            {
                using var client = new ImapClient();
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;

                await client.ConnectAsync(c.ImapHost, c.ImapPort, c.UseSsl == "Y");
                await client.AuthenticateAsync(c.Email, c.Password);
                await client.DisconnectAsync(true);

                return "OK";
            }
            catch
            {
                return "ERROR";
            }
        }
    }

}
