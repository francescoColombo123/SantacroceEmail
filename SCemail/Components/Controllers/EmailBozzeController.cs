using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SCemail.Components.Data;
using SCemail.Components.Shared;

[ApiController]
[Route("api/bozze")]
public class EmailBozzeController : ControllerBase
{
    private readonly IDbContextFactory<MailDbContext> _dbFactory;

    public EmailBozzeController(IDbContextFactory<MailDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    [HttpPost("save")]
    public async Task<ActionResult<long>> SaveBozza([FromBody] EmailBozzaSaveDto dto)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        EmailBozza bozza;

        if (dto.Id.HasValue)
        {
            bozza = await db.EmailBozze
                .FirstOrDefaultAsync(x => x.Id == dto.Id.Value);

            if (bozza == null)
                return NotFound();
        }
        else
        {
            bozza = new EmailBozza();
            db.EmailBozze.Add(bozza);
        }

        bozza.Utente = dto.Utente;
        bozza.Destinatari = dto.Destinatari;
        bozza.Cc = dto.Cc;
        bozza.Ccn = dto.Ccn;
        bozza.Oggetto = dto.Oggetto;
        bozza.CorpoHtml = dto.CorpoHtml;

        await db.SaveChangesAsync();
        return Ok(bozza.Id);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<EmailBozza>> GetBozza(long id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var bozza = await db.EmailBozze
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (bozza == null)
            return NotFound();

        return Ok(bozza);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteBozza(long id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var bozza = await db.EmailBozze.FirstOrDefaultAsync(x => x.Id == id);
        if (bozza == null)
            return NotFound();

        db.EmailBozze.Remove(bozza);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
