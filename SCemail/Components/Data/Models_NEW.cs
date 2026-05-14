using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SCemail.Components.Data
{
    /* ======== MAIL (NEW) ======== */
    public record AllegatoItem_NEW
    {
        public int Id { get; init; }
        public string NomeFile { get; init; } = string.Empty;
        public string? MimeType { get; init; }

        public bool IsEmailEml { get; set; }
        public int? EmailEmlId { get; set; }

        public AllegatoItem_NEW() { }

        public AllegatoItem_NEW(int id, string nomeFile, string? mimeType)
        {
            Id = id;
            NomeFile = nomeFile;
            MimeType = mimeType;
        }
    }
    public class EmailInviata
    {
        [Column("ID")]
        public int Id { get; set; }

        [Column("CASELLA_ID")]
        public int CasellaId { get; set; }

        [Column("UTENTE")]
        public string Utente { get; set; } = default!;

        [Column("DESTINATARI")]
        public string Destinatari { get; set; } = default!;

        [Column("OGGETTO")]
        public string? Oggetto { get; set; }

        [Column("CORPO_HTML", TypeName = "CLOB")]
        public string? CorpoHtml { get; set; }

        [Column("CORPO_TESTO", TypeName = "CLOB")]
        public string? CorpoTesto { get; set; }

        [Column("DATA_INVIO")]
        public DateTime DataInvio { get; set; }

        // 🔹 nuovi campi per threading
        [Column("MESSAGE_ID")]
        [MaxLength(500)]
        public string? MessageId { get; set; }

        [Column("IN_REPLY_TO")]
        [MaxLength(500)]
        public string? InReplyTo { get; set; }

        [Column("REFERENCES_HDR", TypeName = "CLOB")]
        public string? ReferencesHdr { get; set; }

        [Column("THREAD_KEY")]
        [MaxLength(500)]
        public string? ThreadKey { get; set; }
        [Column("CC")]
        public string? Cc { get; set; }

        [Column("BCC")]
        public string? Bcc { get; set; }

        // 🔹 navigation properties
        public CasellaPosta? Casella { get; set; }
        public ICollection<InviataAllegato> Allegati { get; set; } = new List<InviataAllegato>();
    }

    public class InviataAllegato
    {
        public int Id { get; set; }
        public int EmailId { get; set; }
        public string NomeFile { get; set; } = default!;
        public string? MimeType { get; set; }
        public string Path { get; set; } = default!;


        public byte[] Content { get; set; } = Array.Empty<byte>();
        public EmailInviata Email { get; set; } = default!;
    }
    public record EmailListItem_NEW(
     int Id,
     DateTime? Data,
     string? Mittente,
     string? Oggetto,
     string? Aperto,
     bool HasAttachments,
     int ThreadLen,
     int Replies = 0,
     string? Preview = null,
     List<AllegatoItem_NEW>? Allegati = null,
    string? MessageId = null,
    int? CasellaId = null,
    string? CasellaEmail = null,
    string? ThreadKey = null,

    string? AssegnatoA = null,
     string? Destinatari = null,   // TO
    string? Cc = null,            // CC
    string? Ccn = null,           // BCC/CCN
    string? LettoSeguita = null,  // Y / N
    DateTime? LettoSeguitaIl = null,
        bool CanArchive = true,
            bool IsReadByCurrentUser = false

 );

    [Table("EMAIL_REGOLE")]
    public class EmailRegola
    {
        [Key]
        [Column("ID")]
        public int Id { get; set; }

        [Column("MITTENTE_LIKE")]
        public string? MittenteLike { get; set; }

        [Column("DEST_LIKE")]
        public string? DestLike { get; set; }

        [Column("OGGETTO_LIKE")]
        public string? OggettoLike { get; set; }

        [Column("ASSEGNA_A")]
        public string? AssegnaA { get; set; }

        // Mappiamo CHAR(1) con proprietà "shadow" di supporto
        [NotMapped]
        public bool SoloInvio
        {
            get => SoloInvioDb == "Y";
            set => SoloInvioDb = value ? "Y" : "N";
        }

        [Column("SOLO_INVIO")]
        public string SoloInvioDb { get; set; } = "N";

        [NotMapped]
        public bool Attiva
        {
            get => AttivaDb == "Y";
            set => AttivaDb = value ? "Y" : "N";
        }

        [Column("ATTIVA")]
        public string AttivaDb { get; set; } = "Y";

        [Column("CREATED_AT")]
        public DateTime CreatedAt { get; set; }

        [Column("CREATED_BY")]
        public string? CreatedBy { get; set; }
    }

    [Table("EMAIL_DESTINATARI")]
    public class EmailDestinatario
    {
        [Key]
        [Column("ID_EMAIL")]
        public int Id { get; set; }

        [Column("DESTINATARIO")]
        public string Email { get; set; } = null!;
    }
     [Table("POST_IT")]
    public class PostIt
    {
        [Key]
        [Column("ID")]
        public long Id { get; set; }

        [Required]
        [Column("UTENTE")]
        [MaxLength(100)]
        public string Utente { get; set; } = string.Empty;

        [Required]
        [Column("TESTO", TypeName = "CLOB")]
        public string Testo { get; set; } = string.Empty;

        [Column("COLORE")]
        [MaxLength(20)]
        public string Colore { get; set; } = "yellow";

        [Column("DATA_CREAZIONE")]
        public DateTime DataCreazione { get; set; }

        [Column("DATA_MODIFICA")]
        public DateTime? DataModifica { get; set; }

        [Column("ATTIVO")]
        [MaxLength(1)]
        public string Attivo { get; set; } = "Y";
        [Column("TITOLO")]
        [MaxLength(200)]
        public string Titolo { get; set; } = "";
    }
    public class EmailDetail_NEW
    {
        public int Id { get; set; }
        public int? CasellaId { get; set; }
        public string? CasellaEmail { get; set; }

        public string? Mittente { get; set; }
        public string? Destinatari { get; set; }
        public string? Cc { get; set; }
        public string? Ccn { get; set; }
        public string? Oggetto { get; set; }
        public DateTime? Data { get; set; }
        public string? CorpoHtml { get; set; }
        public string? CorpoTesto { get; set; }
        public List<AllegatoItem_NEW> Allegati { get; set; } = new();
        public string? Aperto { get; set; }
        public bool IsLoaded { get; set; }

        public string? MessageId { get; set; }
        public string? ThreadKey { get; set; }
        public string? InReplyTo { get; set; }
        public string? References { get; set; }
        public string? Tipo { get; set; }
        public bool CanArchive { get; set; } = true;
    }


    [Table("COMMENTI_EMAIL")]
    public class CommentoEmail
    {
        public int Id { get; set; }                     // ID
        public int EmailId { get; set; }                // EMAIL_ID (FK su EMAIL_RICEVUTE.Id)
        public string? Autore { get; set; }             // AUTORE
        public string? Testo { get; set; }              // TESTO (CLOB/NVARCHAR2)
        public DateTime DataCreazione { get; set; }     // DATA_CREAZIONE (UTC o locale)
        public byte[]? Allegato { get; set; }           // ALLEGATO (BLOB)  <-- può essere null
        public string? AllegatoNome { get; set; }       // ALLEGATO_NOME
    }


    public record FolderItem_NEW(string UiName, string Label, string IconClass, int SortOrder, int Count);

    public record OutgoingAttachment_NEW(string FileName, string? MimeType, byte[] Content);

    /* ======== TASK (NEW) ======== */
    public record UserTaskItem_NEW(
        int Id,
        string Utente,
        string EmailId,
        string Titolo,
        string? Commento,
        string Stato,
        DateTime DataCreazione
    );

    public record TaskComment_NEW(
        int Id,
        int TaskId,
        string Utente,
        string Testo,
        DateTime DataCreazione
    );
}
