namespace SCemail.Components.Shared
{
    public class AdminEmailDto
    {
        public int EmailId { get; set; }
        public string Oggetto { get; set; } = "";
        public string Mittente { get; set; } = "";
        public DateTime? Data { get; set; }

        public string AssegnatoA { get; set; } = "";
        public bool Completata { get; set; }
    }
}
