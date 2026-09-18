using System.Text.Json;
using System.Text.RegularExpressions;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Services
{
	public class TrendRadarService
	{
		private readonly AppDbContext _db;
		private readonly ILogger<TrendRadarService> _logger;
		private readonly HttpClient _httpClient;
		private const string GraphApiVersion = "v24.0";

		public TrendRadarService(AppDbContext db, ILogger<TrendRadarService> logger)
		{
			_db = db;
			_logger = logger;
			_httpClient = new HttpClient();
		}

		/// <summary>
		/// Находит рабочую связку Facebook Page + Instagram Business ID для пользователя.
		/// Если поле LinkedInstagramBusinessId еще пустое — опрашивает Meta Graph API на лету и сохраняет в БД!
		/// </summary>
		public async Task<(FacebookSettings? FbSettings, string? IgBusinessId)> GetOrDiscoverWorkingPairAsync(int userId)
		{
			var activePages = await _db.FacebookSettings
				.Where(s => s.UserId == userId && !string.IsNullOrEmpty(s.PageAccessToken))
				.ToListAsync();

			if (!activePages.Any()) return (null, null);

			// 1. Сначала проверяем, есть ли страница с уже сохраненным LinkedInstagramBusinessId
			var alreadyLinked = activePages.FirstOrDefault(p => !string.IsNullOrEmpty(p.LinkedInstagramBusinessId));
			if (alreadyLinked != null)
			{
				return (alreadyLinked, alreadyLinked.LinkedInstagramBusinessId);
			}

			// 2. Если в базе еще не записано — опрашиваем страницы через API
			foreach (var page in activePages)
			{
				try
				{
					string url = $"https://graph.facebook.com/{GraphApiVersion}/{page.PageId}?fields=instagram_business_account&access_token={page.PageAccessToken}";
					var response = await _httpClient.GetAsync(url);

					if (response.IsSuccessStatusCode)
					{
						var json = await response.Content.ReadAsStringAsync();
						using var doc = JsonDocument.Parse(json);

						if (doc.RootElement.TryGetProperty("instagram_business_account", out var igObj) &&
							igObj.TryGetProperty("id", out var idElem))
						{
							var igId = idElem.GetString();
							if (!string.IsNullOrEmpty(igId))
							{
								page.LinkedInstagramBusinessId = igId;
								await _db.SaveChangesAsync();

								_logger.LogInformation("[TrendRadar] ✅ Автоматически обнаружена связка: Страница '{Page}' -> Instagram ID: {IgId}", page.PageName, igId);
								return (page, igId);
							}
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "[TrendRadar] Ошибка проверки связки для страницы {PageId}", page.PageId);
				}
			}

			return (null, null);
		}

		/// <summary>
		/// Получает ID хештега из базы или запрашивает у Meta (с сохранением в БД навсегда)
		/// </summary>
		public async Task<string?> GetOrCreateHashtagIdAsync(TrackedHashtag hashtag, string igUserId, string accessToken)
		{
			if (!string.IsNullOrEmpty(hashtag.InstagramHashtagId))
				return hashtag.InstagramHashtagId;

			string cleanTag = hashtag.Tag.Replace("#", "").Trim().ToLowerInvariant();
			string url = $"https://graph.facebook.com/{GraphApiVersion}/ig_hashtag_search?user_id={igUserId}&q={Uri.EscapeDataString(cleanTag)}&access_token={accessToken}";

			try
			{
				var response = await _httpClient.GetAsync(url);
				var json = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(json);
					if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() > 0)
					{
						var tagId = dataArr[0].GetProperty("id").GetString();
						if (!string.IsNullOrEmpty(tagId))
						{
							hashtag.InstagramHashtagId = tagId;
							await _db.SaveChangesAsync();
							_logger.LogInformation("[TrendRadar] Хештег #{Tag} получил постоянный ID: {Id}", cleanTag, tagId);
							return tagId;
						}
					}
				}
				else
				{
					_logger.LogError("[TrendRadar] Ошибка поиска ID хештега #{Tag}: {Json}", cleanTag, json);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[TrendRadar] Исключение при запросе ID хештега #{Tag}", cleanTag);
			}

			return null;
		}

		/// <summary>
		/// Синхронизирует топ вирусных постов по хештегу и парсит сопутствующие теги
		/// </summary>
		public async Task<int> SyncHashtagPostsAsync(int hashtagId, int userId)
		{
			var hashtag = await _db.TrackedHashtags
				.Include(h => h.Posts)
				.FirstOrDefaultAsync(h => h.Id == hashtagId && h.UserId == userId);

			if (hashtag == null) return 0;

			var (fbSettings, igUserId) = await GetOrDiscoverWorkingPairAsync(userId);
			if (fbSettings == null || string.IsNullOrEmpty(igUserId))
			{
				_logger.LogWarning("[TrendRadar] Не найдена рабочая связка Facebook + Instagram для пользователя {UserId}", userId);
				return 0;
			}

			var igTagId = await GetOrCreateHashtagIdAsync(hashtag, igUserId, fbSettings.PageAccessToken);
			if (string.IsNullOrEmpty(igTagId)) return 0;

			string fields = "id,caption,media_type,media_url,permalink,like_count,comments_count";

			// АДАПТИВНЫЙ ЛИМИТ: если тег гигантский (как #girl), плавно снижаем лимит до 8 или 5
			int[] limitsToTry = new[] { 15, 8, 5 };

			foreach (var limit in limitsToTry)
			{
				string url = $"https://graph.facebook.com/{GraphApiVersion}/{igTagId}/top_media?user_id={igUserId}&fields={fields}&limit={limit}&access_token={fbSettings.PageAccessToken}";

				try
				{
					var response = await _httpClient.GetAsync(url);
					var json = await response.Content.ReadAsStringAsync();

					if (response.IsSuccessStatusCode)
					{
						using var doc = JsonDocument.Parse(json);
						if (!doc.RootElement.TryGetProperty("data", out var dataArr)) return 0;

						int updatedCount = 0;
						var hashtagRegex = new Regex(@"#(\w+)", RegexOptions.Compiled);

						foreach (var item in dataArr.EnumerateArray())
						{
							var mediaId = item.GetProperty("id").GetString()!;
							var caption = item.TryGetProperty("caption", out var c) ? c.GetString() : null;
							var mediaType = item.TryGetProperty("media_type", out var mt) ? mt.GetString() : "IMAGE";
							var mediaUrl = item.TryGetProperty("media_url", out var mu) ? mu.GetString() : null;
							var permalink = item.TryGetProperty("permalink", out var pl) ? pl.GetString() : null;
							var likes = item.TryGetProperty("like_count", out var lc) ? lc.GetInt32() : 0;
							var comments = item.TryGetProperty("comments_count", out var cc) ? cc.GetInt32() : 0;

							var extractedTagsList = new List<string>();
							if (!string.IsNullOrEmpty(caption))
							{
								foreach (Match match in hashtagRegex.Matches(caption))
								{
									extractedTagsList.Add("#" + match.Groups[1].Value.ToLowerInvariant());
								}
							}
							string extractedTagsStr = string.Join(" ", extractedTagsList.Distinct());

							var existingPost = hashtag.Posts.FirstOrDefault(p => p.InstagramMediaId == mediaId);
							if (existingPost != null)
							{
								existingPost.LikeCount = likes;
								existingPost.CommentsCount = comments;
								existingPost.Caption = caption;
								existingPost.MediaUrl = mediaUrl;
								existingPost.ExtractedHashtags = extractedTagsStr;
								existingPost.FetchedAt = DateTime.UtcNow;
							}
							else
							{
								var newPost = new ViralPost
								{
									TrackedHashtagId = hashtag.Id,
									InstagramMediaId = mediaId,
									Caption = caption,
									MediaType = mediaType ?? "IMAGE",
									MediaUrl = mediaUrl,
									Permalink = permalink,
									LikeCount = likes,
									CommentsCount = comments,
									ExtractedHashtags = extractedTagsStr,
									FetchedAt = DateTime.UtcNow
								};
								_db.ViralPosts.Add(newPost);
							}

							updatedCount++;
						}

						hashtag.LastSyncedAt = DateTime.UtcNow;
						await _db.SaveChangesAsync();

						_logger.LogInformation("[TrendRadar] ✅ Успешно синхронизировано {Count} постов для #{Tag} (использован limit={Limit})",
							updatedCount, hashtag.Tag, limit);
						return updatedCount;
					}

					// Если сервер Meta просит уменьшить объем данных — пробуем меньший лимит
					if (json.Contains("Please reduce the amount of data") && limit > 5)
					{
						_logger.LogWarning("[TrendRadar] Хештег #{Tag} слишком массивный для limit={Limit}. Пробуем меньший лимит...", hashtag.Tag, limit);
						continue;
					}

					_logger.LogError("[TrendRadar] ❌ Ошибка получения постов для #{Tag}: {Json}", hashtag.Tag, json);
					return 0;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "[TrendRadar] Исключение при синхронизации постов для #{Tag}", hashtag.Tag);
					return 0;
				}
			}

			return 0;
		}
	}
}