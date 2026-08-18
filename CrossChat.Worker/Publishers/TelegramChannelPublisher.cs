using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class TelegramChannelPublisher : ISocialPublisher
{
    public NetworkType Network => NetworkType.TelegramChannel;

    private readonly AppDbContext _db;
    private readonly ITelegramService _service;
    private readonly ITelegramChannelConsole _console;

    public TelegramChannelPublisher(AppDbContext db, ITelegramService service, ITelegramChannelConsole console)
    {
        _db = db;
        _service = service;
        _console = console;
    }

    public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
    {
        var settings = await _db.TelegramChannelSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
        if (settings == null)
            throw new Exception($"Telegram канал (BotId: {state.BotId}) не найден.");

        await _console.Log($"Начало отправки поста в канал {settings.ChannelUsername}.", settings.UserId, state.BotId);

        if (images != null && images.Any())
        {
            if (images.Count == 1)
                await _service.SendSinglePhotoAsync(settings.ChannelId, images.First(), caption);
            else
                await _service.SendPhotoAlbumAsync(settings.ChannelId, images, caption);
        }
        else
        {
            await _service.SendMessage(settings.ChannelId, caption);
        }

        await _console.Log($"Пост успешно опубликован в канал {settings.ChannelUsername}.", settings.UserId, state.BotId);
    }
}