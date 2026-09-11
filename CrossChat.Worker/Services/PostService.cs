using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Posting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Services
{
	public class PostService : IPostService
	{
		// Инициализируем локальный MemoryCache с лимитом по памяти в байтах
		private static readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions
		{
			SizeLimit = 150 * 1024 * 1024 // Лимит кеша: 150 Мегабайт
		});

		private readonly AppDbContext _appDbContext;
		private readonly ILogger<PostService> _logger;

		public PostService(AppDbContext appDbContext, ILogger<PostService> logger)
		{
			_appDbContext = appDbContext;
			_logger = logger;
		}

		public async Task<List<BlogPost>> GetPendingPostsAsync(int profileId, AccessLevel accessLevel, int count)
		{
			// 1. Ищем посты со статусом Pending
			var entities = await _appDbContext.Posts
				.Include(p => p.Media)        // Подгружаем медиа (Google Drive)
				.Include(p => p.NetworkStates)// И статусы
				.AsSplitQuery()
				.Where(p => p.ProfileId == profileId)
				.Where(p => p.AccessLevel == (int)accessLevel)
				.Where(p => p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Pending))
				.Where(p => p.ShowDate <= DateTimeNow)
				.OrderBy(p => p.CreatedAt)
				.Take(count)
				.ToListAsync();

			// 2. Маппим в Domain модели
			var result = new List<BlogPost>();
			foreach (var entity in entities)
			{
				var model = MapToDomain(entity);
				AddToCache(model);
				result.Add(model);
			}

			return result;
		}

		public async Task<List<BlogPost>> GetOldPublishedPostsAsync(AccessLevel accessLevel)
		{
			var query = _appDbContext.Posts
				.Include(p => p.Media)
				.Include(p => p.NetworkStates)
				.AsSplitQuery()
				.Where(p => p.AccessLevel == (int)accessLevel)
				.Where(p => p.NetworkStates.Any() &&
							p.NetworkStates.All(ns => ns.Status == (int)SocialStatus.Published));

			var entities = await query
				.OrderByDescending(p => p.CreatedAt)
				.Skip(5)
				.ToListAsync();

			var result = new List<BlogPost>();
			foreach (var entity in entities)
			{
				var model = MapToDomain(entity);
				AddToCache(model);
				result.Add(model);
			}

			return result;
		}

		// --- МЕТОДЫ ЧТЕНИЯ ---

		public async Task<List<BlogPost>> GetPostsAsync(int profileId, NetworkType filterNet, AccessFilter accessFilter, int page, int pageSize)
		{
			IQueryable<PostEntity> query = _appDbContext.Posts
				.Include(p => p.NetworkStates)
				.Where(p => p.ProfileId == profileId);

			if (accessFilter == AccessFilter.Public)
				query = query.Where(p => p.AccessLevel == (int)AccessLevel.Public);
			else if (accessFilter == AccessFilter.Private)
				query = query.Where(p => p.AccessLevel == (int)AccessLevel.Private);

			if (filterNet != NetworkType.All)
			{
				int netTypeId = (int)filterNet;
				query = query.Where(p => p.NetworkStates.Any(ns => ns.NetworkType == netTypeId && ns.Status != (int)SocialStatus.None));
			}

			var entities = await query
				.OrderByDescending(p => p.CreatedAt)
				.Skip(page * pageSize)
				.Take(pageSize)
				.ToListAsync();

			var result = new List<BlogPost>();
			foreach (var entity in entities)
			{
				if (!_cache.TryGetValue(entity.Id, out BlogPost? cachedPost))
				{
					var fullEntity = await _appDbContext.Posts
						.Include(p => p.Media)
						.Include(p => p.NetworkStates)
						.AsSplitQuery()
						.FirstOrDefaultAsync(p => p.Id == entity.Id);

					if (fullEntity != null)
					{
						cachedPost = MapToDomain(fullEntity);
						AddToCache(cachedPost);
					}
				}

				if (cachedPost != null)
				{
					result.Add(cachedPost);
				}
			}

			return result;
		}

		public async Task<int> GetTotalCountAsync(NetworkType filterNet, AccessFilter accessFilter)
		{
			IQueryable<PostEntity> query = _appDbContext.Posts;

			if (accessFilter == AccessFilter.Public) query = query.Where(p => p.AccessLevel == (int)AccessLevel.Public);
			else if (accessFilter == AccessFilter.Private) query = query.Where(p => p.AccessLevel == (int)AccessLevel.Private);

			if (filterNet != NetworkType.All)
			{
				int nId = (int)filterNet;
				query = query.Where(p => p.NetworkStates.Any(ns => ns.NetworkType == nId && ns.Status != (int)SocialStatus.None));
			}

			return await query.CountAsync();
		}

		public async Task<PostCountsDto> GetPostCountsAsync(AccessLevel accessLevel)
		{
			var query = _appDbContext.Posts.Where(p => p.AccessLevel == (int)accessLevel);

			var pendingCount = await query.CountAsync(p =>
				p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Pending));

			var errorCount = await query.CountAsync(p =>
				p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Error));

			var publishedCount = await query.CountAsync(p =>
				p.NetworkStates.Any() &&
				!p.NetworkStates.Any(ns => ns.Status != (int)SocialStatus.Published && ns.Status != (int)SocialStatus.None));

			var totalCount = await query.CountAsync();

			return new PostCountsDto
			{
				Pending = pendingCount,
				Errors = errorCount,
				Published = publishedCount,
				Total = totalCount
			};
		}

		public async Task<BlogPost?> GetPostByIdAsync(Guid id)
		{
			if (_cache.TryGetValue(id, out BlogPost? cachedPost))
			{
				_logger.LogInformation("Post {Id} retrieved from cache", id);
				return cachedPost;
			}

			var entity = await _appDbContext.Posts
				.Include(p => p.Media)
				.Include(p => p.NetworkStates)
				.AsSplitQuery()
				.FirstOrDefaultAsync(p => p.Id == id);

			if (entity == null) return null;

			var model = MapToDomain(entity);
			AddToCache(model);

			return model;
		}

		// --- МЕТОДЫ ЗАПИСИ ---

		public async Task AddPostAsync(BlogPost post)
		{
			var entity = MapToEntity(post);

			_appDbContext.Posts.Add(entity);
			await _appDbContext.SaveChangesAsync();

			AddToCache(post);
		}

		public async Task UpdatePostAsync(BlogPost post)
		{
			var entity = await _appDbContext.Posts
				.Include(p => p.NetworkStates)
				.Include(p => p.Media)
				.AsSplitQuery()
				.FirstOrDefaultAsync(p => p.Id == post.Id);

			if (entity != null)
			{
				entity.AccessLevel = (int)post.Access;
				entity.ShowDate = post.ShowDate;

				// --- ОБНОВЛЕНИЕ МЕДИАФАЙЛОВ ---
				// Сравниваем по уникальному GoogleDriveFileId
				var newDriveIds = post.Media.Select(m => m.GoogleDriveFileId).ToHashSet();

				// Удаляем те, которых больше нет в посте
				var mediaToRemove = entity.Media
					.Where(dbMedia => !newDriveIds.Contains(dbMedia.GoogleDriveFileId))
					.ToList();

				foreach (var media in mediaToRemove)
				{
					entity.Media.Remove(media);
					_appDbContext.Remove(media);
				}

				// Добавляем новые медиафайлы
				var existingDriveIds = entity.Media.Select(m => m.GoogleDriveFileId).ToHashSet();
				foreach (var newMedia in post.Media)
				{
					if (!existingDriveIds.Contains(newMedia.GoogleDriveFileId))
					{
						entity.Media.Add(new PostMediaEntity
						{
							PostId = entity.Id,
							MediaType = newMedia.MediaType,
							GoogleDriveFileId = newMedia.GoogleDriveFileId,
							FileName = newMedia.FileName,
							MimeType = newMedia.MimeType,
							FileSizeBytes = newMedia.FileSizeBytes,
							ThumbnailDriveFileId = newMedia.ThumbnailDriveFileId,
							SortOrder = newMedia.SortOrder
						});
					}
					else
					{
						// Обновляем порядок сортировки, если он поменялся
						var existing = entity.Media.FirstOrDefault(m => m.GoogleDriveFileId == newMedia.GoogleDriveFileId);
						if (existing != null)
						{
							existing.SortOrder = newMedia.SortOrder;
						}
					}
				}

				// --- СИНХРОНИЗАЦИЯ СОСТОЯНИЙ СЕТЕЙ ---
				var dbStatesToRemove = entity.NetworkStates
					.Where(ns => !post.Networks.ContainsKey($"{((NetworkType)ns.NetworkType).ToString()}_{ns.BotId}"))
					.ToList();

				foreach (var state in dbStatesToRemove)
				{
					entity.NetworkStates.Remove(state);
					_appDbContext.NetworkStates.Remove(state);
				}

				foreach (var kvp in post.Networks)
				{
					var parts = kvp.Key.Split('_');
					var netType = (int)Enum.Parse<NetworkType>(parts[0]);
					var botId = int.Parse(parts[1]);

					var newStatus = (int)kvp.Value.Status;
					var newCaption = kvp.Value.Caption;

					var dbState = entity.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netType && ns.BotId == botId);

					if (dbState != null)
					{
						if (kvp.Value.Status == SocialStatus.None)
						{
							_appDbContext.NetworkStates.Remove(dbState);
						}
						else
						{
							// Обновляем существующую запись
							dbState.Status = newStatus;
							dbState.Caption = newCaption ?? string.Empty;
							dbState.IsVideoNote = kvp.Value.IsVideoNote;
							dbState.IsPaid = kvp.Value.IsPaid;
							dbState.Price = kvp.Value.Price;
							dbState.ButtonText = kvp.Value.ButtonText;
							dbState.ButtonUrl = kvp.Value.ButtonUrl;
						}
					}
					else
					{
						if (kvp.Value.Status != SocialStatus.None)
						{
							// Создаем новую запись (теперь с сохранением IsPaid и Price!)
							entity.NetworkStates.Add(new NetworkStateEntity
							{
								PostId = entity.Id,
								NetworkType = netType,
								BotId = botId,
								Caption = newCaption ?? string.Empty,
								Status = newStatus,
								IsVideoNote = kvp.Value.IsVideoNote,
								IsPaid = kvp.Value.IsPaid, // <-- ИСПРАВЛЕНО
								Price = kvp.Value.Price,    // <-- ИСПРАВЛЕНО
								ButtonText = kvp.Value.ButtonText,
								ButtonUrl = kvp.Value.ButtonUrl,
							});
						}
					}
				}

				await _appDbContext.SaveChangesAsync();
			}

			AddToCache(post);
		}

		public async Task DeletePostAsync(Guid id)
		{
			var entity = await _appDbContext.Posts
				.Include(p => p.Media)
				.FirstOrDefaultAsync(p => p.Id == id);

			if (entity != null)
			{
				_appDbContext.Posts.Remove(entity);
				await _appDbContext.SaveChangesAsync();
			}

			_cache.Remove(id);
		}

		#region Вспомогательные методы кеширования

		private void AddToCache(BlogPost post)
		{
			long estimatedSize = CalculatePostSize(post);

			var options = new MemoryCacheEntryOptions()
				.SetSize(estimatedSize)
				.SetAbsoluteExpiration(TimeSpan.FromHours(3));

			_cache.Set(post.Id, post, options);
		}

		private static long CalculatePostSize(BlogPost post)
		{
			long size = 512; // Базовый вес метаданных

			// Теперь медиа весит копейки, так как хранит только строки ID и имена, а не мегабайты Base64!
			if (post.Media != null)
			{
				foreach (var m in post.Media)
				{
					size += 256;
					size += (m.GoogleDriveFileId?.Length ?? 0) * 2;
					size += (m.FileName?.Length ?? 0) * 2;
				}
			}

			if (post.Networks != null)
			{
				foreach (var net in post.Networks.Values)
				{
					size += (net.Caption?.Length ?? 0) * 2;
					size += 128;
				}
			}

			return size;
		}

		#endregion

		// --- MAPPERS ---
		private BlogPost MapToDomain(PostEntity entity)
		{
			var model = new BlogPost
			{
				Id = entity.Id,
				ProfileId = entity.ProfileId,
				ShowDate = entity.ShowDate,
				CreatedAt = entity.CreatedAt,
				Access = (AccessLevel)entity.AccessLevel,
				Media = entity.Media
					.OrderBy(m => m.SortOrder)
					.Select(m => new PostMediaItem
					{
						Id = m.Id,
						MediaType = m.MediaType,
						GoogleDriveFileId = m.GoogleDriveFileId,
						ThumbnailDriveFileId = m.ThumbnailDriveFileId,
						FileName = m.FileName,
						MimeType = m.MimeType,
						FileSizeBytes = m.FileSizeBytes,
						SortOrder = m.SortOrder
					}).ToList()
			};

			foreach (var state in entity.NetworkStates)
			{
				var resolvedBotId = state.BotId ?? FindFirstActiveBotId(entity.ProfileId, (NetworkType)state.NetworkType);
				var key = $"{((NetworkType)state.NetworkType).ToString()}_{resolvedBotId}";
				model.Networks[key] = new NetworkPostData
				{
					Status = (SocialStatus)state.Status,
					Caption = state.Caption,
					IsVideoNote = state.IsVideoNote,
					IsPaid = state.IsPaid,
					Price = state.Price,
					ButtonText = state.ButtonText,
					ButtonUrl = state.ButtonUrl
				};
			}

			return model;
		}

		private PostEntity MapToEntity(BlogPost model)
		{
			var entity = new PostEntity
			{
				Id = model.Id,
				ProfileId = model.ProfileId,
				ShowDate = model.ShowDate,
				CreatedAt = model.CreatedAt,
				AccessLevel = (int)model.Access
			};

			if (model.Media != null && model.Media.Count > 0)
			{
				foreach (var m in model.Media)
				{
					entity.Media.Add(new PostMediaEntity
					{
						PostId = model.Id,
						MediaType = m.MediaType,
						GoogleDriveFileId = m.GoogleDriveFileId,
						ThumbnailDriveFileId = m.ThumbnailDriveFileId,
						FileName = m.FileName,
						MimeType = m.MimeType,
						FileSizeBytes = m.FileSizeBytes,
						SortOrder = m.SortOrder
					});
				}
			}

			foreach (var kvp in model.Networks)
			{
				if (kvp.Value.Status == SocialStatus.None) continue;

				var parts = kvp.Key.Split('_');
				var netType = (int)Enum.Parse<NetworkType>(parts[0]);
				var botId = int.Parse(parts[1]);

				entity.NetworkStates.Add(new NetworkStateEntity
				{
					PostId = model.Id,
					NetworkType = netType,
					BotId = botId,
					Caption = kvp.Value.Caption ?? string.Empty,
					Status = (int)kvp.Value.Status,
					IsVideoNote = kvp.Value.IsVideoNote,
					IsPaid = kvp.Value.IsPaid,
					Price = kvp.Value.Price,
					ButtonText = kvp.Value.ButtonText,
					ButtonUrl = kvp.Value.ButtonUrl
				});
			}

			return entity;
		}

		private int FindFirstActiveBotId(int profileId, NetworkType netType)
		{
			var profile = _appDbContext.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.TelegramChannelSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.BlueSkySettingsList)
				.FirstOrDefault(p => p.Id == profileId);

			if (profile == null) return 0;

			switch (netType)
			{
				case NetworkType.Instagram:
					return profile.InstagramSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.Facebook:
					return profile.FacebookSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.Threads:
					return profile.ThreadsSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.X:
					return profile.XSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.TelegramPublic:
					return profile.TelegramUserBotSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.TelegramChannel:
					return profile.TelegramChannelSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.BlueSky:
					return profile.BlueSkySettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				default:
					return 0;
			}
		}
	}
}