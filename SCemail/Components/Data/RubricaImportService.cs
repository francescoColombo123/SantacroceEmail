using Microsoft.EntityFrameworkCore;
using SCemail.Components.Data;
using System.Collections.Concurrent;

namespace SCemail.Components.Data;

public class RubricaImportService
{
    private readonly IDbContextFactory<MailDbContext> _dbFactory;

    public RubricaImportService(IDbContextFactory<MailDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public int Total { get; private set; }
    public int Processed { get; private set; }
    public int Inserted { get; private set; }
    public int Skipped { get; private set; }
    public string? Error { get; private set; }

    public event Action? OnCompleted;

    public void StartImport(
        List<(string First, string Middle, string Last, string Email)> records)
    {
        if (IsRunning) return;

        IsRunning = true;
        Total = records.Count;
        Processed = Inserted = Skipped = 0;
        Error = null;

        Task.Run(async () =>
        {
            try
            {
                await ImportAsync(records);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                IsRunning = false;
                OnCompleted?.Invoke();
            }
        });
    }

    private async Task ImportAsync(
        List<(string First, string Middle, string Last, string Email)> records)
    {
        await using var db = _dbFactory.CreateDbContext();

        // 1️⃣ email già presenti
        var existing = new HashSet<string>(
            await db.RubricaContatti
                .AsNoTracking()
                .Select(x => x.Email)
                .ToListAsync(),
            StringComparer.OrdinalIgnoreCase);

        // 2️⃣ dedup CSV
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const int BATCH = 200;
        var buffer = new List<RubricaContatto>(BATCH);

        foreach (var r in records)
        {
            Processed++;

            if (!seen.Add(r.Email) || existing.Contains(r.Email))
            {
                Skipped++;
                continue;
            }

            buffer.Add(new RubricaContatto
            {
                FirstName = r.First,
                MiddleName = r.Middle,
                LastName = r.Last,
                Email = r.Email,
                CreatedAt = DateTime.Now
            });

            existing.Add(r.Email);
            Inserted++;

            if (buffer.Count >= BATCH)
            {
                db.RubricaContatti.AddRange(buffer);
                await db.SaveChangesAsync();
                buffer.Clear();
                db.ChangeTracker.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            db.RubricaContatti.AddRange(buffer);
            await db.SaveChangesAsync();
        }
    }
}

