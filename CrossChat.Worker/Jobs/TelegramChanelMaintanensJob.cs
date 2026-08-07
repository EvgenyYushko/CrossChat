using CrossChat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Telegram.Bot;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]

	public class TelegramChanelMaintanensJob : IJob
	{
		private readonly AppDbContext _db;
		private readonly ILogger<TelegramChanelMaintanensJob> _logger;
		private readonly ITelegramBotClient _telegramBotClient;
		private readonly IHostEnvironment _env;

		public TelegramChanelMaintanensJob(AppDbContext db, ILogger<TelegramChanelMaintanensJob> logger
			, ITelegramBotClient telegramBotClient
			, IHostEnvironment env
		)
		{
			_db = db;
			_logger = logger;
			_telegramBotClient = telegramBotClient;
			_env = env;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			await RefreshTelegramChannels();
		}

		private async Task RefreshTelegramChannels()
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			_logger.LogInformation($"Run {nameof(TelegramChanelMaintanensJob)}");

			try
			{
				// 1. Получаем список всех каналов из базы данных
				var channels = await _db.TelegramChannelSettings.ToListAsync();
				if (!channels.Any()) return;

				bool isDbModified = false;

				foreach (var channel in channels)
				{
					try
					{
						// 2. Запрашиваем актуальную информацию о канале из Telegram API
						var chat = await _telegramBotClient.GetChat(channel.ChannelId);

						bool isChannelUpdated = false;

						// 3. Проверяем изменение названия канала
						if (!string.IsNullOrEmpty(chat.Title) && channel.ChannelTitle != chat.Title)
						{
							_logger.LogInformation($"[Telegram Refresh] Название канала {channel.ChannelId} изменено: '{channel.ChannelTitle}' -> '{chat.Title}'");
							channel.ChannelTitle = chat.Title;
							isChannelUpdated = true;
						}

						// 4. Проверяем изменение юзернейма канала (@username)
						if (channel.ChannelUsername != chat.Username)
						{
							_logger.LogInformation($"[Telegram Refresh] Юзернейм канала {channel.ChannelId} изменен: '@@{channel.ChannelUsername}' -> '@@{chat.Username}'");
							channel.ChannelUsername = chat.Username;
							isChannelUpdated = true;
						}

						// 5. Проверяем и обновляем аватарку канала
						if (chat.Photo != null && !string.IsNullOrEmpty(chat.Photo.BigFileId))
						{
							// Запрашиваем файл актуального изображения
							var file = await _telegramBotClient.GetFile(chat.Photo.BigFileId);
							if (file.FilePath != null)
							{
								using var ms = new MemoryStream();
								await _telegramBotClient.DownloadFile(file.FilePath, ms);

								var newBase64Avatar = $"data:image/jpeg;base64,{Convert.ToBase64String(ms.ToArray())}";

								// Если аватарка изменилась — обновляем запись
								if (channel.ProfilePictureUrl != newBase64Avatar)
								{
									channel.ProfilePictureUrl = newBase64Avatar;
									isChannelUpdated = true;
									_logger.LogInformation($"[Telegram Refresh] Обновлена аватарка канала '{channel.ChannelTitle}'");
								}
							}
						}
						else if (!string.IsNullOrEmpty(channel.ProfilePictureUrl))
						{
							// Если аватарку в Telegram удалили — очищаем её в БД
							channel.ProfilePictureUrl = null;
							isChannelUpdated = true;
							_logger.LogInformation($"[Telegram Refresh] Аватарка канала '{channel.ChannelTitle}' была удалена в Telegram.");
						}

						if (isChannelUpdated)
						{
							isDbModified = true;
						}

						// Пауза 200мс между запросами для соблюдения лимитов Telegram API
						await Task.Delay(200);
					}
					catch (Telegram.Bot.Exceptions.ApiRequestException apiEx) when (apiEx.ErrorCode == 400 || apiEx.ErrorCode == 403)
					{
						// Если канал был удален или бота выгнали из администраторов
						_logger.LogWarning($"[Telegram Refresh] Канал '{channel.ChannelTitle}' ({channel.ChannelId}) недоступен. Отключаем публикацию.");
						channel.IsActive = false;
						isDbModified = true;
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, $"[Telegram Refresh] Ошибка при обновлении канала '{channel.ChannelTitle}' ({channel.ChannelId})");
					}
				}

				// 6. Сохраняем все изменения в БД за один раз
				if (isDbModified)
				{
					await _db.SaveChangesAsync();
					_logger.LogInformation("[Telegram Refresh] Изменения каналов успешно сохранены в базе данных.");
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Telegram Refresh] Критическая ошибка при ежечасной синхронизации каналов");
			}
		}
	}
}
