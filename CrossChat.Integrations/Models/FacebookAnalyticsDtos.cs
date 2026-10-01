using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CrossChat.Integrations.Models
{
	public class FacebookFeedPostDto
	{
		public string Id { get; set; } = string.Empty;
		public string? Message { get; set; }
		public string MediaType { get; set; } = "STATUS"; // PHOTO, VIDEO, STATUS, ALBUM
		public string? PictureUrl { get; set; }
		public string? PermalinkUrl { get; set; }
		public DateTime Timestamp { get; set; }

		// Метрики вовлеченности
		public int LikesCount { get; set; }     // Суммарные реакции
		public int CommentsCount { get; set; }  // Комментарии
		public int SharesCount { get; set; }    // Репосты
	}

	public class FacebookFeedPageDto
	{
		public List<FacebookFeedPostDto> Posts { get; set; } = new();
		public string? AfterCursor { get; set; }
		public string? BeforeCursor { get; set; }
		public bool HasNext => !string.IsNullOrEmpty(AfterCursor);
		public bool HasPrevious => !string.IsNullOrEmpty(BeforeCursor);
	}

	public class FacebookReactionsBreakdown
	{
		public int Like { get; set; }
		public int Love { get; set; }
		public int Haha { get; set; }
		public int Wow { get; set; }
		public int Sad { get; set; }
		public int Angry { get; set; }
		public int Total => Like + Love + Haha + Wow + Sad + Angry;
	}

	public class FacebookPostInsightsDto
	{
		public int Reach { get; set; }          // post_impressions_unique (Уникальные люди)
		public int Impressions { get; set; }    // post_impressions (Всего показов)
		public int EngagedUsers { get; set; }   // post_engaged_users (Вовлеченные)
		public int Clicks { get; set; }         // post_clicks (Клики по посту/ссылке)

		// Раскладка реакций по эмодзи
		public FacebookReactionsBreakdown Reactions { get; set; } = new();

		public double EngagementRate { get; set; }
		public string EngagementBadge { get; set; } = "Норма";
		public string BadgeColor { get; set; } = "#10b981";
	}

	public class FacebookPageInsightsDto
	{
		public int Reach28Days { get; set; }        // Суммарный охват за 28 дней
		public int EngagedUsers28Days { get; set; }  // Активные пользователи
		public int PostEngagements28Days { get; set; } // Взаимодействия с контентом
		public int FollowersCount { get; set; }      // Подписчики страницы
	}
}
