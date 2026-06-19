namespace CrossChat.Integrations.Interfaces;

public interface IEmailService
{
	Task SendFromNoReplyAsync(string toEmail, string subject, string htmlBody);
	Task SendFromNoReplyAsync(string toEmail, string subject, string htmlBody, string plainTextBody);
	Task SendFromSupportAsync(string toEmail, string subject, string htmlBody);
	Task SendFromSupportAsync(string toEmail, string subject, string htmlBody, string plainTextBody);
	Task SendEmailAsync(string fromAddress, string fromName, string toEmail, string subject, string htmlBody, string plainTextBody = null);
	Task SendWelcomeEmailAsync(string userName, string userEmail, string loginUrl, string logoPath = "/images/CrossChatPng.png");
}
