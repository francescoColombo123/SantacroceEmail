namespace SCemail.Components.Data
{
    public class EmailAllegato
    {
        public int Id { get; set; }
        public int EmailId { get; set; }
        public EmailRicevuta Email { get; set; } = default!;
        public string NomeFile { get; set; } = default!;
        public string? MimeType { get; set; }
        public string? PartSpec { get; set; }
    }

}
