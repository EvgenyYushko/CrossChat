using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class ThreadsPublisher : ISocialPublisher
{
	private readonly AppDbContext _db;
	private readonly IThreadsService _service;
	private readonly IThreadsConsole _console;

	public ThreadsPublisher(AppDbContext db, IThreadsService service, IThreadsConsole console)
	{
		_db = db;
		_service = service;
		_console = console;
	}

	public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
	{
		var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
		if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			throw new Exception($"Не найдены настройки для Threads (BotId: {state.BotId})");

		await _console.Log($"Начало отправки поста в Threads @{settings.Username}.", settings.UserId, state.BotId);

		// 1. Публикуем основной пост
		var (success, publishedPostId) = await _service.CreatePostAsync(caption, images, settings.AccessToken);
		if (!success)
			throw new Exception($"Ошибка при публикации поста в Threads");

		await _console.Log($"Пост успешно опубликован в Threads @{settings.Username}.", settings.UserId, state.BotId);

		// 2. ПЕРВЫЙ КОММЕНТАРИЙ (ВЕТКА В THREADS)
		//if (!string.IsNullOrWhiteSpace(state.FirstComment) && !string.IsNullOrEmpty(publishedPostId))
		//{
		//	try
		//	{
		//		await _console.Log("Публикация первого комментария в Threads...", settings.UserId, state.BotId);

		//		// Пауза 4 сек для фиксации корневого поста в ленте Threads
		//		await Task.Delay(4000);

		//		var replyId = await _service.CreateReplyAsync(publishedPostId, state.FirstComment.Trim(), settings.AccessToken);
		//		if (!string.IsNullOrEmpty(replyId))
		//		{
		//			await _console.Log("Первый комментарий в Threads успешно опубликован!", settings.UserId, state.BotId);
		//		}
		//		else
		//		{
		//			await _console.Log("⚠️ Не удалось опубликовать первый комментарий в Threads (основной пост опубликован).", settings.UserId, state.BotId);
		//		}
		//	}
		//	catch (Exception ex)
		//	{
		//		await _console.Log($"⚠️ Ошибка при создании первого комментария в Threads: {ex.Message}", settings.UserId, state.BotId);
		//	}
		//}
	}
}