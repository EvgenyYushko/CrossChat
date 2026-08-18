using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class ThreadsPublisher : ISocialPublisher
{
    public NetworkType Network => NetworkType.Threads;

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

        var success = await _service.CreatePostAsync(caption, images, settings.AccessToken);
        if (!success)
            throw new Exception($"Ошибка при публикации поста в Threads");

        await _console.Log($"Пост успешно опубликован в Threads @{settings.Username}.", settings.UserId, state.BotId);
    }
}