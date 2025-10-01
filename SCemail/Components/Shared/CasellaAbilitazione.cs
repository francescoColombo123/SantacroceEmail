namespace SCemail.Components.Shared
{
    public class CasellaAbilitazione
    {
        public int Id { get; set; }

        public int CasellaId { get; set; }
        public string Username { get; set; } = string.Empty; // InfoUser.Utente

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public CasellaPosta Casella { get; set; } = null!;
    }
}
