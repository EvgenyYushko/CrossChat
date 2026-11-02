using CrossChat.Data.Entities;

namespace CrossChat.Helpers
{
	public static class BioAnalyticsTracker
	{
		public static BioPageEvent ParseRequest(HttpContext context, int bioPageId, int? linkId = null, string eventType = "view")
		{
			var req = context.Request;

			// 1. IP-АДРЕС (с поддержкой прокси Render через X-Forwarded-For)
			string? ip = req.Headers["X-Forwarded-For"].FirstOrDefault();
			if (!string.IsNullOrEmpty(ip) && ip.Contains(','))
			{
				ip = ip.Split(',')[0].Trim();
			}
			if (string.IsNullOrEmpty(ip))
			{
				ip = context.Connection.RemoteIpAddress?.ToString();
			}

			// 2. 🚀 ПЛАН А (ГЕОЛОКАЦИЯ): Читаем заголовки Cloudflare / Render (0 мс задержки!)
			string? country = req.Headers["CF-IPCountry"].FirstOrDefault()
							  ?? req.Headers["X-Country-Code"].FirstOrDefault();

			if (country == "XX" || country == "T1") country = null;

			// 3. ИСТОЧНИК ТРАФИКА (Приоритет: метка ?ref= ➔ заголовок Referer)
			string? referer = req.Headers.Referer.FirstOrDefault();
			string source = DetectTrafficSource(req.Query["ref"].FirstOrDefault() ?? req.Query["utm_source"].FirstOrDefault(), referer);

			// 4. УСТРОЙСТВО, ОС И БРАУЗЕР (Включая In-App Instagram/Facebook!)
			string ua = req.Headers.UserAgent.ToString();
			var (device, os, browser) = ParseUserAgent(ua);

			// 5. ЯЗЫК УСТРОЙСТВА (Accept-Language)
			string? lang = req.Headers.AcceptLanguage.FirstOrDefault();
			if (!string.IsNullOrEmpty(lang))
			{
				lang = lang.Split(',')[0].Split(';')[0].Trim().ToLowerInvariant();
				if (lang.Length > 5) lang = lang.Substring(0, 5);
			}

			return new BioPageEvent
			{
				BioPageId = bioPageId,
				BioLinkItemId = linkId,
				EventType = eventType,
				IpAddress = ip,
				CountryCode = country,
				TrafficSource = source,
				RefererUrl = referer?.Length > 500 ? referer.Substring(0, 500) : referer,
				DeviceType = device,
				OperatingSystem = os,
				Browser = browser,
				Language = lang,
				CreatedAtUtc = DateTime.UtcNow
			};
		}

		private static string DetectTrafficSource(string? customRef, string? referer)
		{
			// Если передана прямая метка в URL: ?ref=ig, ?ref=tg, ?ref=bsky
			if (!string.IsNullOrEmpty(customRef))
			{
				string cr = customRef.ToLowerInvariant();
				if (cr == "ig" || cr.Contains("insta")) return "instagram";
				if (cr == "tg" || cr.Contains("tele")) return "telegram";
				if (cr == "bsky" || cr.Contains("blue")) return "bluesky";
				if (cr == "fb" || cr.Contains("face")) return "facebook";
				if (cr == "th" || cr.Contains("thread")) return "threads";
				if (cr == "x" || cr.Contains("twit")) return "x";
				if (cr == "yt" || cr.Contains("tube")) return "youtube";
				return cr;
			}

			if (string.IsNullOrEmpty(referer)) return "direct";

			string r = referer.ToLowerInvariant();
			if (r.Contains("instagram.com")) return "instagram";
			if (r.Contains("facebook.com") || r.Contains("fb.com")) return "facebook";
			if (r.Contains("bsky.app") || r.Contains("bluesky")) return "bluesky";
			if (r.Contains("t.me") || r.Contains("telegram.org")) return "telegram";
			if (r.Contains("t.co") || r.Contains("twitter.com") || r.Contains("x.com")) return "x";
			if (r.Contains("threads.net")) return "threads";
			if (r.Contains("youtube.com") || r.Contains("youtu.be")) return "youtube";
			if (r.Contains("tiktok.com")) return "tiktok";

			return "other";
		}

		private static (string device, string os, string browser) ParseUserAgent(string ua)
		{
			string device = "desktop";
			string os = "unknown";
			string browser = "browser";

			if (string.IsNullOrEmpty(ua)) return (device, os, browser);

			// Тип устройства
			if (ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || ua.Contains("Android", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
			{
				device = ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ? "tablet" : "mobile";
			}

			// Операционная система
			if (ua.Contains("iPhone") || ua.Contains("iPad") || ua.Contains("iOS")) os = "ios";
			else if (ua.Contains("Android")) os = "android";
			else if (ua.Contains("Windows")) os = "windows";
			else if (ua.Contains("Macintosh") || ua.Contains("Mac OS")) os = "macos";
			else if (ua.Contains("Linux")) os = "linux";

			// Встроенные браузеры соцсетей (Instagram In-App / Facebook In-App)
			if (ua.Contains("Instagram")) browser = "instagram_inapp";
			else if (ua.Contains("FBAN") || ua.Contains("FBAV") || ua.Contains("FB_IAB")) browser = "facebook_inapp";
			else if (ua.Contains("Chrome")) browser = "chrome";
			else if (ua.Contains("Safari") && !ua.Contains("Chrome")) browser = "safari";
			else if (ua.Contains("Firefox")) browser = "firefox";

			return (device, os, browser);
		}
	}
}