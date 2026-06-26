using System.Collections.Concurrent;
using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Posting;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Worker.Services
{
	public class PostService : IPostService
	{
		// БУФЕР (КЕШ) - аналог вашего старого списка _posts
		// Храним уже готовые Domain-модели, чтобы не мапить каждый раз
		private readonly ConcurrentDictionary<Guid, BlogPost> _cache = new();
		private readonly AppDbContext _appDbContext;

		public PostService(AppDbContext appDbContext)
		{
			_appDbContext = appDbContext;
		}

		public async Task<List<BlogPost>> GetPendingPostsAsync(int profileId, AccessLevel accessLevel, int count)
		{
			// 1. Ищем посты:
			// - У которых нужный уровень доступа (Public/Private)
			// - У которых ЕСТЬ хоть одна запись в NetworkStates со статусом Pending (1)
			var entities = await _appDbContext.Posts
				.Include(p => p.Images)       // Сразу грузим картинки
				.Include(p => p.NetworkStates)// И статусы
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
				_cache[entity.Id] = model;
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
				// Обновляем кэш (хотя если вы собираетесь их удалять, это может быть не обязательно, 
				// но для консистентности оставим)
				var model = MapToDomain(entity);
				_cache[entity.Id] = model;
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
				if (!_cache.ContainsKey(entity.Id))
				{
					// Если в кеше нет, надо подгрузить картинки (мы их не инклюдили выше для скорости)
					// Но так как вы просили "доставай из буфера", давайте сделаем полную загрузку.
					var fullEntity = await _appDbContext.Posts
						.Include(p => p.Images)
						.Include(p => p.NetworkStates)
						.FirstOrDefaultAsync(p => p.Id == entity.Id);

					var model = MapToDomain(fullEntity);
					_cache[fullEntity.Id] = model;
				}
				result.Add(_cache[entity.Id]);
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
			if (_cache.TryGetValue(id, out var cachedPost))
			{
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
			_cache[id] = model; // Сохраняем в буфер
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
			_cache[post.Id] = post;
		}

		// 4. Обновить пост (Описание, Статусы)
		public async Task UpdatePostAsync(BlogPost post)
		{
			// 1. Обновляем в БД

			// Загружаем пост вместе с состояниями
			var entity = await _appDbContext.Posts
				.Include(p => p.NetworkStates)
				.Include(p => p.Images)
				.FirstOrDefaultAsync(p => p.Id == post.Id);

			if (entity != null)
			{
				entity.AccessLevel = (int)post.Access;
				entity.ShowDate = post.ShowDate;

				var imagesToRemove = entity.Images
					.Where(dbImg => !post.Images.Contains(dbImg.Base64Data))
					.ToList();

				foreach (var img in imagesToRemove)
				{
					entity.Images.Remove(img);
					_appDbContext.Remove(img); // Явно помечаем сущность на удаление из БД
				}

				// 2. Добавляем новые картинки, которых еще нет в базе данных
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

				// Проходимся по словарю из нашей модели (где есть ВСЕ ключи)
				foreach (var kvp in post.Networks)
				{
					var netType = (int)kvp.Key;
					var newStatus = (int)kvp.Value.Status;
					var newCaption = kvp.Value.Caption;

					// Ищем, есть ли запись в БД для этой сети
					var dbState = entity.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netType);

					if (dbState != null)
					{
						// СЦЕНАРИЙ: Запись в БД есть

						if (kvp.Value.Status == SocialStatus.None)
						{
							// 1. Если новый статус None -> УДАЛЯЕМ строку из БД
							// EF Core поймет, что нужно сделать DELETE
							_appDbContext.NetworkStates.Remove(dbState);
						}
						else
						{
							// 2. Если статус активный -> ОБНОВЛЯЕМ поля
							dbState.Status = newStatus;
							dbState.Caption = newCaption;
						}
					}
					else
					{
						// СЦЕНАРИЙ: Записи в БД нет

						if (kvp.Value.Status != SocialStatus.None)
						{
							// 3. Если статус стал активным -> СОЗДАЕМ новую строку
							entity.NetworkStates.Add(new NetworkStateEntity
							{
								NetworkType = netType,
								Status = newStatus,
								Caption = newCaption
							});
						}
						// Если записи нет и статус None - ничего делать не надо
					}
				}

				await _appDbContext.SaveChangesAsync();
			}

			// Обновляем кеш
			_cache[post.Id] = post;
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
			_cache.TryRemove(id, out _);
		}

		// --- MAPPERS (Преобразование типов) ---

		private BlogPost MapToDomain(PostEntity entity)
		{
			var post = new BlogPost
			{
				Id = entity.Id,
				ProfileId = entity.ProfileId,
				CreatedAt = entity.CreatedAt,
				ShowDate = entity.ShowDate,
				Access = (AccessLevel)entity.AccessLevel,
				Images = entity.Images.Select(img => img.Base64Data).ToList()
			};

			foreach (var ns in entity.NetworkStates)
			{
				var type = (NetworkType)ns.NetworkType;
				if (post.Networks.ContainsKey(type))
				{
					post.Networks[type].Status = (SocialStatus)ns.Status;
					post.Networks[type].Caption = ns.Caption;
				}
			}
			return post;
		}

		private PostEntity MapToEntity(BlogPost post)
		{
			var entity = new PostEntity
			{
				Id = post.Id,
				ProfileId = post.ProfileId,
				CreatedAt = post.CreatedAt,
				ShowDate = post.ShowDate, 
				AccessLevel = (int)post.Access,
				Images = new List<PostImageEntity>(),
				NetworkStates = new List<NetworkStateEntity>()
			};

			// Картинки
			foreach (var fileData in post.Images)
			{
				entity.Images.Add(new PostImageEntity
				{
					Base64Data = fileData // ВАЖНО: Тут должна быть реальная Base64 строка
				});
			}

			// Состояния
			foreach (var kvp in post.Networks)
			{
				if (kvp.Value.Status == SocialStatus.None)
					continue;

				entity.NetworkStates.Add(new NetworkStateEntity
				{
					NetworkType = (int)kvp.Key,
					Status = (int)kvp.Value.Status,
					Caption = kvp.Value.Caption ?? ""
				});
			}

			return entity;
		}
	}
}
