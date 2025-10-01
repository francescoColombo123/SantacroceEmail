using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using SCemail.Components.Data;
using System.Text.RegularExpressions;

namespace SCemail.Components.Data
{
    public class MailService_NEW
    {
        private readonly HttpClient _http;
        private readonly IDbContextFactory<MailDbContext> _dbFactory;
        private readonly ILogger<MailService_NEW> _logger;

        public MailService_NEW(IDbContextFactory<MailDbContext> dbFactory,
                           ILogger<MailService_NEW> logger,
                           HttpClient http)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _http = http;
        }

        /* ================== HELPERS ================== */

        private static string NormalizeFolder(string s)
        {
            var t = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            t = Regex.Replace(t, @"\s+", " ");
            return t.ToUpperInvariant();
        }


        public async Task<List<string>> GetUtentiAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.InfoUsers 
                .AsNoTracking()
                .Where(u => !string.IsNullOrWhiteSpace(u.Utente))
                .Select(u => u.Utente!)
                .Distinct()
                .OrderBy(u => u)
                .ToListAsync(ct);
        }

        private static string NormalizeSubject(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var x = s.Trim();
            var patterns = new[] { "RE:", "R:", "FWD:", "FW:", "I:" };
            bool changed;
            do
            {
                changed = false;
                var t = x.TrimStart();
                foreach (var p in patterns)
                    if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        t = t.Substring(p.Length).TrimStart(' ', '\t', ':');
                        changed = true;
                    }
                x = t;
            } while (changed);
            return x.ToUpperInvariant();
        }

        private static string MapFolderToUi(string dbFolder)
        {
            var norm = NormalizeFolder(dbFolder);

            if (norm == "INBOX" || norm.StartsWith("INBOX")) return "inbox";
            if (norm.Contains("POSTA INVIATA") || norm.Contains("SENT")) return "sent";
            if (norm.Contains("BOZZE") || norm.Contains("DRAFTS")) return "drafts";
            if (norm.Contains("SPAM") || norm.Contains("JUNK")) return "spam";
            if (norm.Contains("CESTINO") || norm.Contains("TRASH")) return "trash";
            return "custom:" + dbFolder;
        }

        private static async Task EnsureOpenAsync(IMailFolder folder, FolderAccess access, CancellationToken ct)
        {
            if (!folder.IsOpen || folder.Access != access)
                await folder.OpenAsync(access, ct);
        }

        private static async Task<List<IMailFolder>> GetAllFoldersAsync(ImapClient client, CancellationToken ct)
        {
            var list = new List<IMailFolder> { client.Inbox };
            try { var all = client.GetFolder(SpecialFolder.All); if (all != null) list.Add(all); } catch { }

            foreach (var ns in client.PersonalNamespaces)
            {
                IMailFolder root;
                try { root = client.GetFolder(ns); }
                catch { continue; }

                await GatherFoldersRec(root, list, ct);
            }
            return list.GroupBy(f => f.FullName).Select(g => g.First()).ToList();
        }

        private static async Task GatherFoldersRec(IMailFolder root, List<IMailFolder> acc, CancellationToken ct)
        {
            if ((root.Attributes & FolderAttributes.NonExistent) != 0) return;

            var ban = FolderAttributes.NonExistent;
            if ((root.Attributes & ban) == 0 && !acc.Any(x => x.FullName == root.FullName))
                acc.Add(root);

            try
            {
                foreach (var sub in await root.GetSubfoldersAsync(false, ct))
                    await GatherFoldersRec(sub, acc, ct);
            }
            catch { }
        }

        /* ================== CASELLA ================== */

        public async Task<int> GetCasellaIdAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(usernameOrEmail))
                throw new ArgumentException("Username/Email mancante.", nameof(usernameOrEmail));

            var key = usernameOrEmail.Trim().ToUpperInvariant();

            await using var db = _dbFactory.CreateDbContext();

            var id = await db.CasellePosta
                .AsNoTracking()
                .Where(c =>
                    ((c.Username ?? "").Trim().ToUpper() == key) ||
                    ((c.Email ?? "").Trim().ToUpper() == key))
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);

            if (id != 0) return id;

            id = await db.CasellaAbilitazioni
                .AsNoTracking()
                .Where(a => ((a.Username ?? "").Trim().ToUpper() == key))
                .Select(a => a.CasellaId)
                .FirstOrDefaultAsync(ct);

            if (id != 0) return id;

            throw new InvalidOperationException($"Nessuna casella associata a '{usernameOrEmail}'.");
        }

        public async Task<List<string>> GetUserListAsync(CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            return await db.InfoUsers
                .AsNoTracking()
                .Select(u => u.Utente)
                .OrderBy(u => u)
                .ToListAsync(ct);
        }

        /* ================== FOLDER SIDEBAR ================== */

        public async Task<List<FolderItem_NEW>> GetFoldersAsync(int casellaId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var rawCounts = await db.EmailRicevute
                .AsNoTracking()
                .Where(e => e.CasellaId == casellaId)
                .GroupBy(e => e.FolderPath)
                .Select(g => new { Folder = g.Key, Cnt = g.Count() })
                .ToListAsync(ct);

            var perUi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rawCounts)
            {
                if (string.IsNullOrWhiteSpace(r.Folder)) continue;
                var ui = MapFolderToUi(r.Folder!);
                if (!perUi.ContainsKey(ui)) perUi[ui] = 0;
                perUi[ui] += r.Cnt;
            }

            var important = new[]
            {
                new FolderItem_NEW("inbox",  "Posta in arrivo", "bi-inbox-fill",           0, perUi.TryGetValue("inbox", out var c0) ? c0 : 0),
                new FolderItem_NEW("all",    "Tutti",           "bi-collection-fill",      1, perUi.Values.Sum()),
                new FolderItem_NEW("sent",   "Inviati",         "bi-send-fill",            2, perUi.TryGetValue("sent", out var c1) ? c1 : 0),
                new FolderItem_NEW("drafts", "Bozze",           "bi-file-earmark-text",    3, perUi.TryGetValue("drafts", out var c2) ? c2 : 0),
                new FolderItem_NEW("spam",   "Spam",            "bi-exclamation-octagon",  4, perUi.TryGetValue("spam", out var c3) ? c3 : 0),
                new FolderItem_NEW("trash",  "Cestino",         "bi-trash3-fill",          5, perUi.TryGetValue("trash", out var c4) ? c4 : 0),
            }.ToList();

            // migliora "Tutti": senza spam/trash
            var withoutTrashSpam = perUi.Where(kv => kv.Key != "trash" && kv.Key != "spam").Sum(kv => kv.Value);
            var idxAll = important.FindIndex(f => f.UiName == "all");
            if (idxAll >= 0) important[idxAll] = important[idxAll] with { Count = withoutTrashSpam };

            return important;
        }

        /* ================== LISTA EMAIL (VIRTUALIZE) ================== */

        public async Task<(List<EmailListItem_NEW>, int)> GetEmailPageAsync(
    int casellaId,
    int startIndex,
    int count,
    string? folderUi,
    CancellationToken ct = default)
        {
            try
            {
                await using var db = _dbFactory.CreateDbContext();

                var q = db.EmailRicevute
                    .AsNoTracking()
                    .Where(e => e.CasellaId == casellaId);

                if (string.IsNullOrWhiteSpace(folderUi) || folderUi == "all")
                {
                    q = q.Where(e =>
                        e.FolderPath == null ||
                        (!EF.Functions.Like(e.FolderPath!, "%CESTINO%") &&
                         !EF.Functions.Like(e.FolderPath!, "%TRASH%") &&
                         !EF.Functions.Like(e.FolderPath!, "%SPAM%") &&
                         !EF.Functions.Like(e.FolderPath!, "%JUNK%")));
                }
                else
                {
                    switch (folderUi.ToLower())
                    {
                        case "inbox":
                            q = q.Where(e => e.FolderPath != null &&
                                             (EF.Functions.Like(e.FolderPath!, "INBOX%")
                                              || EF.Functions.Like(e.FolderPath!, "%Inbox%")));
                            break;

                        case "sent":
                            q = q.Where(e => e.FolderPath != null &&
                                             (EF.Functions.Like(e.FolderPath!, "%POSTA INVIATA%")
                                              || EF.Functions.Like(e.FolderPath!, "%Sent%")));
                            break;

                        case "trash":
                            q = q.Where(e => e.FolderPath != null &&
                                             (EF.Functions.Like(e.FolderPath!, "%CESTINO%")
                                              || EF.Functions.Like(e.FolderPath!, "%Trash%")));
                            break;

                        case "spam":
                            q = q.Where(e => e.FolderPath != null &&
                                             (EF.Functions.Like(e.FolderPath!, "%SPAM%")
                                              || EF.Functions.Like(e.FolderPath!, "%Junk%")));
                            break;

                        case "drafts":
                            return (new List<EmailListItem_NEW>(), 0);

                        default:
                            var raw = folderUi.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)
                                ? folderUi[7..]
                                : folderUi;
                            q = q.Where(e => e.FolderPath == raw);
                            break;
                    }
                }

                // tieni solo le email non eliminate
                q = q.Where(e => e.Eliminato == null || e.Eliminato == "N");

                q = q.OrderByDescending(e => e.DataRicezione);

                var total = await q.CountAsync(ct);

                var pageRows = await q
                    .Skip(startIndex)
                    .Take(count)
                    .Select(e => new
                    {
                        e.Id,
                        e.DataRicezione,
                        e.Mittente,
                        e.Oggetto,
                        e.Aperto,
                        Preview = e.CorpoTesto ?? e.CorpoHtml,
                        Allegati = e.Allegati
                            .Select(a => new AllegatoItem_NEW(a.Id, a.NomeFile, a.MimeType))
                            .ToList()
                    })
                    .ToListAsync(ct);

                var items = pageRows.Select(r => new EmailListItem_NEW(
                    r.Id,
                    r.DataRicezione,
                    r.Mittente,
                    r.Oggetto,
                    r.Aperto,
                    r.Allegati.Count > 0,
                    1,
                    string.IsNullOrWhiteSpace(r.Preview)
                        ? null
                        : (r.Preview.Length > 180 ? r.Preview[..180] + "…" : r.Preview),
                    r.Allegati
                )).ToList();

                return (items, total);
            }
            catch (OperationCanceledException)
            {
                // scroll veloce → query annullata → ritorna vuoto
                return (new List<EmailListItem_NEW>(), 0);
            }
        }


        /* ================== DETTAGLIO & CONVERSAZIONE ================== */

        public async Task<EmailDetail_NEW?> GetEmailDetailAsync(int id, bool onlyMeta, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var q = db.EmailRicevute.AsNoTracking().Where(e => e.Id == id);

            if (onlyMeta)
            {
                var dto = await q.Select(e => new
                {
                    e.Id,
                    e.Mittente,
                    e.Destinatari,
                    e.Oggetto,
                    e.DataRicezione,
                    e.Aperto
                }).FirstOrDefaultAsync(ct);

                if (dto == null) return null;

                return new EmailDetail_NEW
                {
                    Id = dto.Id,
                    Mittente = dto.Mittente,
                    Destinatari = dto.Destinatari,
                    Oggetto = dto.Oggetto,
                    Data = dto.DataRicezione,
                    Aperto = dto.Aperto,
                    IsLoaded = false // ✅ fuori da EF, nessun problema
                };
            }
            else
            {
                var dto = await q.Select(e => new
                {
                    e.Id,
                    e.Mittente,
                    e.Destinatari,
                    e.Oggetto,
                    e.DataRicezione,
                    e.CorpoHtml,
                    e.CorpoTesto,
                    Allegati = e.Allegati.Select(a => new AllegatoItem_NEW(a.Id, a.NomeFile, a.MimeType)).ToList(),
                    e.Aperto
                }).FirstOrDefaultAsync(ct);

                if (dto == null) return null;

                return new EmailDetail_NEW
                {
                    Id = dto.Id,
                    Mittente = dto.Mittente,
                    Destinatari = dto.Destinatari,
                    Oggetto = dto.Oggetto,
                    Data = dto.DataRicezione,
                    CorpoHtml = dto.CorpoHtml,
                    CorpoTesto = dto.CorpoTesto,
                    Allegati = dto.Allegati,
                    Aperto = dto.Aperto,
                    IsLoaded = true // ✅ aggiunto solo dopo la query
                };
            }
        }



        public async Task<List<EmailDetail_NEW>> GetConversationAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var me = await db.EmailRicevute
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == emailId, ct);

            if (me is null) return new List<EmailDetail_NEW>();

            var subjNorm = NormalizeSubject(me.Oggetto);

            var list = await db.EmailRicevute
                .AsNoTracking()
                .Where(x => x.CasellaId == me.CasellaId && (x.Eliminato == null || x.Eliminato != "Y"))
                .OrderBy(x => x.DataRicezione)
                .Take(150)
                .ToListAsync(ct);

            var conv = list
                .Where(x => NormalizeSubject(x.Oggetto) == subjNorm)
                .Select(e => new EmailDetail_NEW
                {
                    Id = e.Id,
                    Mittente = e.Mittente,
                    Destinatari = e.Destinatari,
                    Oggetto = e.Oggetto,
                    Data = e.DataRicezione,
                    Aperto = e.Aperto,
                    IsLoaded = false,          // ⚠️ corpo non caricato
                    CorpoHtml = null,
                    CorpoTesto = null,
                    Allegati = new List<AllegatoItem_NEW>()
                })
                .ToList();

            if (conv.Count == 0)
            {
                conv.Add(new EmailDetail_NEW
                {
                    Id = me.Id,
                    Mittente = me.Mittente,
                    Destinatari = me.Destinatari,
                    Oggetto = me.Oggetto,
                    Data = me.DataRicezione,
                    Aperto = me.Aperto,
                    IsLoaded = false,
                    CorpoHtml = null,
                    CorpoTesto = null,
                    Allegati = new List<AllegatoItem_NEW>()
                });
            }

            return conv;
        }



        /* ================== STATE MUTATIONS ================== */

        public async Task MarkOpenedAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null && e.Aperto != "Y")
            {
                e.Aperto = "Y";
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task MoveToTrashAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null)
            {
                e.FolderPath = "[GMAIL]/Trash"; // adegua al tuo server
                await db.SaveChangesAsync(ct);
            }
        }

        /* ================== SEND (con allegati + append opzionale in Sent) ================== */

        public record OutgoingAttachment_NEW(string FileName, string? MimeType, byte[] Content);

        public async Task MarkUnreadAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null && e.Aperto != "N")
            {
                e.Aperto = "N";
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task<string> GetFirmaUtenteAsync(string username, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var casella = await db.CasellePosta
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Username == username || c.Email == username, ct);

            if (casella == null)
                return "";

            // Costruisci la firma nello stesso stile che usi in SendEmailAsync
            var firma = $@"
--
Cordiali saluti.<br/>
<b>{(string.IsNullOrWhiteSpace(casella.Nome) ? casella.NomeCompleto : casella.Nome)}</b><br/>
{casella.Titolo}<br/>
<small>{casella.Recapito}</small><br/>
GRUPPO SANTACROCE
";

            return firma;
        }

        private async Task<CasellaPosta?> GetCasellaAsync(int casellaId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            return await db.CasellePosta
                .Where(c => c.Id == casellaId)
                .Select(c => new CasellaPosta
                {
                    Id = c.Id,
                    Email = c.Email,
                    Password = c.Password,
                    NomeCompleto = c.NomeCompleto,
                    Username = c.Username,
                    Provider = c.Provider,
                    ImapHost = c.ImapHost,
                    ImapPort = c.ImapPort,
                    UseSsl = c.UseSsl,
                    Nome = c.Nome,
                    Titolo = c.Titolo,
                    Recapito = c.Recapito
                })
                .FirstOrDefaultAsync(ct);
        }
        public async Task SendEmailAsync(
            string usernameOrEmail,
            string to,
            string subject,
            string bodyHtml,
            List<OutgoingAttachment_NEW>? attachments = null,
            CancellationToken ct = default)
        {
            var casellaId = await GetCasellaIdAsync(usernameOrEmail, ct);
            var casella = await GetCasellaAsync(casellaId, ct);
            if (casella == null)
                throw new InvalidOperationException($"Casella {casellaId} non trovata");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(casella.NomeCompleto, casella.Email));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = bodyHtml };

            if (attachments != null && attachments.Count > 0)
            {
                foreach (var a in attachments)
                {
                    using var ms = new MemoryStream(a.Content);
                    builder.Attachments.Add(
                        a.FileName,
                        ms,
                        ContentType.Parse(a.MimeType ?? "application/octet-stream")
                    );
                }
            }

            message.Body = builder.ToMessageBody();

            // 🔑 SMTP dinamico
            string smtpHost;
            int smtpPort;
            SecureSocketOptions socketOptions;

            if (casella.Provider.Equals("Gmail", StringComparison.OrdinalIgnoreCase))
            {
                smtpHost = "smtp.gmail.com";
                smtpPort = 587;
                socketOptions = SecureSocketOptions.StartTls;
            }
            else if (casella.Provider.Equals("Aruba", StringComparison.OrdinalIgnoreCase))
            {
                smtpHost = "smtp.aruba.it";
                smtpPort = 587;
                socketOptions = SecureSocketOptions.StartTls;
            }
            else
            {
                throw new InvalidOperationException($"Provider {casella.Provider} non gestito");
            }

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpHost, smtpPort, socketOptions, ct);
            await client.AuthenticateAsync(casella.Email, casella.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation($"Email inviata da {casella.Email} a {to} con {attachments?.Count ?? 0} allegati");
        }


    }
}
