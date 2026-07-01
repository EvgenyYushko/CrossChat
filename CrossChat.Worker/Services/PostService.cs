using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Posting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Services
{
	public class PostService : IPostService
	{
		// Инициализируем локальный MemoryCache с жестким лимитом по памяти в байтах
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
			// 1. Ищем посты:
			// - У которых нужный уровень доступа (Public/Private)
			// - У которых ЕСТЬ хоть одна запись в NetworkStates со статусом Pending (1)
			var entities = await _appDbContext.Posts
				.Include(p => p.Images)       // Сразу грузим картинки
				.Include(p => p.NetworkStates)// И статусы
				.AsSplitQuery()
				.Where(p => p.ProfileId == profileId)
				.Where(p => p.AccessLevel == (int)accessLevel)
				.Where(p => p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Pending))
				.Where(p => p.ShowDate <= DateTime.UtcNow)
				.OrderBy(p => p.CreatedAt)    // Сначала старые (очередь)
				.Take(count)
				.ToListAsync();

			// 2. Маппим в Domain модели
			var result = new List<BlogPost>();
			foreach (var entity in entities)
			{
				// Можно обновить кэш, чтобы UI сразу увидел, что посты взяты в работу
				var model = MapToDomain(entity);
				AddToCache(model);
				result.Add(model);
			}

			return result;
		}

		public async Task<List<BlogPost>> GetOldPublishedPostsAsync(AccessLevel accessLevel)
		{
			// 1. Ищем посты:
			// - Нужный уровень доступа
			// - Есть записи в NetworkStates (защита от пустых)
			// - ВСЕ записи в NetworkStates имеют статус Published
			var query = _appDbContext.Posts
				.Include(p => p.Images)
				.Include(p => p.NetworkStates)
				.AsSplitQuery()
				.Where(p => p.AccessLevel == (int)accessLevel)
				.Where(p => p.NetworkStates.Any() &&
							p.NetworkStates.All(ns => ns.Status == (int)SocialStatus.Published));

			// 2. Сортировка и выборка
			// Сортируем от НОВЫХ к СТАРЫМ, пропускаем 5 самых свежих, берем остальные
			var entities = await query
				.OrderByDescending(p => p.CreatedAt)
				.Skip(5)
				.ToListAsync();

			// 3. Маппим в Domain модели
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

		// 1. Получить список (с пагинацией). 
		// Тут хитрость: список лучше всегда брать актуальный или частично кешировать ID.
		// Для упрощения: мы подгружаем заголовки из БД, а детали берем из кеша.
		public async Task<List<BlogPost>> GetPostsAsync(int profileId, NetworkType filterNet, AccessFilter accessFilter, int page, int pageSize)
		{
			// Строим запрос
			IQueryable<PostEntity> query = _appDbContext.Posts
				.Include(p => p.NetworkStates) // Нам нужны статусы для фильтрации
				.Where(p => p.ProfileId == profileId);

			// Фильтр по Приватности
			if (accessFilter == AccessFilter.Public)
				query = query.Where(p => p.AccessLevel == (int)AccessLevel.Public);
			else if (accessFilter == AccessFilter.Private)
				query = query.Where(p => p.AccessLevel == (int)AccessLevel.Private);

			// Фильтр по Наличию в соцсети (если это не All)
			if (filterNet != NetworkType.All)
			{
				int netTypeId = (int)filterNet;
				query = query.Where(p => p.NetworkStates.Any(ns => ns.NetworkType == netTypeId && ns.Status != (int)SocialStatus.None));
			}

			// Пагинация (Сортируем новые сверху)
			var entities = await query
				.OrderByDescending(p => p.CreatedAt)
				.Skip(page * pageSize)
				.Take(pageSize)
				.ToListAsync();

			// Превращаем в Domain модели и кладем в кеш (если их там нет)
			var result = new List<BlogPost>();
			foreach (var entity in entities)
			{
				// Если пост уже есть в кеше и он "свежий" - берем его. 
				// Но для списка нам нужны только заголовки, так что можно и смапить.
				// Для надежности берем полную версию.
				if (!_cache.TryGetValue(entity.Id, out BlogPost? cachedPost))
				{
					// Если в кэше нет — загружаем полную запись (с картинками)
					var fullEntity = await _appDbContext.Posts
						.Include(p => p.Images)
						.Include(p => p.NetworkStates)
						.AsSplitQuery()
						.FirstOrDefaultAsync(p => p.Id == entity.Id);

					if (fullEntity != null)
					{
						cachedPost = MapToDomain(fullEntity);
						// Сохраняем в кэш с ограничением времени и размера
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
			IQueryable<PostEntity> query = _appDbContext.Posts; // ... повторить фильтры query ...

			// (Сокращенно для примера, фильтры те же, что и выше)
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
			// Базовый запрос: фильтруем по уровню доступа (Public/Private)
			var query = _appDbContext.Posts.Where(p => p.AccessLevel == (int)accessLevel);

			// 1. PENDING: Пост считается ожидающим, если у него есть ХОТЯ БЫ ОДНА сеть в статусе Pending
			var pendingCount = await query.CountAsync(p =>
				p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Pending));

			// 2. ERROR: Пост считается ошибочным, если у него есть ХОТЯ БЫ ОДНА сеть в статусе Error
			var errorCount = await query.CountAsync(p =>
				p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Error));

			// 3. PUBLISHED: Пост считается полностью опубликованным, если:
			//    - У него ЕСТЬ записи в NetworkStates (защита от пустых/новых)
			//    - И ВСЕ эти записи имеют статус Published (нет ни Pending, ни Error)
			//    - Исключаем записи со статусом None (они не важны)
			var publishedCount = await query.CountAsync(p =>
				p.NetworkStates.Any() && // Есть хоть одна сеть
				!p.NetworkStates.Any(ns => ns.Status != (int)SocialStatus.Published && ns.Status != (int)SocialStatus.None));

			// 4. TOTAL: Общее количество постов в базе
			var totalCount = await query.CountAsync();

			return new PostCountsDto
			{
				Pending = pendingCount,
				Errors = errorCount,
				Published = publishedCount,
				Total = totalCount
			};
		}

		// 2. Получить один пост
		public async Task<BlogPost?> GetPostByIdAsync(Guid id)
		{
			// 1. Сначала ищем в БУФЕРЕ
			if (_cache.TryGetValue(id, out BlogPost? cachedPost))
			{
				_logger.LogInformation("file take from cashe");
				return cachedPost;
			}

			// 2. Если нет - идем в БД
			var entity = await _appDbContext.Posts
				.Include(p => p.Images)
				.Include(p => p.NetworkStates)
				.AsSplitQuery() // <-- Ключевое исправление: разделяет один тяжелый запрос на три легких
				.FirstOrDefaultAsync(p => p.Id == id);

			if (entity == null) return null;

			var model = MapToDomain(entity);

			AddToCache(model); // Сохраняем в буфер с расчетом размера и TTL

			return model;
		}

		// --- МЕТОДЫ ЗАПИСИ (Create, Update, Delete) ---

		// 3. Создать пост
		public async Task AddPostAsync(BlogPost post)
		{
			// 1. Сохраняем в БД
			var entity = MapToEntity(post);

			_appDbContext.Posts.Add(entity);
			await _appDbContext.SaveChangesAsync();

			// 2. Кладем в кеш (обновляем ID если база сгенерила, но у нас GUID создается в C#)
			AddToCache(post);
		}

		// 4. Обновить пост (Описание, Статусы)
		public async Task UpdatePostAsync(BlogPost post)
		{
			// 1. Загружаем пост вместе с состояниями и картинками
			var entity = await _appDbContext.Posts
				.Include(p => p.NetworkStates)
				.Include(p => p.Images)
				.AsSplitQuery()
				.FirstOrDefaultAsync(p => p.Id == post.Id);

			if (entity != null)
			{
				entity.AccessLevel = (int)post.Access;
				entity.ShowDate = post.ShowDate;

				// --- ОБНОВЛЕНИЕ КАРТИНОК ---
				var imagesToRemove = entity.Images
					.Where(dbImg => !post.Images.Contains(dbImg.Base64Data))
					.ToList();

				foreach (var img in imagesToRemove)
				{
					entity.Images.Remove(img);
					_appDbContext.Remove(img); // Явно помечаем сущность на удаление из БД
				}

				// Добавляем новые картинки, которых еще нет в базе данных
				var existingBase64s = entity.Images.Select(img => img.Base64Data).ToHashSet();
				foreach (var newBase64 in post.Images)
				{
					if (!existingBase64s.Contains(newBase64))
					{
						entity.Images.Add(new PostImageEntity
						{
							PostId = entity.Id,
							Base64Data = newBase64
						});
					}
				}

				// --- СИНХРОНИЗАЦИЯ СОСТОЯНИЙ СЕТЕЙ (С поддержкой BotId) ---

				// 1. Безопасное удаление: убираем из БД те направления, ключей которых вообще нет в словаре post.Networks
				var dbStatesToRemove = entity.NetworkStates
					.Where(ns => !post.Networks.ContainsKey($"{((NetworkType)ns.NetworkType).ToString()}_{ns.BotId}"))
					.ToList();

				foreach (var state in dbStatesToRemove)
				{
					entity.NetworkStates.Remove(state);
					_appDbContext.NetworkStates.Remove(state);
				}

				// 2. Добавляем новые или обновляем существующие направления
				foreach (var kvp in post.Networks)
				{
					// Парсим составной строковый ключ "Instagram_5" -> "Instagram" и ID бота 5
					var parts = kvp.Key.Split('_');
					var netType = (int)Enum.Parse<NetworkType>(parts[0]);
					var botId = int.Parse(parts[1]);

					var newStatus = (int)kvp.Value.Status;
					var newCaption = kvp.Value.Caption;

					// Ищем запись в БД одновременно по двум критериям: типу соцсети и ID конкретного бота
					var dbState = entity.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netType && ns.BotId == botId);

					if (dbState != null)
					{
						// СЦЕНАРИЙ: Запись в БД есть
						if (kvp.Value.Status == SocialStatus.None)
						{
							// Если статус стал None -> УДАЛЯЕМ строку из БД
							_appDbContext.NetworkStates.Remove(dbState);
						}
						else
						{
							// Если статус активный -> ОБНОВЛЯЕМ поля в БД
							dbState.Status = newStatus;
							dbState.Caption = newCaption;
						}
					}
					else
					{
						// СЦЕНАРИЙ: Записи в БД нет (пост добавили в новую соцсеть при редактировании)
						if (kvp.Value.Status != SocialStatus.None)
						{
							// Создаем новую запись состояния для конкретного BotId
							entity.NetworkStates.Add(new NetworkStateEntity
							{
								PostId = entity.Id,
								NetworkType = netType,
								BotId = botId,
								Caption = newCaption,
								Status = newStatus
							});
						}
					}
				}

				await _appDbContext.SaveChangesAsync();
			}

			// Обновляем кеш
			AddToCache(post);
		}

		// 5. Удалить пост целиком
		public async Task DeletePostAsync(Guid id)
		{
			var entity = await _appDbContext.Posts.FindAsync(id);
			if (entity != null)
			{
				_appDbContext.Posts.Remove(entity);
				await _appDbContext.SaveChangesAsync();
			}

			// Удаляем из кеша
			_cache.Remove(id);
		}

		#region Вспомогательные методы кеширования

		// Метод безопасного добавления в кеш с установкой лимитов
		private void AddToCache(BlogPost post)
		{
			long estimatedSize = CalculatePostSize(post);

			var options = new MemoryCacheEntryOptions()
				.SetSize(estimatedSize) // Указываем вес записи для контроля общего лимита в 150 МБ
				.SetAbsoluteExpiration(TimeSpan.FromHours(3)); // Время жизни ровно 3 час

			_cache.Set(post.Id, post, options);
		}

		// Примерная оценка веса объекта в байтах
		private static long CalculatePostSize(BlogPost post)
		{
			long size = 512; // Базовый примерный вес метаданных класса (в байтах)

			// Считаем размер картинок (символ Unicode в C# занимает 2 байта)
			if (post.Images != null)
			{
				foreach (var img in post.Images)
				{
					size += (img.Length * 2);
				}
			}

			// Считаем размер текстовых описаний соцсетей
			if (post.Networks != null)
			{
				foreach (var net in post.Networks.Values)
				{
					size += (net.Caption?.Length ?? 0) * 2;
					size += 128; // Вес структуры NetworkPostData
				}
			}

			return size;
		}

		#endregion

		// --- MAPPERS (Преобразование типов) ---
		private BlogPost MapToDomain(PostEntity entity)
		{
			var model = new BlogPost
			{
				Id = entity.Id,
				ProfileId = entity.ProfileId,
				ShowDate = entity.ShowDate,
				CreatedAt = entity.CreatedAt,
				Access = (AccessLevel)entity.AccessLevel,
				Images = entity.Images.Select(img => img.Base64Data).ToList()
			};

			foreach (var state in entity.NetworkStates)
			{
				// Ключ: "Instagram_5", "TelegramUserBot_3" и т.д.
				var resolvedBotId = state.BotId ?? FindFirstActiveBotId(entity.ProfileId, (NetworkType)state.NetworkType);

				var key = $"{((NetworkType)state.NetworkType).ToString()}_{resolvedBotId}";
				model.Networks[key] = new NetworkPostData
				{
					Status = (SocialStatus)state.Status,
					Caption = state.Caption
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

			foreach (var kvp in model.Networks)
			{
				var parts = kvp.Key.Split('_');
				var netType = (int)Enum.Parse<NetworkType>(parts[0]);
				var botId = int.Parse(parts[1]);

				entity.NetworkStates.Add(new NetworkStateEntity
				{
					PostId = model.Id,
					NetworkType = netType,
					BotId = botId,
					Caption = kvp.Value.Caption,
					Status = (int)kvp.Value.Status
				});
			}

			return entity;
		}

		// Вспомогательный метод поиска первого активного BotId в профиле по типу сети
		private int FindFirstActiveBotId(int profileId, NetworkType netType)
		{
			var profile = _appDbContext.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
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
				//case NetworkType.TelegramP:
				//	return (profile.TelegramSettings != null && profile.TelegramSettings.IsActive) ? profile.TelegramSettings.UserId : 0;
				case NetworkType.BlueSky:
					return profile.BlueSkySettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				default:
					return 0;
			}
		}
	}
}
