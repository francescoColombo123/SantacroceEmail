using Microsoft.EntityFrameworkCore;
using SCemail.Components.Pages;
using SCemail.Components.Shared;
using System.Reflection.Emit;
using static Dapper.SqlMapper;
using static SCemail.Components.Data.MailService_NEW;

namespace SCemail.Components.Data
{
    public class MailDbContext : DbContext
    {
        public MailDbContext(DbContextOptions<MailDbContext> options) : base(options) { }
        public DbSet<EmailMenzione> emailMenzionis { get; set; } = null!;
        public DbSet<CasellaAbilitazione> CasellaAbilitazioni { get; set; } = null!;
        public DbSet<Accesso> ACCESSI { get; set; } = default!;
        public DbSet<CasellaPosta> CasellePosta => Set<CasellaPosta>();
        public DbSet<EmailRicevuta> EmailRicevute => Set<EmailRicevuta>();
        public DbSet<EmailRegola> EmailRegole { get; set; }
        public DbSet<CommentoEmail> CommentiEmail { get; set; } = null!;
        public DbSet<EmailAllegato> EmailAllegati => Set<EmailAllegato>();
        public DbSet<InfoUser> InfoUsers => Set<InfoUser>();
        public DbSet<EmailDestinatario> EmailDestinatari { get; set; } = null!;
        public DbSet<EmailInboxSezione> EmailInboxSezioni => Set<EmailInboxSezione>();
        public DbSet<EmailInboxSezioneMap> EmailInboxSezioneMap => Set<EmailInboxSezioneMap>();
        public DbSet<EmailSeguita> EmailSeguite => Set<EmailSeguita>();
        public DbSet<EmailBlacklist> EmailBlacklist { get; set; }
        public DbSet<EmailTask> EmailTasks => Set<EmailTask>();
        public DbSet<EmailBozza> EmailBozze => Set<EmailBozza>();
        public DbSet<BozzaAllegato> BozzaAllegati => Set<BozzaAllegato>();
        public DbSet<EmailInviata> EmailInviate { get; set; } = null!;
        public DbSet<InviataAllegato> InviataAllegati { get; set; } = null!;
        public DbSet<EmailLetturaUtente> EmailLetture => Set<EmailLetturaUtente>();
        public DbSet<EmailAssegnazione> EmailAssegnazione => Set<EmailAssegnazione>();
        public DbSet<EmailArchivio> EmailArchivio => Set<EmailArchivio>();
        public DbSet<EmailWorkflow> EmailWorkflow => Set<EmailWorkflow>();
        public DbSet<PostIt> PostIt { get; set; } = null!;
        public DbSet<RubricaContatto> RubricaContatti => Set<RubricaContatto>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder
                .LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
                .EnableSensitiveDataLogging();
        }


        protected override void OnModelCreating(ModelBuilder mb)
        {
            Console.WriteLine(">>> CasellaPosta Namespace: " + typeof(CasellaPosta).Namespace);
            Console.WriteLine(">>> CasellaPosta Assembly: " + typeof(CasellaPosta).Assembly.FullName);


            mb.Entity<TaskParticipant>(e =>
            {
                e.ToTable("TaskPartecipanti");
                e.HasKey(x => x.Id);
                e.Property(x => x.Utente).HasMaxLength(128).IsRequired();
                e.HasIndex(x => new { x.TaskId, x.Utente }).IsUnique();
            });
            mb.Entity<InfoUser>(e =>
            {
                e.ToTable("INFOUSER");
                e.HasNoKey();
                e.Property(x => x.Utente).HasColumnName("UTENTE");
            });

            mb.Entity<EmailDestinatario>(e =>
            {
                e.ToTable("EMAIL_DESTINATARI");
                e.HasKey(x => x.Id);

                e.Property(x => x.Id)
                 .HasColumnName("ID_EMAIL");

                e.Property(x => x.Email)
                 .HasColumnName("DESTINATARIO")
                 .HasMaxLength(500);
            });
            mb.Entity<EmailAssegnazione>(e =>
            {
                e.ToTable("EMAIL_ASSEGNAZIONI", "SGAPP");  // schema corretto SGAPP

                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.SoloInvio).HasColumnName("SOLO_INVIO");
            });

            mb.Entity<RubricaContatto>(entity =>
            {
                entity.ToTable("RUBRICA_CONTATTI", "SGAPP");

                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id).HasColumnName("ID");
                entity.Property(e => e.FirstName).HasColumnName("FIRST_NAME");
                entity.Property(e => e.MiddleName).HasColumnName("MIDDLE_NAME");
                entity.Property(e => e.LastName).HasColumnName("LAST_NAME");
                entity.Property(e => e.Email).HasColumnName("EMAIL").IsRequired();
                entity.Property(e => e.CreatedAt).HasColumnName("CREATED_AT");

                entity.HasIndex(e => e.Email)
                      .HasDatabaseName("UX_RUBRICA_EMAIL")
                      .IsUnique();

            });


            mb.Entity<PostIt>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                      .HasColumnName("ID");

                entity.Property(e => e.Utente)
                      .HasColumnName("UTENTE")
                      .HasMaxLength(100)
                      .IsRequired();

                entity.Property(e => e.Testo)
                      .HasColumnName("TESTO")
                      .HasColumnType("CLOB")
                      .IsRequired();

                entity.Property(e => e.Colore)
                      .HasColumnName("COLORE")
                      .HasMaxLength(20)
                      .HasDefaultValue("yellow");

                entity.Property(e => e.DataCreazione)
                      .HasColumnName("DATA_CREAZIONE")
                      .HasColumnType("TIMESTAMP")
                      .HasDefaultValueSql("SYSTIMESTAMP");

                entity.Property(e => e.DataModifica)
                      .HasColumnName("DATA_MODIFICA")
                      .HasColumnType("TIMESTAMP");

                entity.Property(e => e.Attivo)
                      .HasColumnName("ATTIVO")
                      .HasMaxLength(1)
                      .HasDefaultValue("Y");
            });
            mb.Entity<CommentoEmail>(entity =>
            {
                entity.ToTable("COMMENTI_EMAIL");

                entity.HasKey(e => e.Id)
                      .HasName("PK_COMMENTI_EMAIL");

                entity.Property(e => e.Id)
                      .HasColumnName("ID");

                entity.Property(e => e.EmailId)
                      .HasColumnName("EMAIL_ID");

                entity.Property(e => e.Autore)
                      .HasColumnName("AUTORE")
                      .HasMaxLength(200);

                entity.Property(e => e.Testo)
                    .HasColumnName("TESTO")
                    .HasColumnType("CLOB")
                     .IsUnicode(false);


                entity.Property(e => e.DataCreazione)
                      .HasColumnName("DATA_CREAZIONE");

                entity.Property(e => e.Allegato)
                  .HasColumnName("ALLEGATO")
                  .HasColumnType("BLOB")
                   .IsUnicode(false);


                entity.Property(e => e.AllegatoNome)
                      .HasColumnName("ALLEGATO_NOME")
                      .HasMaxLength(400);
            });

            mb.Entity<EmailMenzione>(e =>
            {
                e.ToTable("EMAIL_MENZIONI", schema: "SGAPP");

                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.DataMenzione).HasColumnName("DATA_MENZIONE");
                e.Property(x => x.CommentoId).HasColumnName("COMMENTO_ID");

                // FK verso EMAIL_RICEVUTE
                e.HasOne(x => x.Email)
                    .WithMany()          // o .WithMany(e => e.Menzioni) se aggiungi la collection
                    .HasForeignKey(x => x.EmailId);

                // FK verso COMMENTI (se esiste EmailCommento)
                e.HasOne(x => x.Commento)
                    .WithMany()
                    .HasForeignKey(x => x.CommentoId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            mb.Entity<EmailSeguita>(entity =>
            {
                entity.ToTable("EMAIL_SEGUITE");

                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id).HasColumnName("ID");
                entity.Property(e => e.EmailId).HasColumnName("EMAIL_ID");
                entity.Property(e => e.Utente).HasColumnName("UTENTE");
                entity.Property(e => e.CreataIl).HasColumnName("CREATA_IL");
                entity.Property(e => e.Letto).HasColumnName("LETTO");
                entity.Property(e => e.LettoIl).HasColumnName("LETTO_IL");
                entity.Property(e => e.UltimoCommentoId).HasColumnName("ULTIMO_COMMENTO_ID");

                entity.HasIndex(e => new { e.EmailId, e.Utente })
                      .IsUnique();
            });

            mb.Entity<UserTaskItem>(e =>
            {
                e.ToTable("EMAIL_TASKS");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.Commento).HasColumnName("COMMENTO");
                e.Property(x => x.DataCreazione).HasColumnName("DATA_CREAZIONE");
                e.Property(x => x.Titolo).HasColumnName("TITOLO");
            });

            // === CASELLEPOSTA ===
            mb.Entity<CasellaPosta>(e =>
            {
                e.ToTable("CASELLEPOSTA", "SGAPP");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd();

                e.Property(x => x.Email).HasColumnName("EMAIL").HasMaxLength(255);
                e.Property(x => x.Password).HasColumnName("PASSWORD").HasMaxLength(255);
                e.Property(x => x.Provider).HasColumnName("PROVIDER").HasMaxLength(100);
                e.Property(x => x.ImapHost).HasColumnName("IMAP_HOST").HasMaxLength(255);
                e.Property(x => x.ImapPort).HasColumnName("IMAP_PORT");
                e.Property(x => x.UseSsl).HasColumnName("USE_SSL");
                e.Property(x => x.Attiva).HasColumnName("ATTIVA").HasMaxLength(1);
                e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");
                e.Property(x => x.NomeBk).HasColumnName("NOME_BK");
                e.Property(x => x.TitoloBk).HasColumnName("TITOLO_BK");
                e.Property(x => x.RecapitoBk).HasColumnName("RECAPITO_BK");
                e.Property(x => x.FirmaDefault).HasColumnName("FIRMA_DEFAULT");

                // ✅ Relazione corretta
                e.HasMany(c => c.Abilitazioni)
                 .WithOne(a => a.Casella)
                 .HasForeignKey(a => a.CasellaId)
                 .HasConstraintName("FK_ABILITAZIONE_CASELLA");
            });

            mb.Entity<CasellaAbilitazione>(e =>
            {
                e.ToTable("CASELLA_ABILITAZIONI", "SGAPP");

                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.CasellaId).HasColumnName("CASELLA_ID");
                e.Property(x => x.Username).HasColumnName("USERNAME");
                e.Property(x => x.Nome).HasColumnName("NOME");
                e.Property(x => x.Titolo).HasColumnName("TITOLO");
                e.Property(x => x.Recapito).HasColumnName("RECAPITO");
                e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");

                e.Property(x => x.IsAdmin)
                    .HasColumnName("IS_ADMIN")
                    .HasColumnType("NUMBER(1)")
                    .HasConversion(v => v ? 1 : 0, v => v == 1);

                e.HasOne(x => x.Casella)
                 .WithMany(c => c.Abilitazioni)     // ✔ CORRETTO
                 .HasForeignKey(x => x.CasellaId)   // ✔ CORRETTO
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => new { x.CasellaId, x.Username }).IsUnique();
            });
            mb.Entity<EmailWorkflow>(e =>
            {
                e.ToTable("EMAIL_WORKFLOW", "SGAPP");
                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.AssegnatoA).HasColumnName("ASSEGNATO_A");
                e.Property(x => x.Stato).HasColumnName("STATO");
                e.Property(x => x.DataAssegnazione).HasColumnName("DATA_ASSEGNAZIONE");
                e.Property(x => x.DataCompletamento).HasColumnName("DATA_COMPLETAMENTO");
                e.Property(x => x.Note).HasColumnName("NOTE");
            });


            // === EMAIL_BOZZE ===
            mb.Entity<EmailBozza>(e =>
            {
                e.ToTable("EMAIL_BOZZE");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.Destinatari).HasColumnName("DESTINATARI");
                e.Property(x => x.Cc) .HasColumnName("CC").HasColumnType("CLOB");
                e.Property(x => x.Ccn).HasColumnName("CCN").HasColumnType("CLOB");
                e.Property(x => x.Oggetto).HasColumnName("OGGETTO");
                e.Property(x => x.CorpoHtml).HasColumnName("CORPO_HTML");
                e.Property(x => x.LastSaved).HasColumnName("LAST_SAVED"); // tipo DATE/NULL
                e.Property(x => x.Letto)
                     .HasColumnName("LETTO")
                     .HasConversion<int>()     // bool <-> number(1)
                     .IsRequired();
            });

            mb.Entity<EmailBlacklist>(entity =>
            {
                entity.ToTable("EMAIL_BLACKLIST", "SGAPP");

                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id).HasColumnName("ID");
                entity.Property(e => e.Email).HasColumnName("EMAIL");
                entity.Property(e => e.InseritoDa).HasColumnName("INSERITO_DA");
                entity.Property(e => e.DataInserimento).HasColumnName("DATA_INSERIMENTO");
                entity.Property(e => e.Attiva).HasColumnName("ATTIVA");
            });

            mb.Entity<EmailTaskComment>(e =>
            {
                e.ToTable("EMAIL_TASK_COMMENTS");
                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.TaskId).HasColumnName("TASK_ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.Testo).HasColumnName("TESTO");
                e.Property(x => x.DataCreazione).HasColumnName("DATA_CREAZIONE");
                e.Property(x => x.ReplyTo).HasColumnName("REPLY_TO");

                // NEW
                e.Property(x => x.IsDone).HasColumnName("IS_DONE");
            });

            mb.Entity<EmailInboxSezioneMap>(e =>
            {
                e.HasKey(x => x.Id);

                e.HasIndex(x => new { x.IdEmail, x.Utente }).IsUnique();

                e.HasOne(x => x.Sezione)
                 .WithMany()
                 .HasForeignKey(x => x.IdSezione)
                 .HasPrincipalKey(s => s.Id);
            });

            mb.Entity<EmailInboxSezione>(e =>
            {
                e.HasKey(x => x.Id);
            });
            // === BOZZA_ALLEGATI ===
            mb.Entity<BozzaAllegato>(e =>
            {
                e.ToTable("BOZZA_ALLEGATI");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.BozzaId).HasColumnName("BOZZA_ID");
                e.Property(x => x.NomeFile).HasColumnName("NOME_FILE");
                e.Property(x => x.MimeType).HasColumnName("MIME_TYPE");
                e.Property(x => x.Content).HasColumnName("CONTENT");

                e.HasOne(x => x.Bozza)
                 .WithMany(b => b.Allegati)
                 .HasForeignKey(x => x.BozzaId);
            });



            // === EMAIL_RICEVUTE ===
            mb.Entity<EmailRicevuta>(e =>
            {
                e.ToTable("EMAIL_RICEVUTE", "SGAPP");
                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.CasellaId).HasColumnName("CASELLA_ID");
                e.Property(x => x.MessageId).HasColumnName("MESSAGE_ID");
                e.Property(x => x.DataRicezione).HasColumnName("DATA_RICEZIONE");
                e.Property(x => x.Mittente).HasColumnName("MITTENTE");
                e.Property(x => x.Destinatari).HasColumnName("DESTINATARI");
                e.Property(x => x.Oggetto).HasColumnName("OGGETTO");
                e.Property(x => x.CorpoHtml).HasColumnName("CORPO_HTML").HasColumnType("CLOB");
                e.Property(x => x.CorpoTesto).HasColumnName("CORPO_TESTO").HasColumnType("CLOB");
                e.Property(x => x.FolderPath).HasColumnName("FOLDER_PATH");
                e.Property(x => x.MessageUid).HasColumnName("MESSAGE_UID");
                e.Property(x => x.InReplyTo).HasColumnName("IN_REPLY_TO");
                e.Property(x => x.ReferencesHdr).HasColumnName("REFERENCES_HDR");
                e.Property(x => x.ThreadKey).HasColumnName("THREAD_KEY"); // ✅ AGGIUNTA QUESTA
                e.Property(e => e.Blacklist).HasColumnName("BLACKLIST");

                e.HasOne(x => x.Casella)
                    .WithMany(c => c.EmailRicevute)
                    .HasForeignKey(x => x.CasellaId);

                
            });

            mb.Entity<EmailInviata>(e =>
            {
                e.ToTable("EMAIL_INVIATE");
                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.CasellaId).HasColumnName("CASELLA_ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.Destinatari)
                   .HasColumnName("DESTINATARI")
                   .HasColumnType("CLOB");
                e.Property(x => x.Oggetto)
                 .HasColumnName("OGGETTO")
                 .HasColumnType("CLOB");

                // 👇 forziamo che EF usi CLOB
                e.Property(x => x.CorpoHtml)
                     .HasColumnName("CORPO_HTML")
                     .HasColumnType("CLOB")
                     .IsUnicode(false);

                e.Property(x => x.CorpoTesto)
                    .HasColumnName("CORPO_TESTO")
                    .HasColumnType("CLOB")
                    .IsUnicode(false);

                e.Property(x => x.DataInvio).HasColumnName("DATA_INVIO");
            });


            mb.Entity<InviataAllegato>(e =>
            {
                e.ToTable("INVIATA_ALLEGATI");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.NomeFile).HasColumnName("NOME_FILE");
                e.Property(x => x.MimeType).HasColumnName("MIME_TYPE");
                e.Property(x => x.Path)
                     .HasColumnName("PATH")
                     .HasMaxLength(2000)
                     .IsRequired();
                e.Property(x => x.Content)
        .HasColumnName("CONTENT")
        .HasColumnType("BLOB")
        .IsRequired();

                e.HasOne(x => x.Email)
                 .WithMany(m => m.Allegati)
                 .HasForeignKey(x => x.EmailId);
            });
            // === EMAIL_ALLEGATI ===
            mb.Entity<EmailAllegato>(e =>
            {
                e.ToTable("EMAIL_ALLEGATI");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.NomeFile).HasColumnName("NOME_FILE");
                e.Property(x => x.MimeType).HasColumnName("MIME_TYPE");
                e.Property(x => x.PartSpec).HasColumnName("PART_SPEC");

                e.HasOne(x => x.Email)
                 .WithMany(m => m.Allegati)
                 .HasForeignKey(x => x.EmailId);
            });

            mb.Entity<EmailRicevuta>()
              .HasIndex(x => new { x.CasellaId, x.FolderPath, x.MessageUid })
              .HasDatabaseName("IX_EMAIL_UID");

            mb.Entity<InviataAllegato>()
                .HasOne(a => a.Email)
                .WithMany(e => e.Allegati)
                .HasForeignKey(a => a.EmailId)
                .OnDelete(DeleteBehavior.Cascade);


            mb.Entity<EmailTask>(entity =>
            {
                entity.ToTable("EMAIL_TASKS", "SGAPP"); 
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id).HasColumnName("ID");
                entity.Property(e => e.EmailId).HasColumnName("EMAIL_ID");
                entity.Property(e => e.Utente).HasColumnName("UTENTE");
                entity.Property(e => e.Commento).HasColumnName("COMMENTO");
                entity.Property(e => e.Titolo).HasColumnName("TITOLO");
                entity.Property(e => e.Stato).HasColumnName("STATO");
                entity.Property(e => e.DataCreazione).HasColumnName("DATA_CREAZIONE");
                entity.Property(e => e.DataChiusura).HasColumnName("DATA_CHIUSURA");
            });

            mb.Entity<EmailLetturaUtente>(e =>
            {
                e.ToTable("EMAIL_LETTURE_UTENTE", "SGAPP");

                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.EmailId).HasColumnName("EMAIL_ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.DataLettura).HasColumnName("DATA_LETTURA");

                e.HasOne(x => x.Email)
                 .WithMany()
                 .HasForeignKey(x => x.EmailId);

                // Utente può leggere una email solo una volta
                e.HasIndex(x => new { x.EmailId, x.Utente }).IsUnique();
            });

            mb.Entity<EmailArchivio>(e =>
            {
                e.ToTable("EMAIL_ARCHIVIO", "SGAPP");
                e.HasKey(x => x.IdArchivio);

                e.Property(x => x.IdArchivio).HasColumnName("ID_ARCHIVIO");
                e.Property(x => x.IdEmail).HasColumnName("ID_EMAIL");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.DataArchiviazione).HasColumnName("DATA_ARCHIVIAZIONE");
                e.Property(x => x.Note).HasColumnName("NOTE");
            });
        }

    }
}
