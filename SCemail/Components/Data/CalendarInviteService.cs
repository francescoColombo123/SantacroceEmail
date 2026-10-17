using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;

namespace SCemail.Components.Data;

// Invito a riunione letto da un allegato .ics (text/calendar, RFC 5545 / iMIP RFC 6047).
public sealed class CalendarInvite
{
    public string Method { get; init; } = "";
    public string Uid { get; init; } = "";
    public int Sequence { get; init; }
    public string Summary { get; init; } = "";
    public string? Location { get; init; }
    public string? Description { get; init; }
    public DateTime? StartLocal { get; init; }
    public DateTime? EndLocal { get; init; }
    public bool AllDay { get; init; }
    public string? OrganizerEmail { get; init; }
    public string? OrganizerName { get; init; }
    public string? Status { get; init; }
    public List<CalendarAttendee> Attendees { get; init; } = new();

    // Righe originali (già "unfolded") usate per costruire la REPLY.
    internal List<string> RawEventLines { get; init; } = new();
    internal List<string> RawTimezoneBlocks { get; init; } = new();

    public bool IsCancelled =>
        Method.Equals("CANCEL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

    // Si può rispondere solo a una richiesta (METHOD:REQUEST) con organizzatore.
    public bool CanReply =>
        !IsCancelled
        && !string.IsNullOrWhiteSpace(OrganizerEmail)
        && !string.IsNullOrWhiteSpace(Uid)
        && (Method.Length == 0 || Method.Equals("REQUEST", StringComparison.OrdinalIgnoreCase));
}

public sealed record CalendarAttendee(string Email, string? Name, string? PartStat);

public enum CalendarReplyKind
{
    Accepted,
    Tentative,
    Declined
}

public sealed class CalendarInviteService
{
    private static readonly TimeZoneInfo RomeTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Rome");

    private readonly IEmailAttachmentsRepository _attRepo;
    private readonly EmailFetchService _fetch;
    private readonly AttachmentsOptions _opt;
    private readonly MailService_NEW _mail;
    private readonly ILogger<CalendarInviteService> _logger;

    public CalendarInviteService(
        IEmailAttachmentsRepository attRepo,
        EmailFetchService fetch,
        IOptions<AttachmentsOptions> opt,
        MailService_NEW mail,
        ILogger<CalendarInviteService> logger)
    {
        _attRepo = attRepo;
        _mail = mail;
        _fetch = fetch;
        _opt = opt.Value;
        _logger = logger;
    }

    public static bool IsCalendarAttachment(string? mimeType, string? fileName)
    {
        var mime = (mimeType ?? "").Trim().ToLowerInvariant();

        return mime.StartsWith("text/calendar")
            || mime.StartsWith("application/ics")
            || (fileName ?? "").Trim().EndsWith(".ics", StringComparison.OrdinalIgnoreCase);
    }

    // Carica l'allegato da disco (scaricandolo da IMAP se manca) e lo interpreta.
    public async Task<CalendarInvite?> LoadInviteAsync(
        int attachmentId,
        string? src,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opt.BasePath))
                return null;

            var basePath = Path.GetFullPath(_opt.BasePath);

            string? ResolvePath(AttachmentMeta? m)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.FilePath))
                    return null;

                var p = Path.GetFullPath(Path.Combine(basePath, m.FilePath));

                return p.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? p : null;
            }

            var fullPath = ResolvePath(await _attRepo.GetMetaAsync(attachmentId, src, ct));

            // Percorso vuoto o file assente su disco: lo riscarico da IMAP (come AttachmentsController).
            if (fullPath == null)
            {
                if (!await _fetch.EnsureSingleAttachmentAsync(attachmentId, ct))
                    return null;

                fullPath = ResolvePath(await _attRepo.GetMetaAsync(attachmentId, src, ct));

                if (fullPath == null)
                    return null;
            }

            var text = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);

            return Parse(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lettura invito .ics fallita AllegatoId={Id}", attachmentId);
            return null;
        }
    }

    // Invia all'organizzatore la risposta all'invito, nello stesso thread della mail ricevuta.
    // attendeeEmail = casella che ha ricevuto l'invito (deve essere una casella configurata).
    public async Task SendReplyAsync(
        CalendarInvite invite,
        CalendarReplyKind kind,
        string utente,
        string attendeeEmail,
        string? attendeeName,
        string? inReplyTo,
        string? references,
        string? threadKey,
        CancellationToken ct = default)
    {
        if (!invite.CanReply)
            throw new InvalidOperationException("Questo invito non prevede una risposta.");

        // Se l'invito elenca la casella tra i partecipanti, uso l'indirizzo esatto dell'ATTENDEE.
        var attendee = invite.Attendees.FirstOrDefault(a =>
            a.Email.Equals(attendeeEmail, StringComparison.OrdinalIgnoreCase));

        var ics = BuildReply(invite, attendee?.Email ?? attendeeEmail, attendeeName ?? attendee?.Name, kind);

        var who = System.Net.WebUtility.HtmlEncode(attendeeName ?? attendeeEmail);
        var what = kind switch
        {
            CalendarReplyKind.Accepted => "ha accettato",
            CalendarReplyKind.Tentative => "ha risposto forse a",
            _ => "ha rifiutato"
        };

        var body =
            $"<p>{who} {what} l'invito: <b>{System.Net.WebUtility.HtmlEncode(invite.Summary)}</b>" +
            (invite.StartLocal.HasValue ? $" ({invite.StartLocal:dd/MM/yyyy HH:mm})" : "") +
            ".</p>";

        await _mail.SendEmailAsync(
            usernameOrEmail: utente,
            to: invite.OrganizerEmail!,
            subject: $"{ReplySubjectPrefix(kind)}: {invite.Summary}",
            bodyHtml: body,
            fromAddressOverride: attendeeEmail,
            inReplyTo: inReplyTo,
            referencesHdr: references,
            threadKey: threadKey,
            ct: ct,
            calendarReplyIcs: ics);

        _logger.LogInformation(
            "Risposta invito inviata UID={Uid} Esito={Kind} Da={From} A={To}",
            invite.Uid, kind, attendeeEmail, invite.OrganizerEmail);
    }

    public static CalendarInvite? Parse(string? ics)
    {
        if (string.IsNullOrWhiteSpace(ics)
            || !ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            return null;

        var lines = Unfold(ics);

        var method = "";
        var timezoneBlocks = new List<string>();
        var eventLines = new List<string>();
        var inEvent = false;
        var eventDone = false;
        var nestedDepth = 0;
        StringBuilder? tzBlock = null;

        foreach (var line in lines)
        {
            var upper = line.ToUpperInvariant();

            if (tzBlock != null)
            {
                tzBlock.Append(line).Append("\r\n");

                if (upper == "END:VTIMEZONE")
                {
                    timezoneBlocks.Add(tzBlock.ToString());
                    tzBlock = null;
                }

                continue;
            }

            if (upper == "BEGIN:VTIMEZONE")
            {
                tzBlock = new StringBuilder().Append(line).Append("\r\n");
                continue;
            }

            if (!inEvent && upper.StartsWith("METHOD:"))
            {
                method = line[7..].Trim();
                continue;
            }

            if (upper == "BEGIN:VEVENT" && !eventDone)
            {
                inEvent = true;
                continue;
            }

            if (!inEvent)
                continue;

            if (upper.StartsWith("BEGIN:"))
            {
                // es. VALARM: non interessa
                nestedDepth++;
                continue;
            }

            if (upper.StartsWith("END:"))
            {
                if (nestedDepth > 0)
                {
                    nestedDepth--;
                    continue;
                }

                if (upper == "END:VEVENT")
                {
                    inEvent = false;
                    eventDone = true;
                }

                continue;
            }

            if (nestedDepth == 0)
                eventLines.Add(line);
        }

        if (eventLines.Count == 0)
            return null;

        var props = eventLines.Select(ParseProperty).ToList();

        string? Value(string name) =>
            props.FirstOrDefault(p => p.Name == name)?.Value;

        var dtStart = props.FirstOrDefault(p => p.Name == "DTSTART");
        var dtEnd = props.FirstOrDefault(p => p.Name == "DTEND");
        var organizer = props.FirstOrDefault(p => p.Name == "ORGANIZER");

        var start = ParseDate(dtStart, out var allDay);
        var end = ParseDate(dtEnd, out _);

        if (end == null && start != null)
        {
            var duration = Value("DURATION");
            end = TryParseDuration(duration, out var d)
                ? start.Value + d
                : allDay ? start.Value.AddDays(1) : start;
        }

        return new CalendarInvite
        {
            Method = method,
            Uid = Value("UID") ?? "",
            Sequence = int.TryParse(Value("SEQUENCE"), out var seq) ? seq : 0,
            Summary = Unescape(Value("SUMMARY")) ?? "(senza titolo)",
            Location = Unescape(Value("LOCATION")),
            Description = Unescape(Value("DESCRIPTION")),
            Status = Value("STATUS"),
            StartLocal = start,
            EndLocal = end,
            AllDay = allDay,
            OrganizerEmail = MailtoAddress(organizer?.Value),
            OrganizerName = Unquote(organizer?.Param("CN")),
            Attendees = props
                .Where(p => p.Name == "ATTENDEE")
                .Select(p => new CalendarAttendee(
                    MailtoAddress(p.Value) ?? "",
                    Unquote(p.Param("CN")),
                    p.Param("PARTSTAT")))
                .Where(a => a.Email.Length > 0)
                .ToList(),
            RawEventLines = eventLines,
            RawTimezoneBlocks = timezoneBlocks
        };
    }

    // Costruisce l'iCalendar METHOD:REPLY da inviare all'organizzatore.
    public static string BuildReply(
        CalendarInvite invite,
        string attendeeEmail,
        string? attendeeName,
        CalendarReplyKind kind)
    {
        var partStat = kind switch
        {
            CalendarReplyKind.Accepted => "ACCEPTED",
            CalendarReplyKind.Tentative => "TENTATIVE",
            _ => "DECLINED"
        };

        // Proprietà dell'evento da riportare identiche nella risposta.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UID", "SEQUENCE", "RECURRENCE-ID", "DTSTART", "DTEND",
            "DURATION", "SUMMARY", "ORGANIZER", "LOCATION"
        };

        var sb = new StringBuilder();

        void Add(string line) => sb.Append(Fold(line)).Append("\r\n");

        Add("BEGIN:VCALENDAR");
        Add("PRODID:-//SCportal//Mail//IT");
        Add("VERSION:2.0");
        Add("CALSCALE:GREGORIAN");
        Add("METHOD:REPLY");

        foreach (var tz in invite.RawTimezoneBlocks)
        {
            foreach (var l in tz.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                Add(l);
        }

        Add("BEGIN:VEVENT");

        foreach (var line in invite.RawEventLines)
        {
            if (keep.Contains(ParseProperty(line).Name))
                Add(line);
        }

        Add("DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));

        var cn = string.IsNullOrWhiteSpace(attendeeName)
            ? ""
            : $";CN=\"{attendeeName.Replace("\"", "'")}\"";

        Add($"ATTENDEE;PARTSTAT={partStat}{cn}:mailto:{attendeeEmail.Trim()}");
        Add("END:VEVENT");
        Add("END:VCALENDAR");

        return sb.ToString();
    }

    public static string ReplySubjectPrefix(CalendarReplyKind kind) => kind switch
    {
        CalendarReplyKind.Accepted => "Accettato",
        CalendarReplyKind.Tentative => "Provvisorio",
        _ => "Rifiutato"
    };

    // ======================================================================
    // Helpers
    // ======================================================================

    private sealed record IcsProperty(string Name, Dictionary<string, string> Params, string Value)
    {
        public string? Param(string key) => Params.TryGetValue(key, out var v) ? v : null;
    }

    private static List<string> Unfold(string ics)
    {
        var result = new List<string>();

        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && result.Count > 0)
            {
                result[^1] += raw[1..];
                continue;
            }

            if (raw.Length > 0)
                result.Add(raw);
        }

        return result;
    }

    // NAME;PARAM=val;PARAM2="a:b":VALUE  (i ":" dentro le virgolette non separano)
    private static IcsProperty ParseProperty(string line)
    {
        var inQuotes = false;
        var colon = -1;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ':' && !inQuotes) { colon = i; break; }
        }

        var head = colon >= 0 ? line[..colon] : line;
        var value = colon >= 0 ? line[(colon + 1)..] : "";

        var parts = new List<string>();
        var sb = new StringBuilder();
        inQuotes = false;

        foreach (var c in head)
        {
            if (c == '"') inQuotes = !inQuotes;

            if (c == ';' && !inQuotes)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        parts.Add(sb.ToString());

        var prms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=');
            if (eq > 0)
                prms[p[..eq].Trim()] = p[(eq + 1)..].Trim();
        }

        return new IcsProperty(parts[0].Trim().ToUpperInvariant(), prms, value.Trim());
    }

    private static DateTime? ParseDate(IcsProperty? p, out bool allDay)
    {
        allDay = false;

        if (p == null || string.IsNullOrWhiteSpace(p.Value))
            return null;

        var v = p.Value.Trim();

        if (string.Equals(p.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase) || v.Length == 8)
        {
            allDay = true;
            return DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null;
        }

        var isUtc = v.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var core = isUtc ? v[..^1] : v;

        if (!DateTime.TryParseExact(core, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;

        if (isUtc)
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc), RomeTz);

        var tzid = Unquote(p.Param("TZID"));

        if (!string.IsNullOrWhiteSpace(tzid) && TryFindTimeZone(tzid, out var tz) && tz.Id != RomeTz.Id)
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), tz);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, RomeTz);
        }

        // Ora "floating" o fuso non riconosciuto: la si considera già ora italiana.
        return dt;
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)
                || TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out win))
            {
                try
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(win);
                    return true;
                }
                catch { }
            }
        }

        tz = RomeTz;
        return false;
    }

    // Solo le forme più comuni: PT1H30M, P1D, -PT15M non serve per DTEND.
    private static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            duration = System.Xml.XmlConvert.ToTimeSpan(value.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? MailtoAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim();

        if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            v = v[7..];

        return v.Contains('@') ? v.Trim() : null;
    }

    private static string? Unquote(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');

    private static string? Unescape(string? value)
    {
        if (value == null)
            return null;

        return value
            .Replace("\\n", "\n")
            .Replace("\\N", "\n")
            .Replace("\\,", ",")
            .Replace("\\;", ";")
            .Replace("\\\\", "\\");
    }

    // RFC 5545 3.1: righe max 75 ottetti, continuazione con spazio iniziale.
    private static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75)
            return line;

        var sb = new StringBuilder();
        var count = 0;

        foreach (var rune in line.EnumerateRunes())
        {
            var len = rune.Utf8SequenceLength;

            if (count + len > 75)
            {
                sb.Append("\r\n ");
                count = 1;
            }

            sb.Append(rune.ToString());
            count += len;
        }

        return sb.ToString();
    }
}
