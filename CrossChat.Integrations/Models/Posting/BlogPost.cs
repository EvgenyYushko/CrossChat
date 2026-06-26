using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Models.Posting;
using CrossChat.Integrations.Models.Posting.Configurations;

namespace CrossChat.Integrations.Models
{
	public class BlogPost
	{
		public Guid Id { get; set; } = Guid.NewGuid();
		public int ProfileId { get; set; }

		public List<string> Images { get; set; } = new();
		public DateTime CreatedAt { get; set; } = DateTime.Now;
		public DateTime ShowDate { get; set; }

		public AccessLevel Access { get; set; } = AccessLevel.Public; // Пост публичный или приватный?

		// ВМЕСТО КУЧИ СВОЙСТВ - ОДИН СЛОВАРЬ
		// Хранит данные только для тех сетей, куда планируем постить
		public Dictionary<NetworkType, NetworkPostData> Networks { get; set; } = new();

		public BlogPost()
		{
			// Инициализируем словарь для всех известных сетей (по умолчанию Status = None)
			foreach (var net in NetworkMetadata.Supported)
			{
				Networks[net] = new NetworkPostData();
			}
		}

		// Хелпер: Получить текст
		public string GetCaption(NetworkType type)
		{
			if (type == NetworkType.All)
			{
				// Ищем первый непустой текст или возвращаем дефолтный
				return Networks.Values.FirstOrDefault(x => !string.IsNullOrEmpty(x.Caption))?.Caption ?? "";
			}
			return Networks.ContainsKey(type) ? Networks[type].Caption : "";
		}

		// Хелпер: Установить текст
		public void SetCaption(NetworkType type, string text)
		{
			if (type == NetworkType.All)
			{
				// Обновляем везде, где статус не None (то есть где пост активен)
				foreach (var net in Networks.Values.Where(n => n.Status != SocialStatus.None))
				{
					net.Caption = text;
				}
			}
			else if (Networks.ContainsKey(type))
			{
				Networks[type].Caption = text;
			}
		}

		// Хелпер: Получить статус
		public SocialStatus GetStatus(NetworkType type)
		{
			if (type == NetworkType.All) return SocialStatus.Pending; // Заглушка для All
			return Networks.ContainsKey(type) ? Networks[type].Status : SocialStatus.None;
		}
	}
}
