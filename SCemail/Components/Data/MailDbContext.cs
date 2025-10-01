using Microsoft.EntityFrameworkCore;
using SCemail.Components.Shared;
using System.Reflection.Emit;

namespace SCemail.Components.Data
{
    public class MailDbContext : DbContext
    {
        public MailDbContext(DbContextOptions<MailDbContext> options) : base(options) { }
        public DbSet<CasellaAbilitazione> CasellaAbilitazioni { get; set; } = null!;
        public DbSet<Accesso> ACCESSI { get; set; } = default!;
        public DbSet<CasellaPosta> CasellePosta => Set<CasellaPosta>();
        public DbSet<EmailRicevuta> EmailRicevute => Set<EmailRicevuta>();
        public DbSet<EmailAllegato> EmailAllegati => Set<EmailAllegato>();
        public DbSet<InfoUser> InfoUsers => Set<InfoUser>();
        public DbSet<UserTaskItem> EmailTasks => Set<UserTaskItem>();
        public DbSet<EmailBozza> EmailBozze => Set<EmailBozza>();
        public DbSet<BozzaAllegato> BozzaAllegati => Set<BozzaAllegato>();


        protected override void OnModelCreating(ModelBuilder mb)
        {


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
                e.ToTable("CASELLEPOSTA");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID").ValueGeneratedOnAdd();
                e.Property(x => x.Email).HasColumnName("EMAIL");
                e.Property(x => x.Password).HasColumnName("PASSWORD");
                e.Property(x => x.NomeCompleto).HasColumnName("NOMECOMPLETO");
                e.Property(x => x.Username).HasColumnName("USERNAME");
                e.Property(x => x.Provider).HasColumnName("PROVIDER");
                e.Property(x => x.ImapHost).HasColumnName("IMAP_HOST");
                e.Property(x => x.ImapPort).HasColumnName("IMAP_PORT");
                e.Property(x => x.UseSsl).HasColumnName("USE_SSL");
                e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT"); // <<< importante
            });

            // === CASELLA ABILITAZIONE ===
            mb.Entity<CasellaAbilitazione>(e =>
            {
                e.ToTable("CASELLA_ABILITAZIONI"); // <-- nome corretto della tabella in Oracle
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.CasellaId).HasColumnName("CASELLA_ID");
                e.Property(x => x.Username).HasColumnName("USERNAME");
                e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");

                e.HasOne(x => x.Casella)
                 .WithMany()
                 .HasForeignKey(x => x.CasellaId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => new { x.CasellaId, x.Username }).IsUnique();
            });



            // === EMAIL_BOZZE ===
            mb.Entity<EmailBozza>(e =>
            {
                e.ToTable("EMAIL_BOZZE");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.Utente).HasColumnName("UTENTE");
                e.Property(x => x.Destinatari).HasColumnName("DESTINATARI");
                e.Property(x => x.Oggetto).HasColumnName("OGGETTO");
                e.Property(x => x.CorpoHtml).HasColumnName("CORPO_HTML");
                e.Property(x => x.LastSaved).HasColumnName("LAST_SAVED"); // tipo DATE/NULL
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
                e.ToTable("EMAIL_RICEVUTE");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("ID");
                e.Property(x => x.CasellaId).HasColumnName("CASELLA_ID");
                e.Property(x => x.MessageId).HasColumnName("MESSAGE_ID");
                e.Property(x => x.DataRicezione).HasColumnName("DATA_RICEZIONE");
                e.Property(x => x.Mittente).HasColumnName("MITTENTE");
                e.Property(x => x.Destinatari).HasColumnName("DESTINATARI");
                e.Property(x => x.Oggetto).HasColumnName("OGGETTO");
                e.Property(x => x.CorpoHtml).HasColumnName("CORPO_HTML");
                e.Property(x => x.CorpoTesto).HasColumnName("CORPO_TESTO");
                e.Property(x => x.Aperto).HasColumnName("APERTO");
                e.Property(x => x.Eliminato).HasColumnName("ELIMINATO");

                e.Property(x => x.FolderPath).HasColumnName("FOLDER_PATH");
                e.Property(x => x.MessageUid).HasColumnName("MESSAGE_UID");

                e.HasOne(x => x.Casella)
                 .WithMany(c => c.EmailRicevute)
                 .HasForeignKey(x => x.CasellaId);
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
        }

    }
}
