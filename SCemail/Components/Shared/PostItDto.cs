namespace SCemail.Components.Shared
{
    public class PostItDto
    {
        public long Id { get; set; }
        public string Utente { get; set; } = "";
        public string Titolo { get; set; } = "";
        public string Testo { get; set; } = "";
        public string Colore { get; set; } = "yellow";

        // 👉 non usate, ma ok averle
        public DateTime? DataCreazione { get; set; }
        public DateTime? DataModifica { get; set; }

        public string Attivo { get; set; } = "Y";
    }


}
