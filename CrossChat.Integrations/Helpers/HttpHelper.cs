using Polly;

namespace CrossChat.Integrations.Helpers
{
	public static class HttpHelper
	{
		public static async Task<string?> DownloadImageAsBase64ForHtml(string imageUrl)
		{
			if (string.IsNullOrEmpty(imageUrl)) return null;

			try
			{
				var base64String = DownloadImageAsBase64(imageUrl);

				// ВАЖНО: Возвращаем сразу готовый для HTML формат!
				// Тогда во View ничего менять не придется.
				return $"data:image/jpeg;base64,{base64String}";
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex + $"Error downloading profile image from {imageUrl}");
				return null; // Если не вышло скачать - будет без аватарки
			}
		}

		public static async Task<string> DownloadImageAsBase64(string imageUrl)
		{
			// 1. Создаем политику повторов: 3 попытки, если Инста вернула 404 или упала сеть
			// Паузы: 1 сек, 2 сек, 4 сек. Это даст время CDN Фейсбука обновить кэш.
			var retryPolicy = Policy
				.Handle<HttpRequestException>() // Ловим 404, 403, 500 и обрывы сети
				.Or<TaskCanceledException>()    // Ловим таймауты
				.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
					(exception, timeSpan, retryCount, context) =>
					{
						Console.WriteLine($"[Download Image] Попытка {retryCount} провалилась: {exception.Message}. Ждем {timeSpan.TotalSeconds} сек...");
					});

			try
			{
				// 2. Оборачиваем скачивание в retryPolicy
				return await retryPolicy.ExecuteAsync(async () =>
				{
					using var httpClient = new HttpClient();

					// Делаем запрос более похожим на настоящий браузер
					httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
					httpClient.DefaultRequestHeaders.Add("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
					httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

					var imageBytes = await httpClient.GetByteArrayAsync(imageUrl);

					if (imageBytes == null || imageBytes.Length == 0)
					{
						throw new HttpRequestException("Скачался пустой файл");
					}

					return Convert.ToBase64String(imageBytes);
				});
			}
			catch (Exception ex)
			{
				throw new Exception($"[Download Image] Критическая ошибка при скачивании после всех попыток: {imageUrl}", ex);
			}
		}

		public static async Task<string> DownloadAudioFileAsBase64(string audioUrl)
		{
			try
			{
				using var httpClient = new HttpClient();
				// Добавляем заголовки для успешного скачивания
				httpClient.DefaultRequestHeaders.Add("User-Agent",
					"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

				var response = await httpClient.GetAsync(audioUrl);
				if (response.IsSuccessStatusCode)
				{
					var audioBytes = await response.Content.ReadAsByteArrayAsync();

					// Конвертируем в base64 строку
					var base64String = Convert.ToBase64String(audioBytes);

					Console.WriteLine($"Audio converted to base64, length: {base64String.Length} chars");
					return base64String;
				}

				Console.WriteLine($"Failed to download audio: {response.StatusCode}");
				return null;
			}
			catch (Exception ex)
			{
				Console.WriteLine("Error downloading audio file", ex);
				return null;
			}
		}
	}
}
