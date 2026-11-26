using System.Globalization;
using CrossChat.Data;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace CrossChat.Worker.Consumers.TelegramChat;

public class TelegramGroupReactionConsumer : IConsumer<TelegramGroupReactionCommand>
{
	private readonly AppDbContext _db;
	private readonly ITelegramBotClient _telegramBotClient;
	private readonly ILogger<TelegramGroupReactionConsumer> _logger;

	public TelegramGroupReactionConsumer(
		AppDbContext db,
		ITelegramBotClient telegramBotClient,
		ILogger<TelegramGroupReactionConsumer> logger)
	{
		_db = db;
		_telegramBotClient = telegramBotClient;
		_logger = logger;
	}

	public async Task Consume(ConsumeContext<TelegramGroupReactionCommand> context)
	{
		var msg = context.Message;

		// 1. Обязательная задержка 10 секунд перед реакцией (при UseConcurrencyLimit(1) даёт шаг 10, 20, 30... сек)
		await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);

		// 2. Берём свежие настройки группы из БД (если выключили бота пока ждали в очереди — не ставим)
		var group = await _db.TelegramChannelSettings
			.AsNoTracking()
			.FirstOrDefaultAsync(c => c.Id == msg.GroupSettingsId, context.CancellationToken);

		if (group == null || !group.IsActive || !group.AutoReactToMessages)
		{
			_logger.LogInformation("[TG Group Queue] Пропускаем реакцию #{MsgId}: группа выключена или удалена.", msg.MessageId);
			return;
		}

		string emoji = PickRandomTelegramEmoji(group.AllowedReactions);

		try
		{
			await _telegramBotClient.SetMessageReaction(
				chatId: msg.ChatId,
				messageId: msg.MessageId,
				reaction: new ReactionType[] { new ReactionTypeEmoji { Emoji = emoji } },
				isBig: false,
				cancellationToken: context.CancellationToken);

			_logger.LogInformation("✅ [TG Group Queue] Поставлена реакция {Emoji} на #{MsgId} в группе «{Title}»",
				emoji, msg.MessageId, group.ChannelTitle);
		}
		catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 400)
		{
			// Например, пользователь успел удалить коммент за эти 10 секунд или эмодзи запрещён в группе — не ретраим
			_logger.LogWarning("⚠️ [TG Group Queue] Telegram отклонил реакцию {Emoji} на #{MsgId} (400): {Err}",
				emoji, msg.MessageId, apiEx.Message);
		}
		catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
		{
			int waitSec = apiEx.Parameters?.RetryAfter ?? 15;
			_logger.LogWarning("⏳ [TG Group Queue] Telegram 429 TooManyRequests. Ждём {Sec}с...", waitSec);
			await Task.Delay(TimeSpan.FromSeconds(waitSec), context.CancellationToken);
			throw; // Передаём MassTransit на retry
		}
	}

	private static string PickRandomTelegramEmoji(string? allowedReactions)
	{
		if (string.IsNullOrWhiteSpace(allowedReactions))
			return "👍";

		var emojis = new List<string>();
		var enumerator = StringInfo.GetTextElementEnumerator(allowedReactions);
		while (enumerator.MoveNext())
		{
			string el = enumerator.GetTextElement().Replace("\uFE0F", "").Trim();
			if (!string.IsNullOrEmpty(el) && el != ",")
			{
				emojis.Add(el);
			}
		}

		if (emojis.Count == 0) return "👍";
		return emojis[Random.Shared.Next(emojis.Count)];
	}
}