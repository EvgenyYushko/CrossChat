namespace CrossChat.Worker.Helpers
{
	public static class TimeZoneHelper
	{
		private static readonly TimeZoneInfo _timeZoneInfo;

		static TimeZoneHelper()
		{
			try
			{
				// Попытка 1: IANA ID (стандарт Linux и современного .NET)
				_timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
			}
			catch
			{
				try
				{
					// Попытка 2: Windows Registry ID (для Windows машин)
					_timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Belarus Standard Time");
				}
				catch
				{
					// Попытка 3: Гарантированный UTC+3 (работает на ЛЮБОЙ ОС в мире без исключений)
					// В Минске круглый год строго UTC+3 без перехода на летнее время
					_timeZoneInfo = TimeZoneInfo.CreateCustomTimeZone(
						"Europe/Minsk", 
						TimeSpan.FromHours(3), 
						"Europe/Minsk", 
						"Europe/Minsk");
				}
			}
		}

		// Всегда возвращает точное минское время (UTC+3) и на сервере, и на локалке!
		public static DateTime DateTimeNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZoneInfo);

		public static DateTimeOffset? ConvertFromUtc(DateTimeOffset? utcTime)
		{
			if (!utcTime.HasValue)
				return null;

			return new DateTimeOffset(
				TimeZoneInfo.ConvertTimeFromUtc(utcTime.Value.UtcDateTime, _timeZoneInfo),
				_timeZoneInfo.GetUtcOffset(utcTime.Value.UtcDateTime)
			);
		}

		public static TimeZoneInfo GetTimeZoneInfo() => _timeZoneInfo;
	}
}