using System.Net.Sockets;
using System.Text;
using CrossChat.Integrations.Helpers;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WTelegram;

namespace CrossChat.Integrations.Services.Telegram
{
	public class TelegramUserBotService : ITelegramUserBotService
	{
		public const string TELEGRAM_API_ID = "TELEGRAM_API_ID";
		public const string TELEGRAM_API_HASH = "TELEGRAM_API_HASH";

		private readonly ILogger<TelegramUserBotService> _logger;
		private readonly int ApiId;
		private readonly string ApiHash;
		private readonly IHostEnvironment _env;

		public TelegramUserBotService(ILogger<TelegramUserBotService> logger, IConfiguration configuration, IHostEnvironment env)
		{
			_logger = logger;
			var apiId = configuration[TELEGRAM_API_ID] ?? Environment.GetEnvironmentVariable(TELEGRAM_API_ID);
			ApiId = int.Parse(apiId);
			ApiHash = configuration[TELEGRAM_API_HASH] ?? Environment.GetEnvironmentVariable(TELEGRAM_API_HASH);
			_env = env;
		}

		private string GetSessionPath(int id) => $"userbot_{id}.session";

		public async Task<Client> CreateAndConnectAsync(UserBotDto dto)
		{
			if (_env.IsDevelopment())
			{
				return null;
			}

			string path = GetSessionPath(dto.Id);

			// 1. Восстанавливаем файл сессии, если есть байты
			if (dto.SessionBytes != null && dto.SessionBytes.Length > 0)
			{
				await File.WriteAllBytesAsync(path, dto.SessionBytes);
			}

			// 2. Всегда используем один и тот же конструктор (с конфигом)
			var client = new Client(config => config switch
			{
				"api_id" => ApiId.ToString(),
				"api_hash" => ApiHash,
				"session_pathname" => path,
				// Если это первый запуск (Inject), указываем адрес сервера
				"server_address" => dto.SessionBytes == null || dto.SessionBytes.Length == 0
									? "1>149.154.175.53:443" : null,
				_ => null
			});

			// 3. СРАЗУ настраиваем логгер и прокси (до ConnectAsync)
			WTelegram.Helpers.Log = (lvl, str) => _logger.LogDebug($"{lvl}: {str}");

			if (!string.IsNullOrEmpty(dto.ProxyHost))
			{
				SetupProxy(client, dto);
			}

			// 4. Если сессии не было — делаем Inject
			if (dto.SessionBytes == null || dto.SessionBytes.Length == 0)
			{
				_logger.LogInformation($"[UserBot {dto.Id}] Внедрение ключа...");
				UserBotHelper.InjectSession(client, dto.DcId, dto.AuthKey, dto.TgUserId);
			}

			// 5. Подключаемся
			await client.ConnectAsync();

			return client;
		}

		public async Task<byte[]> GetSessionBytesAsync(int botId)
		{
			string path = GetSessionPath(botId);

			if (File.Exists(path))
			{
				// Читаем всё в память
				byte[] sessionData = await File.ReadAllBytesAsync(path);

				// ВАЖНО: Удаляем временный файл с диска, чтобы не мусорить на сервере
				try { File.Delete(path); } catch { }

				return sessionData;
			}

			return null;
		}

		private void SetupProxy(Client client, UserBotDto dto)
		{
			string pHost = dto.ProxyHost?.Trim() ?? "";
			int pPort = dto.ProxyPort ?? 443;
			string pUser = dto.ProxyUser?.Trim() ?? "";
			string pPass = dto.ProxyPass?.Trim() ?? "";

			client.TcpHandler = async (host, port) =>
			{
				var tcpClient = new TcpClient(AddressFamily.InterNetwork);
				try
				{
					await tcpClient.ConnectAsync(pHost, pPort);

					// Формируем заголовок авторизации только если есть данные
					string authHeader = "";
					if (!string.IsNullOrWhiteSpace(pUser))
					{
						var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{pUser}:{pPass}"));
						authHeader = $"Proxy-Authorization: Basic {auth}\r\n";
					}

					// Формируем запрос CONNECT
					// Если authHeader пустой, он просто добавит пустую строку (ничего не изменит)
					var request = $"CONNECT {pHost}:{pPort} HTTP/1.1\r\n" +
								  $"Host: {pHost}:{pPort}\r\n" +
								  authHeader +
								  "\r\n";

					var stream = tcpClient.GetStream();
					byte[] requestBytes = Encoding.ASCII.GetBytes(request);
					await stream.WriteAsync(requestBytes, 0, requestBytes.Length);

					byte[] buffer = new byte[1024];
					int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
					string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

					// Проверяем ответ (обычно "HTTP/1.1 200 Connection Established")
					if (response.Contains("200"))
					{
						return tcpClient;
					}

					throw new Exception($"Proxy Error: {response.Split("\r\n")[0]}");
				}
				catch (Exception ex)
				{
					tcpClient.Dispose();
					_logger.LogError($"[Proxy] Ошибка подключения через {pHost}:{pPort}. {ex.Message}");
					throw;
				}
			};
		}
	}
}
