using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
	public class AuthUser
	{
		public User User { get; set; }
		public List<Profile> Profiles = new List<Profile>();
	}

	[Authorize]
	[Route("profile")]
	public class ProfileController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly ILogger<ProfileController> _logger;

		private readonly CrossChat.Services.IVoiceService _voiceService;
		private readonly CrossChat.Integrations.Interfaces.IAiService _aiService;

		public ProfileController(
			AppDbContext db,
			ILogger<ProfileController> logger,
			CrossChat.Services.IVoiceService voiceService,
			CrossChat.Integrations.Interfaces.IAiService aiService)
		{
			_db = db;
			_logger = logger;
			_voiceService = voiceService;
			_aiService = aiService;
		}

		// Страница профиля
		[HttpGet]
		public async Task<IActionResult> Index(int profileId)
		{
			// Безопасно пытаемся достать ID
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

			// Если ID нет или он не число (битая кука)
			if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
			{
				// ЭВАКУАЦИЯ: Чистим куки и шлем на вход
				await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
				return RedirectToAction("Index", "Home");
			}

			// Тянем профиль и ВСЕ его настройки одним махом
			var profile = await _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.TelegramChannelSettingsList)
				.Include(p => p.BlueSkySettingsList)
				.Include(p => p.YouTubeSettingsList)
				.FirstOrDefaultAsync(p => p.Id == profileId && p.UserId == userId);

			// Если профиль не найден или чужой — 404 или редирект
			if (profile == null)
			{
				_logger.LogWarning($"Пользователь {userId} пытался открыть чужой или несуществующий профиль {profileId}");
				return NotFound();
			}

			SetActiveProfileId(profileId);

			return View(profile);
		}

		[HttpGet("create")]
		[Authorize]
		public IActionResult Create() => View();

		[HttpPost("create")]
		[Authorize]
		public async Task<IActionResult> Create([FromForm] string name, IFormFile? avatarFile)
		{
			_logger.LogInformation($"[CreateProfile] Получено имя: '{name}', Размер файла: {avatarFile?.Length ?? 0}");

			if (string.IsNullOrWhiteSpace(name))
			{
				TempData["Error"] = "Имя профиля обязательно!";
				return View(); // Возвращаем форму обратно с ошибкой
			}

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var newProfile = new Profile
			{
				UserId = userId,
				Name = name,
				AvatarUrl = null
			};

			if (avatarFile != null)
			{
				newProfile.AvatarUrl = await ProcessUploadedFileToBase64(avatarFile);
			}

			_db.Profile.Add(newProfile);

			await _db.SaveChangesAsync();

			return Redirect("/profiles");
		}

		[HttpPost("delete")]
		[Authorize]
		public async Task<IActionResult> Delete(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Подгружаем ВСЕ коллекции, чтобы проверить, пуст ли профиль
			var profile = await _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.BlueSkySettingsList)
				.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

			if (profile == null) return NotFound();

			// Проверка на наличие подключенных аккаунтов
			bool hasBots = (profile.InstagramSettingsList.Any() ||
							profile.ThreadsSettingsList.Any() ||
							profile.XSettingsList.Any() ||
							profile.FacebookSettingsList.Any() ||
							profile.TelegramUserBotSettingsList.Any() ||
							profile.TelegramSettings != null ||
							profile.BlueSkySettingsList.Any());

			if (hasBots)
			{
				TempData["Error"] = "Сначала удалите все подключенные соцсети из профиля.";
				return Redirect("/profiles");
			}

			_db.Profile.Remove(profile);
			await _db.SaveChangesAsync();
			return Redirect("/profiles");
		}

		[HttpGet("edit/{id}")]
		public async Task<IActionResult> Edit(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var profile = await _db.Profile.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

			if (profile == null) return NotFound();

			// Возвращаем вьюху "Create", но передаем в нее данные профиля
			return View("Create", profile);
		}

		[HttpPost("edit/{id}")]
		public async Task<IActionResult> Edit(int id, string name, IFormFile? avatarFile)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var profile = await _db.Profile.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

			if (profile != null)
			{
				profile.Name = name;
				if (avatarFile != null)
				{
					profile.AvatarUrl = await ProcessUploadedFileToBase64(avatarFile);
				}
				await _db.SaveChangesAsync();
			}
			return Redirect("/profiles");
		}

		private async Task<string?> ProcessUploadedFileToBase64(IFormFile file)
		{
			if (file == null || file.Length == 0) return null;

			using var ms = new MemoryStream();
			await file.CopyToAsync(ms);
			var bytes = ms.ToArray();

			// Формируем формат Data URI
			var base64String = Convert.ToBase64String(bytes);
			return $"data:{file.ContentType};base64,{base64String}";
		}

		// ==========================================================
		// 1. СТРАНИЦА НАСТРОЕК ГОЛОСА И ПЕСОЧНИЦЫ (/profile/voice)
		// ==========================================================
		[HttpGet("voice")]
		public async Task<IActionResult> VoiceSettings([FromQuery] int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var profile = await _db.Profile
				.Include(p => p.VoiceSetting)
				.FirstOrDefaultAsync(p => p.Id == profileId && p.UserId == userId);

			if (profile == null) return NotFound("Профиль не найден");

			// Если настроек голоса ещё нет — создаем дефолтные для этого профиля
			if (profile.VoiceSetting == null)
			{
				profile.VoiceSetting = new ProfileVoiceSetting
				{
					ProfileId = profile.Id,
					Provider = "Gemini",
					VoiceName = "Aoede",
					Style = "Whisper",
					Pace = "Natural",
					Accent = "American (Gen)",
					Scene = "Нежная девушка, говорит мягко и доверительно",
					IsActive = true
				};

				_db.ProfileVoiceSettings.Add(profile.VoiceSetting);
				await _db.SaveChangesAsync();
			}

			ViewBag.Profile = profile;
			return View("VoiceSettings", profile.VoiceSetting);
		}

		// ==========================================================
		// 2. СОХРАНЕНИЕ НАСТРОЕК ГОЛОСА
		// ==========================================================
		[HttpPost("voice/save")]
		public async Task<IActionResult> SaveVoiceSettings([FromBody] ProfileVoiceSetting dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var setting = await _db.ProfileVoiceSettings
				.Include(s => s.Profile)
				.FirstOrDefaultAsync(s => s.Id == dto.Id && s.Profile.UserId == userId);

			if (setting == null) return NotFound();

			setting.Provider = dto.Provider ?? "Gemini";
			setting.VoiceName = dto.VoiceName ?? "Aoede";
			setting.Style = dto.Style ?? "Whisper";
			setting.Pace = dto.Pace ?? "Natural";
			setting.Accent = dto.Accent ?? "American (Gen)";
			setting.Scene = dto.Scene ?? "";
			setting.SampleContext = dto.SampleContext;
			setting.IsActive = dto.IsActive;
			setting.Temperature = dto.Temperature;

			await _db.SaveChangesAsync();
			return Ok(new { success = true });
		}

		// ==========================================================
		// 3. ТЕСТ 1: ОЗВУЧКА ТЕКСТА
		// ==========================================================
		[HttpPost("voice/test-text")]
		public async Task<IActionResult> TestVoiceText([FromBody] TestVoiceTextDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Text))
				return BadRequest("Введите текст для озвучки");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var setting = await _db.ProfileVoiceSettings
				.Include(s => s.Profile)
				.FirstOrDefaultAsync(s => s.Id == dto.SettingId && s.Profile.UserId == userId);

			if (setting == null) return NotFound();

			// Создаем временную копию настроек с полями из формы (чтобы тестировать до сохранения!)
			var testSetting = new ProfileVoiceSetting
			{
				Provider = dto.Provider ?? setting.Provider,
				VoiceName = dto.VoiceName ?? setting.VoiceName,
				Style = dto.Style ?? setting.Style,
				Pace = dto.Pace ?? setting.Pace,
				Accent = dto.Accent ?? setting.Accent,
				Scene = dto.Scene ?? setting.Scene,
				SampleContext = dto.SampleContext ?? setting.SampleContext,
				Temperature = dto.Temperature
			};

			try
			{
				byte[] rawAudio = await _voiceService.GenerateVoiceAudioAsync(dto.Text.Trim(), testSetting, setting.Profile?.Name);

				// Упаковываем сырой PCM звук в валидный WAV с заголовком (браузер воспроизведет мгновенно!)
				byte[] wavAudio = EnsureWavHeader(rawAudio, 24000, 1, 16);

				return File(wavAudio, "audio/wav");
			}
			catch (Exception ex)
			{
				return BadRequest("Ошибка озвучки: " + ex.Message);
			}
		}

		// ==========================================================
		// 4. ТЕСТ 2: МИКРОФОН ➔ РАСПОЗНАВАНИЕ ➔ ГОЛОС ПРОФИЛЯ
		// ==========================================================
		[HttpPost("voice/test-mic")]
		public async Task<IActionResult> TestVoiceMic(IFormFile audioFile, [FromForm] int settingId)
		{
			if (audioFile == null || audioFile.Length == 0)
				return BadRequest("Аудиофайл с микрофона не получен");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var setting = await _db.ProfileVoiceSettings
				.Include(s => s.Profile)
				.FirstOrDefaultAsync(s => s.Id == settingId && s.Profile.UserId == userId);

			if (setting == null) return NotFound();

			try
			{
				// 1. Превращаем аудио в Base64
				using var ms = new MemoryStream();
				await audioFile.CopyToAsync(ms);
				string base64Audio = Convert.ToBase64String(ms.ToArray());

				// 2. Распознаем речь через твой существующий метод в AiService!
				string recognizedText = await _aiService.GeminiAudioToText(base64Audio, null);

				if (string.IsNullOrWhiteSpace(recognizedText))
				{
					return BadRequest("Не удалось распознать речь. Попробуйте сказать громче.");
				}

				// 3. Синтезируем этот текст настроенным голосом профиля
				byte[] rawVoice = await _voiceService.GenerateVoiceAudioAsync(recognizedText, setting, setting.Profile?.Name);
				byte[] wavVoice = EnsureWavHeader(rawVoice, 24000, 1, 16);

				// Возвращаем и распознанный текст, и сгенерированное аудио в Base64
				return Ok(new
				{
					success = true,
					recognizedText = recognizedText,
					audioBase64 = $"data:audio/wav;base64,{Convert.ToBase64String(wavVoice)}"
				});
			}
			catch (Exception ex)
			{
				return BadRequest("Ошибка обработки голоса: " + ex.Message);
			}
		}

		/// <summary>
		/// Добавляет 44-байтный RIFF/WAV заголовок к сырому PCM-потоку 24кГц, чтобы браузер играл звук без конвертеров
		/// </summary>
		private static byte[] EnsureWavHeader(byte[] pcmData, int sampleRate = 24000, short channels = 1, short bitsPerSample = 16)
		{
			// Если аудио уже начинается с "RIFF", значит заголовок уже есть
			if (pcmData.Length >= 4 && pcmData[0] == 0x52 && pcmData[1] == 0x49 && pcmData[2] == 0x46 && pcmData[3] == 0x46)
			{
				return pcmData;
			}

			int byteRate = sampleRate * channels * (bitsPerSample / 8);
			short blockAlign = (short)(channels * (bitsPerSample / 8));
			int totalDataLen = pcmData.Length;
			int totalAudioLen = totalDataLen + 36;

			using var outStream = new MemoryStream();
			using var writer = new BinaryWriter(outStream);

			writer.Write(new char[] { 'R', 'I', 'F', 'F' });
			writer.Write(totalAudioLen);
			writer.Write(new char[] { 'W', 'A', 'V', 'E' });
			writer.Write(new char[] { 'f', 'm', 't', ' ' });
			writer.Write(16); // subchunk1Size (16 для PCM)
			writer.Write((short)1); // AudioFormat 1 = PCM
			writer.Write(channels);
			writer.Write(sampleRate);
			writer.Write(byteRate);
			writer.Write(blockAlign);
			writer.Write(bitsPerSample);
			writer.Write(new char[] { 'd', 'a', 't', 'a' });
			writer.Write(totalDataLen);
			writer.Write(pcmData);

			return outStream.ToArray();
		}

		public class TestVoiceTextDto
		{
			public int SettingId { get; set; }
			public string Text { get; set; } = string.Empty;
			public string? Provider { get; set; }
			public string? VoiceName { get; set; }
			public string? Style { get; set; }
			public string? Pace { get; set; }
			public string? Accent { get; set; }
			public string? Scene { get; set; }
			public string? SampleContext { get; set; }
			public double Temperature { get; set; } = 1.0;
		}
	}
}
