namespace SCemail.Components.Shared
{
    public class EmailBozzaSaveDto
    {
        public long? Id { get; set; }

        public string Utente { get; set; } = "";

        public string? Destinatari { get; set; } // TO
        public string? Cc { get; set; }
        public string? Ccn { get; set; }

        public string? Oggetto { get; set; }
        public string? CorpoHtml { get; set; }
    }
}
