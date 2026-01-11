using Microsoft.EntityFrameworkCore;
using SCemail.Components.Shared;

namespace SCemail.Components.Data
{
    public class PostItService
    {
        private readonly MailDbContext _db;

        public PostItService(MailDbContext db)
        {
            _db = db;
        }

        // =========================
        // GET
        // =========================
        public async Task<List<PostItDto>> GetByUtenteAsync(string utente)
        {
            return await _db.PostIt
                .Where(x => x.Utente == utente && x.Attivo == "Y")
                .OrderBy(x => x.Id) // 👈 ordine naturale per strip
                .Select(x => new PostItDto
                {
                    Id = x.Id,
                    Utente = x.Utente,
                    Titolo = x.Titolo,
                    Testo = x.Testo,
                    Colore = x.Colore
                })
                .ToListAsync();
        }

        // =========================
        // ADD
        // =========================
        public async Task<long> AddAsync(string utente, string titolo, string testo)
        {
            var p = new PostIt
            {
                Utente = utente,
                Titolo = titolo,
                Testo = testo,
                Colore = "yellow",
                DataCreazione = DateTime.Now,
                Attivo = "Y"
            };

            _db.PostIt.Add(p);
            await _db.SaveChangesAsync();
            return p.Id;
        }

        // =========================
        // UPDATE TITOLO
        // =========================
        public async Task UpdateTitleAsync(long id, string titolo)
        {
            var p = await _db.PostIt.FindAsync(id);
            if (p == null || p.Attivo != "Y") return;

            if (p.Titolo == titolo) return; // 🛑 evita update inutili

            p.Titolo = titolo;
            p.DataModifica = DateTime.Now;

            await _db.SaveChangesAsync();
        }

        // =========================
        // UPDATE TESTO
        // =========================
        public async Task UpdateAsync(long id, string testo)
        {
            var p = await _db.PostIt.FindAsync(id);
            if (p == null || p.Attivo != "Y") return;

            if (p.Testo == testo) return; // 🛑 evita update inutili

            p.Testo = testo;
            p.DataModifica = DateTime.Now;

            await _db.SaveChangesAsync();
        }

        // =========================
        // DELETE (soft)
        // =========================
        public async Task DeleteAsync(long id)
        {
            var p = await _db.PostIt.FindAsync(id);
            if (p == null) return;

            p.Attivo = "N";
            p.DataModifica = DateTime.Now;

            await _db.SaveChangesAsync();
        }
    }
}
