using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using static CrossChat.Data.Helpers.TimeZoneHelper;

namespace CrossChat.Data.Entities;

public class BotResponseLog
{
	[Key]
	public int Id { get; set; }

	// Привязка к клиенту
	[ForeignKey("Customer")]
	public int CustomerId { get; set; }
	public InstagramBotCustomer Customer { get; set; } = null!;

	// ID сообщения, на которое ответили
	public string MessageId { get; set; } = string.Empty;

	// Потраченные токены (пока 0, потом будешь заполнять)
	public int TokensSpent { get; set; } = 0;

	// Дата ответа (по ней будем считать лимит в сутки)
	public DateTime RespondedAt { get; set; } = DateTimeNow;
}