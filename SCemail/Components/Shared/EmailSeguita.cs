public class EmailSeguita
{
    public int Id { get; set; }
    public int EmailId { get; set; }
    public string Utente { get; set; } = default!;
    public DateTime CreataIl { get; set; }
    public string Letto { get; set; } = "N";
    public DateTime? LettoIl { get; set; }
    public int? UltimoCommentoId { get; set; }
}