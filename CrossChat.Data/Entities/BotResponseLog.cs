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

	// Новые поля для точного анализа и будущего биллинга по моделям:
	public int InputTokens { get; set; } = 0;
	public int OutputTokens { get; set; } = 0; // Output + Thought токены

	[MaxLength(100)]
	public string? ModelName { get; set; }    // Основная модель, которая сгенерировала ответ

	// Детализация по всем вызовам за ход (Decision 1, Media, Chat, Memory) в JSON:
	[Column(TypeName = "text")]
	public string? UsageDetailsJson { get; set; }

	// Дата ответа (по ней будем считать лимит в сутки)
	public DateTime RespondedAt { get; set; } = DateTimeNow;
}