using CrossChat.Helpers;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Services;

public class EmailService : IEmailService
{
	private readonly EmailSettings _settings;
	private readonly ILogger<EmailService> _logger;

	public EmailService(IOptions<EmailSettings> settings, ILogger<EmailService> logger)
	{
		_settings = settings.Value;
		_logger = logger;
	}

	public async Task SendFromNoReplyAsync(string toEmail, string subject, string htmlBody)
	{
		await SendEmailAsync(
			_settings.NoReplyAddress,
			_settings.NoReplyName,
			toEmail,
			subject,
			htmlBody
		);
	}

	public async Task SendFromNoReplyAsync(string toEmail, string subject, string htmlBody, string plainTextBody)
	{
		await SendEmailAsync(
			_settings.NoReplyAddress,
			_settings.NoReplyName,
			toEmail,
			subject,
			htmlBody,
			plainTextBody
		);
	}

	public async Task SendFromSupportAsync(string toEmail, string subject, string htmlBody)
	{
		await SendEmailAsync(
			_settings.SupportAddress,
			_settings.SupportName,
			toEmail,
			subject,
			htmlBody
		);
	}

	public async Task SendFromSupportAsync(string toEmail, string subject, string htmlBody, string plainTextBody)
	{
		await SendEmailAsync(
			_settings.SupportAddress,
			_settings.SupportName,
			toEmail,
			subject,
			htmlBody,
			plainTextBody
		);
	}

	public async Task SendWelcomeEmailAsync(string userName, string userEmail, string loginUrl, string logoPath = "/images/CrossChatPng.png")
	{
		var logoUrl = $"{APP_URL}{logoPath}";

		var html = EmailTemplates.GetHtml(userName, userEmail, loginUrl, logoUrl);

		await SendFromNoReplyAsync(
			userEmail,
			"🎉 Добро пожаловать в CrossChat!",
			html
		);
	}

	public async Task SendEmailAsync(
	string fromAddress,
	string fromName,
	string toEmail,
	string subject,
	string htmlBody,
	string plainTextBody = null)
	{
		try
		{
			var email = new MimeMessage();
			email.From.Add(new MailboxAddress(fromName, fromAddress));
			email.To.Add(MailboxAddress.Parse(toEmail));
			email.Subject = subject;

			var bodyBuilder = new BodyBuilder
			{
				HtmlBody = htmlBody,
				TextBody = plainTextBody ?? StripHtml(htmlBody)
			};

			email.Body = bodyBuilder.ToMessageBody();

			using var smtp = new SmtpClient();

			// 1. ИСПРАВЛЕНИЕ ДЛЯ DOCKER/RENDER: Отключаем проверку отзыва сертификатов (CRL/OCSP).
			// Именно она вызывает зависание и TimeoutException в контейнерах Linux!
			smtp.CheckCertificateRevocation = false;

			// 2. Устанавливаем явный таймаут соединения (15 секунд вместо бесконечного ожидания)
			smtp.Timeout = 30000;

			// 3. Отключаем строгую проверку цепочки сертификатов Linux
			smtp.ServerCertificateValidationCallback = (s, c, h, e) => true;

			// Подбор правильного SSL-режима
			SecureSocketOptions socketOptions;
			if (!_settings.UseSsl)
			{
				socketOptions = SecureSocketOptions.None;
			}
			else
			{
				socketOptions = _settings.SmtpPort switch
				{
					465 => SecureSocketOptions.SslOnConnect,
					587 => SecureSocketOptions.StartTls,
					_ => SecureSocketOptions.Auto
				};
			}

			// Подключаемся к SMTP серверу
			await smtp.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, socketOptions);
			await smtp.AuthenticateAsync(_settings.SmtpUsername, _settings.SmtpPassword);
			await smtp.SendAsync(email);
			await smtp.DisconnectAsync(true);

			_logger.LogInformation(
				"Письмо успешно отправлено от {From} на {To} с темой '{Subject}'",
				fromAddress, toEmail, subject
			);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex,
				"Ошибка отправки email от {From} на {To}: {Message}",
				fromAddress, toEmail, ex.Message
			);
			// Не перевыбрасываем throw, чтобы ошибка отправки почты не ломала работу всего сервера
		}
	}

	public async Task SendErrorRefreshToken(string toEmail, string userName, int botId, string botName, string socialType)
	{
		var loginUrl = $"{APP_URL}/{socialType}?botId={botId}"; // Ссылка на страницу подключения
		var emailBody = EmailTemplates.GetReauthEmailHtml(userName, botName, socialType, loginUrl);
		await SendFromNoReplyAsync(toEmail, $"⚠️ Бот {botName} ({socialType}) требует внимания", emailBody);
	}

	/// <summary>
	/// Простая очистка HTML для plain text версии
	/// </summary>
	private static string StripHtml(string html)
	{
		if (string.IsNullOrEmpty(html))
			return string.Empty;

		// Удаляем HTML теги
		var plainText = System.Text.RegularExpressions.Regex.Replace(
			html, "<[^>]*>", string.Empty
		);

		// Декодируем HTML сущности
		plainText = System.Net.WebUtility.HtmlDecode(plainText);

		return plainText.Trim();
	}
}
