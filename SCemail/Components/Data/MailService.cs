using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Oracle.ManagedDataAccess.Client;
using SCemail.Components.Shared;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace SCemail.Components.Data
{
    public record EmailListItem(
     int Id,
     DateTime? Data,
     string? Mittente,
     string? Oggetto,
     string? Aperto,
     bool HasAttachments = false,
     int Replies = 0,
     string? Preview = null,
     List<AllegatoItem>? Allegati = null
 );


    public record AllegatoItem(int Id, string NomeFile, string? MimeType);

    public record EmailDraftItem(int Id, string? To, string? Subject, DateTime? LastSaved);
    public record EmailDraftDetail(int Id, string? To, string? Subject, string? Body, DateTime? LastSaved, IEnumerable<AllegatoItem> Allegati);

    public class EmailBozza
    {
        public int Id { get; set; }
        public string? Utente { get; set; }
        public string? Destinatari { get; set; }
        public string? Oggetto { get; set; }
        public string? CorpoHtml { get; set; }
        public DateTime? LastSaved { get; set; }
        public ICollection<BozzaAllegato> Allegati { get; set; } = new List<BozzaAllegato>();
    }

    public class BozzaAllegato
    {
        public int Id { get; set; }
        public int BozzaId { get; set; }
        public string NomeFile { get; set; } = "";
        public string? MimeType { get; set; }
        public byte[]? Content { get; set; }
        public EmailBozza Bozza { get; set; } = null!;
    }

    public record EmailDetail(
        int Id,
        string? Mittente,
        string? Destinatari,
        string? Oggetto,
        DateTime? Data,
        string? CorpoHtml,
        string? CorpoTesto,
        IEnumerable<AllegatoItem> Allegati,
        string? Aperto
    );

    public class MailService
    {
        private readonly HttpClient _http;
        private readonly IDbContextFactory<MailDbContext> _dbFactory;
        private readonly ILogger<MailService> _logger;
        private readonly string _connectionString;

        public MailService(
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<MailService> logger,
    HttpClient http,
    IConfiguration config)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _http = http;

            _connectionString = config.GetConnectionString("OracleDb")
                ?? throw new InvalidOperationException("Missing OracleDb connection string!");
        }


        // ================== LISTA CON ORACLE-SAFE PROJECTION ==================
        public async Task<(List<EmailListItem>, int)> GetEmailPageAsync(
    int casellaId,
    int startIndex,
    int count,
    string? search,
    bool onlyUnread,
    bool onlyWithAttachments,
    bool asc,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            // 1) Query base SOLO con campi semplici (niente bool/Any/NormalizeSubject)
            var q = db.EmailRicevute
                .AsNoTracking()
                .Where(e => e.CasellaId == casellaId && (e.Eliminato == null || e.Eliminato != "Y"));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToUpper();
                q = q.Where(e =>
                    ((e.Mittente ?? "").ToUpper().Contains(s)) ||
                    ((e.Oggetto ?? "").ToUpper().Contains(s)));
            }

            if (onlyUnread)
                q = q.Where(e => e.Aperto != "Y");

            q = asc ? q.OrderBy(e => e.DataRicezione)
                    : q.OrderByDescending(e => e.DataRicezione);

            var total = await q.CountAsync(ct);

            // 2) Page fetch: nessun bool nella SELECT
            var pageRows = await q
                .Skip(startIndex)
                .Take(count)
                .Select(e => new
                {
                    e.Id,
                    e.CasellaId,
                    e.DataRicezione,
                    e.Mittente,
                    e.Oggetto,
                    e.Aperto,
                    Preview = e.CorpoTesto ?? e.CorpoHtml,
                    Allegati = e.Allegati.Select(a => new AllegatoItem(a.Id, a.NomeFile, a.MimeType)).ToList()
                })
                .ToListAsync(ct);


            var pageIds = pageRows.Select(r => r.Id).ToList();

            // 3) HasAttachments: calcolato in memoria con una query separata (no bool in SQL)
            var attCounts = await db.EmailAllegati
                .AsNoTracking()
                .Where(a => pageIds.Contains(a.EmailId))
                .GroupBy(a => a.EmailId)
                .Select(g => new { EmailId = g.Key, Cnt = g.Count() })
                .ToListAsync(ct);
            var hasAtt = new HashSet<int>(attCounts.Select(x => x.EmailId));

            // 4) Replies: stima sulla pagina (per conteggio globale servirebbe una colonna/conversation id)
            var subjMap = pageRows
                .GroupBy(r => NormalizeSubject(r.Oggetto))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var items = pageRows.Select(r => new EmailListItem(
                r.Id,
                r.DataRicezione,
                r.Mittente,
                r.Oggetto,
                r.Aperto,
                hasAtt.Contains(r.Id),
                subjMap.TryGetValue(NormalizeSubject(r.Oggetto), out var c) ? c : 1,
                string.IsNullOrWhiteSpace(r.Preview) ? null : r.Preview.Substring(0, Math.Min(120, r.Preview.Length)),
                r.Allegati
            )).ToList();


            if (onlyWithAttachments)
                items = items.Where(i => i.HasAttachments).ToList();

            return (items, total);
        }

        public async Task<List<string>> GetProprietariEmailAsync(int emailId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            return await db.EmailAssegnazione
                .AsNoTracking()
                .Where(a => a.EmailId == emailId)
                .Select(a => a.Utente)
                .Distinct()
                .ToListAsync();
        }



        public async Task<List<string>> GetPartecipantiEmailAsync(int emailId)
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            var assegnati = db.EmailAssegnazione
                .Where(a => a.EmailId == emailId)
                .Select(a => a.Utente);

            var commentatori = db.CommentiEmail
                .Where(c => c.EmailId == emailId)
                .Select(c => c.Autore);

            var menzionati = db.emailMenzionis
                .Where(m => m.EmailId == emailId)
                .Select(m => m.Utente);

            return await assegnati
                .Union(commentatori)
                .Union(menzionati)
                .Distinct()
                .ToListAsync();
        }

        public async Task<List<string>> GetFollowersAsync(int emailId)
        {
            await using var db = _dbFactory.CreateDbContext();

            return await db.emailMenzionis
                .Where(m => m.EmailId == emailId)
                .Select(m => m.Utente)
                .ToListAsync();
        }
        public async Task RegistraEventoChatAsync(
    int emailId,
    string utente,
    bool isMention,
    int commentoId,
    string autore)
        {
            if (utente.Equals(autore, StringComparison.OrdinalIgnoreCase))
                return;

            await using var db = _dbFactory.CreateDbContext();

            var row = await db.emailMenzionis
                .FirstOrDefaultAsync(m =>
                    m.EmailId == emailId &&
                    m.Utente == utente);

            if (row == null)
            {
                row = new EmailMenzione
                {
                    EmailId = emailId,
                    Utente = utente
                };
                db.emailMenzionis.Add(row);
            }

            row.TipoEvento = isMention ? "MENTION" : "UPDATE";
            row.Visto = "N";
            row.CommentoId = commentoId;
            row.DataMenzione = DateTime.Now;

            await db.SaveChangesAsync();
        }

        public async Task SegnaChatVistaAsync(int emailId, string utente)
        {
            await using var db = _dbFactory.CreateDbContext();

            var row = await db.emailMenzionis
                .FirstOrDefaultAsync(m =>
                    m.EmailId == emailId &&
                    m.Utente == utente);

            if (row != null)
            {
                row.Visto = "Y";
                await db.SaveChangesAsync();
            }
        }

        public async Task<(byte[] data, string mime, string filename)> GetAttachmentAsync(
    int attachmentId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var a = await db.EmailAllegati
                .Include(x => x.Email)
                .ThenInclude(e => e.Casella)
                .AsNoTracking()
                .SingleAsync(x => x.Id == attachmentId, ct);

            var e = a.Email;
            var c = e.Casella;

            using var imap = new MailKit.Net.Imap.ImapClient();

            var sock = c.UseSsl == "Y"
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            await imap.ConnectAsync(c.ImapHost, c.ImapPort, sock, ct);
            imap.AuthenticationMechanisms.Remove("XOAUTH2");
            await imap.AuthenticateAsync(c.Email, c.Password, ct);

            var located = await LocateMessageAsync(
                imap,
                e.FolderPath,
                e.MessageUid,
                e.MessageId,
                e.DataRicezione,
                e.Oggetto,
                ct
            );

            if (located is null)
                throw new InvalidOperationException("Messaggio non trovato sul server IMAP.");

            var folder = located.Value.folder;
            var uid = located.Value.uid;
            await EnsureOpenAsync(folder, FolderAccess.ReadOnly, ct);

            var summaries = await folder.FetchAsync(new[] { uid }, MessageSummaryItems.BodyStructure, ct);
            var body = summaries.FirstOrDefault()?.Body;

            if (body == null)
                throw new InvalidOperationException("BodyStructure non disponibile.");

            // aggiorna Folder/UID se cambiati
            if (string.IsNullOrWhiteSpace(e.FolderPath) || e.MessageUid == null || e.MessageUid != (long)uid.Id)
            {
                await using var dbUpdate = _dbFactory.CreateDbContext();
                var toUpd = await dbUpdate.EmailRicevute.FindAsync(new object[] { e.Id }, ct);
                if (toUpd != null)
                {
                    toUpd.FolderPath = folder.FullName;
                    toUpd.MessageUid = (long)uid.Id;
                    await dbUpdate.SaveChangesAsync(ct);
                }
            }

            BodyPartBasic? part = null;
            if (!string.IsNullOrWhiteSpace(a.PartSpec))
                part = FindBodyPartBySpec(body, a.PartSpec!) as BodyPartBasic;
            if (part == null)
                part = FindBodyPartByFileName(body, a.NomeFile);

            if (part is null)
                throw new InvalidOperationException("Parte allegato non trovata.");

            var entity = await folder.GetBodyPartAsync(uid, part, ct);
            if (entity is not MimePart mp)
                throw new InvalidOperationException("Parte MIME non valida.");

            await using var ms = new MemoryStream();
            await mp.Content.DecodeToAsync(ms, ct);
            var bytes = ms.ToArray();

            await imap.DisconnectAsync(true, ct);

            var mime = a.MimeType ?? mp.ContentType?.MimeType ?? "application/octet-stream";
            var name = string.IsNullOrWhiteSpace(a.NomeFile) ? "allegato" : a.NomeFile;

            return (bytes, mime, name);
        }


        // ================== DRAFTS, SEND, DETAIL, CONVERSATION ==================

        public async Task<(List<EmailDraftItem>, int)> GetDraftsPageAsync(int startIndex, int count, string? search, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var q = db.EmailBozze.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToUpperInvariant();
                q = q.Where(b => ((b.Destinatari ?? "").ToUpper().Contains(s)) || ((b.Oggetto ?? "").ToUpper().Contains(s)));
            }

            var total = await q.CountAsync(ct);

            var items = await q.OrderByDescending(b => b.LastSaved)
                .Skip(startIndex).Take(count)
                .Select(b => new EmailDraftItem(b.Id, b.Destinatari, b.Oggetto, b.LastSaved))
                .ToListAsync(ct);

            return (items, total);
        }

        public async Task<EmailDraftDetail> GetDraftDetailAsync(int bozzaId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var b = await db.EmailBozze.AsNoTracking().FirstAsync(x => x.Id == bozzaId, ct);

            return new EmailDraftDetail(
                b.Id, b.Destinatari, b.Oggetto, b.CorpoHtml, b.LastSaved, new List<AllegatoItem>()
            );
        }

        public async Task<List<EmailDraftItem>> GetDraftsAsync(string utente)
        {
            await using var db = _dbFactory.CreateDbContext();

            return await db.EmailBozze
                .Where(b => b.Utente == utente)
                .OrderByDescending(b => b.LastSaved)
                .Select(b => new EmailDraftItem(b.Id, b.Destinatari, b.Oggetto, b.LastSaved))
                .ToListAsync();
        }

        public async Task SaveDraftAsync(string? to, string? subject, string? bodyHtml, int? bozzaId = null, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            EmailBozza b;
            if (bozzaId.HasValue)
            {
                b = await db.EmailBozze.FirstAsync(x => x.Id == bozzaId.Value, ct);
                b.Destinatari = to;
                b.Oggetto = subject;
                b.CorpoHtml = bodyHtml;
                b.LastSaved = DateTime.UtcNow;
            }
            else
            {
                b = new EmailBozza { Destinatari = to, Oggetto = subject, CorpoHtml = bodyHtml, LastSaved = DateTime.UtcNow };
                db.EmailBozze.Add(b);
            }
            await db.SaveChangesAsync(ct);
        }

        public async Task DeleteDraftAsync(int bozzaId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var b = await db.EmailBozze.FindAsync(new object[] { bozzaId }, ct);
            if (b != null)
            {
                db.EmailBozze.Remove(b);
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task SendEmailAsync(
    string usernameOrEmail,
    string to,
    string subject,
    string htmlBody,
    IEnumerable<OutgoingAttachment>? attachments = null,
    CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            // 1️⃣ Trova la casella a partire da username o indirizzo email
            CasellaPosta? casella = null;
            CasellaAbilitazione? abilitazione = null;

            // Se è una mail diretta (contiene "@"), cerca per EMAIL
            if (usernameOrEmail.Contains("@"))
            {
                casella = await db.CasellePosta
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Email.ToUpper() == usernameOrEmail.ToUpper(), ct);
            }
            else
            {
                // Altrimenti cerca l’utente nella tabella delle abilitazioni
                abilitazione = await db.CasellaAbilitazioni
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Username.ToUpper() == usernameOrEmail.ToUpper(), ct);

                if (abilitazione != null)
                {
                    casella = await db.CasellePosta
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == abilitazione.CasellaId, ct);
                }
            }

            if (casella is null)
                throw new InvalidOperationException($"Nessuna casella associata a '{usernameOrEmail}'.");

            // 2️⃣ Nome e firma: presi dalla tabella CASELLA_ABILITAZIONI se disponibile
            var nomeMittente = abilitazione?.Nome ?? casella.Email;
            var titoloMittente = abilitazione?.Titolo ?? "";
            var recapitoMittente = abilitazione?.Recapito ?? "";

            // 3️⃣ Costruisci il messaggio
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(nomeMittente, casella.Email));
            message.To.AddRange(InternetAddressList.Parse(to));
            message.Subject = subject ?? "(nessun oggetto)";

            // 4️⃣ Firma HTML
            var firmaHtml = $@"
<br><br>
<div style=""font-family:'Courier New', Courier, monospace; font-size:13px;"">
Cordiali saluti.<br/>
<b>{nomeMittente}</b><br/>
{(string.IsNullOrWhiteSpace(titoloMittente) ? "" : titoloMittente + "<br/>")}
<small>{recapitoMittente}</small><br/>
<span style='color:#0044cc; font-style:italic; font-weight:bold;'>
{GetSocietaByEmail(casella.Email)}
</span>
</div>";

            var builder = new BodyBuilder
            {
                HtmlBody = (htmlBody ?? "") + firmaHtml
            };

            if (attachments != null)
            {
                foreach (var a in attachments)
                {
                    var mime = a.MimeType ?? "application/octet-stream";
                    builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(mime));
                }
            }

            message.Body = builder.ToMessageBody();

            // 5️⃣ SMTP dinamico
            using var smtp = new MailKit.Net.Smtp.SmtpClient();
            SecureSocketOptions socketOptions;
            string smtpHost;
            int smtpPort;

            if (casella.Provider.Equals("Gmail", StringComparison.OrdinalIgnoreCase))
            {
                smtpHost = "smtp.gmail.com";
                smtpPort = 587;
                socketOptions = SecureSocketOptions.StartTls;
            }
            else if (casella.Provider.Equals("Aruba", StringComparison.OrdinalIgnoreCase))
            {
                smtpHost = "smtps.aruba.it";
                smtpPort = 465;
                socketOptions = SecureSocketOptions.SslOnConnect;
            }
            else
            {
                throw new InvalidOperationException($"Provider {casella.Provider} non gestito.");
            }

            await smtp.ConnectAsync(smtpHost, smtpPort, socketOptions, ct);
            await smtp.AuthenticateAsync(casella.Email, casella.Password, ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);

            _logger.LogInformation($"✉️ Email inviata da {casella.Email} ({nomeMittente}) a {to}");
        }

        private static string GetSocietaByEmail(string email)
        {
            if (email.EndsWith("@grupposantacroce.com", StringComparison.OrdinalIgnoreCase))
                return "GRUPPO SANTACROCE";
            else if (email.EndsWith("@rayaitaly.com", StringComparison.OrdinalIgnoreCase))
                return "RAYA S.p.A. - Grains Commodities & Investment";
            else if (email.EndsWith("@eurocereali.com", StringComparison.OrdinalIgnoreCase))
                return "EUROCEREALI S.R.L. - Grains Commodities & Investment";
            else
                return "GRUPPO SANTACROCE";
        }
        public async Task<EmailDetail?> GetEmailDetailAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var e = await db.EmailRicevute.AsNoTracking()
                .Include(x => x.Allegati)
                .FirstOrDefaultAsync(x => x.Id == emailId, ct);

            if (e is null) return null;

            return new EmailDetail(
                e.Id, e.Mittente, e.Destinatari, e.Oggetto, e.DataRicezione,
                e.CorpoHtml, e.CorpoTesto,
                e.Allegati.Select(a => new AllegatoItem(a.Id, a.NomeFile, a.MimeType)),
                e.Aperto
            );
        }

        public async Task<List<EmailDetail>> GetConversationAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var me = await db.EmailRicevute
                .Include(x => x.Allegati)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == emailId, ct);

            if (me is null) return new List<EmailDetail>();

            var subjNorm = NormalizeSubject(me.Oggetto);

            var list = await db.EmailRicevute
                .Include(x => x.Allegati)
                .AsNoTracking()
                .Where(x => x.CasellaId == me.CasellaId && (x.Eliminato == null || x.Eliminato != "Y"))
                .OrderBy(x => x.DataRicezione)
                .Take(150)
                .ToListAsync(ct);

            var conv = list
                .Where(x => NormalizeSubject(x.Oggetto) == subjNorm)
                .Select(e => new EmailDetail(
                    e.Id, e.Mittente, e.Destinatari, e.Oggetto, e.DataRicezione,
                    e.CorpoHtml, e.CorpoTesto,
                    e.Allegati.Select(al => new AllegatoItem(al.Id, al.NomeFile, al.MimeType)),
                    e.Aperto
                ))
                .ToList();

            if (conv.Count == 0)
            {
                conv.Add(new EmailDetail(
                    me.Id, me.Mittente, me.Destinatari, me.Oggetto, me.DataRicezione,
                    me.CorpoHtml, me.CorpoTesto,
                    me.Allegati.Select(al => new AllegatoItem(al.Id, al.NomeFile, al.MimeType)),
                    me.Aperto
                ));
            }

            return conv;
        }

        public async Task<int> GetCasellaIdAsync(string usernameOrEmail, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(usernameOrEmail))
                throw new ArgumentException("Username o email mancante.", nameof(usernameOrEmail));

            var key = usernameOrEmail.Trim().ToUpperInvariant();

            await using var db = _dbFactory.CreateDbContext();

            // 1️⃣ Se è un indirizzo email (contiene "@"), cerca direttamente nella tabella CASELLEPOSTA
            if (key.Contains("@"))
            {
                var id = await db.CasellePosta
                    .AsNoTracking()
                    .Where(c => (c.Email ?? "").Trim().ToUpper() == key)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync(ct);

                if (id != 0)
                    return id;
            }

            // 2️⃣ Se è un nome utente, cerca nella tabella CASELLA_ABILITAZIONI
            var idAbilitazione = await db.CasellaAbilitazioni
                .AsNoTracking()
                .Where(a => (a.Username ?? "").Trim().ToUpper() == key)
                .Select(a => a.CasellaId)
                .FirstOrDefaultAsync(ct);

            if (idAbilitazione != 0)
                return idAbilitazione;

            // 3️⃣ Nessuna corrispondenza trovata
            throw new InvalidOperationException($"Nessuna casella associata a '{usernameOrEmail}'.");
        }



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

        public async Task MarkDeletedAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null && e.Eliminato != "Y")
            {
                e.Eliminato = "Y";
                await db.SaveChangesAsync(ct);
            }
        }

        public async Task<List<int>> GetTaggedEmailIdsAsync(IEnumerable<int> emailIds, string utente, CancellationToken ct = default)
        {
            var ids = (emailIds ?? Enumerable.Empty<int>()).Distinct().ToList();
            if (ids.Count == 0 || string.IsNullOrWhiteSpace(utente))
                return new List<int>();

            var csv = string.Join(",", ids);
            try
            {
                var url = $"api/email-tasks/mentions?ids={Uri.EscapeDataString(csv)}&user={Uri.EscapeDataString(utente)}";
                var res = await _http.GetFromJsonAsync<List<int>>(url, ct);
                return res ?? new List<int>();
            }
            catch
            {
                return new List<int>();
            }
        }

        // ================== IMAP helper (come li avevi) ==================

        // ... (tutti i tuoi metodi IMAP/LocateMessageAsync/FindBodyPart, invariati) ...

        private static string NormalizeFolder(string s)
        {
            var t = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            t = Regex.Replace(t, @"\s+", " ");
            return t.ToUpperInvariant();
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

        // --- MIME bodypart: trova per PartSpecifier (es. "2.1") ---
        private BodyPart? FindBodyPartBySpec(BodyPart? part, string? spec)
        {
            if (part is null || string.IsNullOrWhiteSpace(spec))
                return null;

            if (string.Equals(part.PartSpecifier, spec, StringComparison.OrdinalIgnoreCase))
                return part;

            if (part is BodyPartMultipart multi)
            {
                foreach (var child in multi.BodyParts)
                {
                    var found = FindBodyPartBySpec(child, spec);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // --- MIME bodypart: trova un allegato per nome file ---
        private BodyPartBasic? FindBodyPartByFileName(BodyPart? part, string fileName)
        {
            if (part is null) return null;

            if (part is BodyPartBasic basic)
            {
                var name = basic.FileName ?? "";
                var isAttachment = !string.IsNullOrWhiteSpace(name)
                                   || string.Equals(basic.ContentDisposition?.Disposition, "attachment", StringComparison.OrdinalIgnoreCase);

                if (isAttachment && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return basic;
            }

            if (part is BodyPartMultipart mp)
            {
                foreach (var child in mp.BodyParts)
                {
                    var found = FindBodyPartByFileName(child, fileName);
                    if (found != null) return found;
                }
            }

            return null;
        }

        // --- Assicura apertura cartella con un certo accesso ---
        private static async Task EnsureOpenAsync(IMailFolder folder, FolderAccess access, CancellationToken ct)
        {
            if (!folder.IsOpen || folder.Access != access)
                await folder.OpenAsync(access, ct);
        }

        // --- Trova in modo robusto la IMAP message (folder+uid) ---
        private async Task<(IMailFolder folder, UniqueId uid)?> LocateMessageAsync(
            ImapClient imap,
            string? folderPath,
            long? messageUid,
            string? messageId,
            DateTime? internalDateUtc,
            string? subject,
            CancellationToken ct)
        {
            // 0) Tentativo diretto con path + uid
            if (!string.IsNullOrWhiteSpace(folderPath) && messageUid.HasValue && messageUid.Value > 0)
            {
                var f = await SafeGetFolderIfExistsAsync(imap, folderPath, ct);
                if (f != null)
                {
                    try
                    {
                        await EnsureOpenAsync(f, FolderAccess.ReadOnly, ct);
                        await f.GetMessageAsync(new UniqueId((uint)messageUid.Value), ct); // se non esiste, eccezione
                        return (f, new UniqueId((uint)messageUid.Value));
                    }
                    catch { /* ignore e prosegui */ }
                    finally { if (f.IsOpen) await f.CloseAsync(false, ct); }
                }
            }

            // 1) Costruisci lista cartelle candidate: All Mail (se c’è) + Inbox + tutte
            var candidates = new List<IMailFolder>();
            try
            {
                var all = imap.GetFolder(SpecialFolder.All);
                if (all != null) candidates.Add(all);
            }
            catch { /* alcuni server non lo supportano */ }

            candidates.Add(imap.Inbox);

            var allFolders = await GetAllFoldersAsync(imap, ct);
            foreach (var f in allFolders)
                if (!candidates.Any(x => x.FullName == f.FullName))
                    candidates.Add(f);

            // 2) Ricerca per Message-Id
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                var variants = new List<string> { messageId, messageId.Trim('<', '>', ' ') };
                foreach (var f in candidates)
                {
                    try
                    {
                        await EnsureOpenAsync(f, FolderAccess.ReadOnly, ct);
                        foreach (var v in variants.Distinct())
                        {
                            var ids = await f.SearchAsync(SearchQuery.HeaderContains("Message-Id", v), ct);
                            if (ids?.Count > 0)
                                return (f, ids[0]);
                        }
                    }
                    catch { }
                    finally { if (f.IsOpen) await f.CloseAsync(false, ct); }
                }
            }

            // 3) Fallback: data ±3 giorni + subject (se disponibile)
            if (internalDateUtc.HasValue)
            {
                var from = internalDateUtc.Value.AddDays(-3);
                var to = internalDateUtc.Value.AddDays(+3);
                var subj = (subject ?? string.Empty).Trim();
                var hasSubj = subj.Length >= 3;

                foreach (var f in candidates)
                {
                    try
                    {
                        await EnsureOpenAsync(f, FolderAccess.ReadOnly, ct);

                        var q = SearchQuery.DeliveredAfter(from)
                                           .And(SearchQuery.DeliveredBefore(to));

                        if (hasSubj)
                            q = q.And(SearchQuery.SubjectContains(subj));

                        var ids = await f.SearchAsync(q, ct);
                        if (ids?.Count > 0)
                            return (f, ids[0]);
                    }
                    catch { }
                    finally { if (f.IsOpen) await f.CloseAsync(false, ct); }
                }
            }

            return null;
        }

        // --- Prende cartella se esiste (anche con nomi "strani") ---
        private static async Task<IMailFolder?> SafeGetFolderIfExistsAsync(ImapClient client, string path, CancellationToken ct)
        {
            try
            {
                if (string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase))
                    return client.Inbox;

                return await client.GetFolderAsync(path, ct);
            }
            catch
            {
                // ricerca "morbida"
                var wanted = NormalizeFolder(path);
                var all = await GetAllFoldersAsync(client, ct);
                return all.FirstOrDefault(f =>
                    NormalizeFolder(f.FullName) == wanted ||
                    NormalizeFolder(f.Name) == wanted);
            }
        }

        // --- Enumera tutte le cartelle accessibili ---
        private static async Task<List<IMailFolder>> GetAllFoldersAsync(ImapClient client, CancellationToken ct)
        {
            var list = new List<IMailFolder> { client.Inbox };

            try
            {
                var all = client.GetFolder(SpecialFolder.All);
                if (all != null) list.Add(all);
            }
            catch { }

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

            var ban = FolderAttributes.Trash | FolderAttributes.Junk | FolderAttributes.NonExistent;
            if ((root.Attributes & ban) == 0 && !acc.Any(x => x.FullName == root.FullName))
                acc.Add(root);

            try
            {
                foreach (var sub in await root.GetSubfoldersAsync(false, ct))
                    await GatherFoldersRec(sub, acc, ct);
            }
            catch { }
        }



        public async Task MoveToTrashAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var e = await db.EmailRicevute.FirstOrDefaultAsync(x => x.Id == emailId, ct);
            if (e != null)
            {
                e.FolderPath = "[GMAIL]/CESTINO"; // o Trash
                await db.SaveChangesAsync(ct);
            }
        }
        // --- Aggiungi questo tipo dentro MailService ---
        public record FolderItem(string UiName, string Label, string IconClass, int SortOrder, int Count);

        // --- Aggiorna/estendi la mappa cartelle -> UI name (più robusta, localizzata) ---
        public static string MapFolderToUi(string dbFolder)
        {
            var norm = NormalizeFolder(dbFolder);

            // INBOX
            if (norm == "INBOX" || norm.StartsWith("INBOX")) return "inbox";

            // SENT
            if (norm.Contains("POSTA INVIATA") || norm.Contains("/SENT") || norm == "SENT" || norm == "INBOX.SENT")
                return "sent";

            // DRAFTS
            if (norm.Contains("BOZZE") || norm.Contains("/DRAFTS") || norm == "DRAFTS")
                return "drafts";

            // SPAM / JUNK
            if (norm.Contains("SPAM") || norm.Contains("JUNK"))
                return "spam";

            // TRASH
            if (norm.Contains("CESTINO") || norm.Contains("/TRASH") || norm == "TRASH")
                return "trash";

            // ALL MAIL
            if (norm.Contains("TUTTI I MESSAGGI") || norm.Contains("ALL MAIL") || norm.Contains("/ALL"))
                return "all";

            return "custom:" + dbFolder;
        }

        // --- Nuova versione: elenco cartelle con icone + count, già raggruppate per UiName ---
        public async Task<List<FolderItem>> GetFoldersAsync(int casellaId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            // Conta messaggi per FolderPath
            var rawCounts = await db.EmailRicevute
                .AsNoTracking()
                .Where(e => e.CasellaId == casellaId)
                .GroupBy(e => e.FolderPath)
                .Select(g => new { Folder = g.Key, Cnt = g.Count() })
                .ToListAsync(ct);

            // Aggrega per UiName
            var perUi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rawCounts)
            {
                if (string.IsNullOrWhiteSpace(r.Folder)) continue;
                var ui = MapFolderToUi(r.Folder!);
                if (!perUi.ContainsKey(ui)) perUi[ui] = 0;
                perUi[ui] += r.Cnt;
            }

            // Definizione voci "importanti"
            var important = new[]
            {
        new FolderItem("inbox",  "Posta in arrivo", "bi-inbox-fill",           0, perUi.TryGetValue("inbox", out var c0) ? c0 : 0),
        new FolderItem("all",    "Tutti",           "bi-collection-fill",      1, perUi.Values.Sum()), // tutti (grezza: somma)
        new FolderItem("sent",   "Inviati",         "bi-send-fill",            2, perUi.TryGetValue("sent", out var c1) ? c1 : 0),
        new FolderItem("drafts", "Bozze",           "bi-file-earmark-text",    3, perUi.TryGetValue("drafts", out var c2) ? c2 : 0), // (solo Ricevute; le bozze vere sono altrove)
        new FolderItem("spam",   "Spam",            "bi-exclamation-octagon",  4, perUi.TryGetValue("spam", out var c3) ? c3 : 0),
        new FolderItem("trash",  "Cestino",         "bi-trash3-fill",          5, perUi.TryGetValue("trash", out var c4) ? c4 : 0),
    }.ToList();

            // Eventuali cartelle custom presenti a DB
            var customs = rawCounts
                .Select(r => r.Folder)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => new { Raw = f!, Ui = MapFolderToUi(f!) })
                .Where(x => x.Ui.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Ui)
                .Select(g => g.First())
                .Select(x =>
                {
                    var ui = x.Ui; var label = x.Raw; // usa nome originale
                    var cnt = rawCounts.Where(r => MapFolderToUi(r.Folder!) == ui).Sum(r => r.Cnt);
                    return new FolderItem(ui, label, "bi-folder-fill", 10, cnt);
                });

            var all = important.Concat(customs)
                .GroupBy(f => f.UiName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Label)
                .ToList();

            // Rendi "Tutti" più sensato: escluso trash/spam se vuoi
            var withoutTrashSpam = perUi.Where(kv => kv.Key != "trash" && kv.Key != "spam").Sum(kv => kv.Value);
            var idxAll = all.FindIndex(f => f.UiName == "all");
            if (idxAll >= 0) all[idxAll] = all[idxAll] with { Count = withoutTrashSpam };

            return all;
        }

        // --- Nuova firma: filtro per cartella UI (folderUi) e miglioramenti preview/allegati ---
        // Sostituisci la tua GetEmailPageAsync con questa versione (la logica "core" rimane la tua)
        public async Task<(List<EmailListItem>, int)> GetEmailPageAsync(
            int casellaId,
            int startIndex,
            int count,
            string? search,
            bool onlyUnread,
            bool onlyWithAttachments,
            bool asc,
            string? folderUi,                   // ⬅️ nuovo
            CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var q = db.EmailRicevute
                .AsNoTracking()
                .Where(e => e.CasellaId == casellaId);

            // Filtro cartella (default: escludo Cestino/Spam se folderUi non specificato o "all")
            if (string.IsNullOrWhiteSpace(folderUi) || folderUi == "all")
            {
                q = q.Where(e =>
                    e.FolderPath == null ||
                    (!EF.Functions.Like(e.FolderPath, "%CESTINO%") && !EF.Functions.Like(e.FolderPath, "%Trash%") &&
                     !EF.Functions.Like(e.FolderPath, "%SPAM%") && !EF.Functions.Like(e.FolderPath, "%Junk%")));
            }
            else
            {
                switch (folderUi)
                {
                    case "inbox":
                        q = q.Where(e => e.FolderPath != null &&
                                         (EF.Functions.Like(e.FolderPath, "INBOX%") || EF.Functions.Like(e.FolderPath, "%Inbox%")));
                        break;

                    case "sent":
                        q = q.Where(e => e.FolderPath != null &&
                                         (EF.Functions.Like(e.FolderPath, "%POSTA INVIATA%") ||
                                          EF.Functions.Like(e.FolderPath, "%Sent%")));
                        break;

                    case "trash":
                        q = q.Where(e => e.FolderPath != null &&
                                         (EF.Functions.Like(e.FolderPath, "%CESTINO%") || EF.Functions.Like(e.FolderPath, "%Trash%")));
                        break;

                    case "spam":
                        q = q.Where(e => e.FolderPath != null &&
                                         (EF.Functions.Like(e.FolderPath, "%SPAM%") || EF.Functions.Like(e.FolderPath, "%Junk%")));
                        break;

                    case "drafts":
                        // Le bozze stanno su tabella EmailBozze (non EmailRicevute): qui mostriamo 0, la vista potrebbe dedicare un tab separato
                        return (new List<EmailListItem>(), 0);

                    default:
                        // custom:<raw>
                        var raw = folderUi.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) ? folderUi.Substring(7) : folderUi;
                        q = q.Where(e => e.FolderPath == raw);
                        break;
                }
            }

            // Escludi eliminati "soft" (rimane OK anche per trash)
            q = q.Where(e => e.Eliminato == null || e.Eliminato != "Y");

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToUpper();
                q = q.Where(e =>
                    ((e.Mittente ?? "").ToUpper().Contains(s)) ||
                    ((e.Oggetto ?? "").ToUpper().Contains(s)));
            }

            if (onlyUnread)
                q = q.Where(e => e.Aperto != "Y");

            q = asc ? q.OrderBy(e => e.DataRicezione)
                    : q.OrderByDescending(e => e.DataRicezione);

            var total = await q.CountAsync(ct);

            var pageRows = await q
                .Skip(startIndex)
                .Take(count)
                .Select(e => new
                {
                    e.Id,
                    e.CasellaId,
                    e.DataRicezione,
                    e.Mittente,
                    e.Oggetto,
                    e.Aperto,
                    Preview = e.CorpoTesto ?? e.CorpoHtml,
                    Allegati = e.Allegati.Select(a => new AllegatoItem(a.Id, a.NomeFile, a.MimeType)).ToList()
                })
                .ToListAsync(ct);

            var pageIds = pageRows.Select(r => r.Id).ToList();

            var attCounts = await db.EmailAllegati
                .AsNoTracking()
                .Where(a => pageIds.Contains(a.EmailId))
                .GroupBy(a => a.EmailId)
                .Select(g => new { EmailId = g.Key, Cnt = g.Count() })
                .ToListAsync(ct);

            var hasAtt = new HashSet<int>(attCounts.Select(x => x.EmailId));

            var subjMap = pageRows
                .GroupBy(r => NormalizeSubject(r.Oggetto))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var items = pageRows.Select(r => new EmailListItem(
                r.Id,
                r.DataRicezione,
                r.Mittente,
                r.Oggetto,
                r.Aperto,
                hasAtt.Contains(r.Id),
                subjMap.TryGetValue(NormalizeSubject(r.Oggetto), out var c) ? c : 1,
                string.IsNullOrWhiteSpace(r.Preview) ? null : (r.Preview.Length > 180 ? r.Preview.Substring(0, 180) + "…" : r.Preview),
                r.Allegati
            )).ToList();

            if (onlyWithAttachments)
                items = items.Where(i => i.HasAttachments).ToList();

            return (items, total);
        }

        public record OutgoingAttachment(string FileName, string? MimeType, byte[] Content);

        public async Task<EmailDetail?> GetEmailDetailAsync(int emailId, bool onlyMeta, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var q = db.EmailRicevute.AsNoTracking().Where(e => e.Id == emailId);

            if (onlyMeta)
            {
                return await q.Select(e => new EmailDetail(
                    e.Id, e.Mittente, e.Destinatari, e.Oggetto, e.DataRicezione,
                    null, null, Enumerable.Empty<AllegatoItem>(), e.Aperto ?? "N"
                )).FirstOrDefaultAsync(ct);
            }

            return await q.Select(e => new EmailDetail(
                e.Id, e.Mittente, e.Destinatari, e.Oggetto, e.DataRicezione,
                e.CorpoHtml, e.CorpoTesto,
                e.Allegati.Select(a => new AllegatoItem(a.Id, a.NomeFile, a.MimeType)),
                e.Aperto ?? "N"
            )).FirstOrDefaultAsync(ct);
        }

        // Legge commenti
        public async Task<List<CommentoEmail>> GetCommentsByEmailAsync(int emailId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            return await db.CommentiEmail
                .AsNoTracking()
                .Where(c => c.EmailId == emailId)
                .OrderBy(c => c.DataCreazione)
                .ToListAsync(ct);
        }

        // Inserisce commento (+ opzionale allegato)
        public async Task<int> AddCommentAsync(
            int emailId,
            string autore,
            string testo,
            byte[]? fileBytes = null,
            string? fileName = null,
            CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();

            var c = new CommentoEmail
            {
                EmailId = emailId,
                Autore = autore,
                Testo = testo,
                DataCreazione = DateTime.Now,
                Allegato = fileBytes,
                AllegatoNome = fileName
            };

            db.CommentiEmail.Add(c);
            await db.SaveChangesAsync(ct);

            return c.Id; // ⬅️ IMPORTANTISSIMO: ritorna l’ID del commento
        }


        // Ritorna l'allegato di un commento
        public async Task<(byte[] data, string filename)?> GetCommentAttachmentAsync(int commentId, CancellationToken ct = default)
        {
            await using var db = _dbFactory.CreateDbContext();
            var c = await db.CommentiEmail
                .AsNoTracking()
                .Where(x => x.Id == commentId)
                .Select(x => new { x.Allegato, x.AllegatoNome })
                .FirstOrDefaultAsync(ct);

            if (c?.Allegato == null) return null;
            return (c.Allegato, string.IsNullOrWhiteSpace(c.AllegatoNome) ? "allegato.dat" : c.AllegatoNome);
        }



    }
}
