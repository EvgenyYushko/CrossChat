namespace CrossChat.Integrations.Models;

public class EmailSettings
{
	public string SmtpHost { get; set; }
	public int SmtpPort { get; set; } = 587;
	public string SmtpUsername { get; set; }
	public string SmtpPassword { get; set; }
	public string NoReplyAddress { get; set; }
	public string NoReplyName { get; set; }
	public string SupportAddress { get; set; }
	public string SupportName { get; set; }
	public bool UseSsl { get; set; } = true;
	public bool CheckCertificateRevocation { get; set; } = false;
}
