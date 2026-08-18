using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class XPublisher : ISocialPublisher
{
    public NetworkType Network => NetworkType.X;

    private readonly AppDbContext _db;
    private readonly IXService _service;
    private readonly IXConsole _console;

    public XPublisher(AppDbContext db, IXService service, IXConsole console)
    {
        _db = db;
        _service = service;
        _console = console;
    }

    public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
    {
        var settings = await _db.XSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
        if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
            throw new Exception($"Не найдены настройки для X (Twitter) (BotId: {state.BotId})");

        await _console.Log($"Начало отправки поста в X @{settings.ScreenName}.", settings.UserId, state.BotId);

        bool success;
        if (images != null && images.Any())
            success = await _service.CreateImagePost(caption, images, settings.AccessToken);
        else
            success = await _service.CreateTextPostAsync(caption, settings.AccessToken);

        if (!success)
            throw new Exception($"Ошибка при публикации твита в X");

        await _console.Log($"Пост успешно опубликован в X @{settings.ScreenName}.", settings.UserId, state.BotId);
    }
}