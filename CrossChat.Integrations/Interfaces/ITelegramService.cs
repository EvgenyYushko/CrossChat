using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CrossChat.Integrations.Interfaces
{
	public interface ITelegramService
	{
		Task SetWebhookAsync(string token, string webhookUrl);
		Task DeleteWebhookAsync(string token);
		Task SendMessageAsync(string token, long chatId, string text);

		//
		Task<Message> SendMessage(long senderId, string text, ReplyMarkup replyMarkup);

		Task<Message> SendMessage(long senderId, string text);

		Task<Message> SendMessage(string text, long senderId, int? replayMsgId = null, ParseMode parseMode = ParseMode.Html, ReplyMarkup replyMarkup = null, CancellationToken cancellationToken = default);

		Task<Message> SendSinglePhotoAsync(long senderId, string base64Image, string caption = "", ParseMode parseMode = ParseMode.None, ReplyMarkup replyMarkup = null);

		Task<Message[]> SendPhotoAlbumAsync(long senderId, List<string> base64Images, string caption = "");

		Task<Message> SendPaidPhotosAsync(long senderId, IEnumerable<string> base64Images, int starCount, string caption = "", ParseMode parseMode = ParseMode.None);
		Task<string?> GetChannelAvatarBase64Async(long channelId);

		Task<string?> GetChannelAvatarBase64ByFileIdAsync(string fileId);
	}
}
