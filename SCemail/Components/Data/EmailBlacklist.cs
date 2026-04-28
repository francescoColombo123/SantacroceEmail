namespace SCemail.Components.Data
{
    public class EmailBlacklist
    {
        public long Id { get; set; }
        public string Email { get; set; } = "";
        public string? InseritoDa { get; set; }
        public DateTime DataInserimento { get; set; }
        public string Attiva { get; set; } = "Y";
    }
}
