namespace CrossChat.Helpers
{
	public static class TimeZoneHelper
	{
		private static TimeZoneInfo _timeZoneInfo;

		static TimeZoneHelper()
		{
			try
			{
				_timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
			}
			catch
			{
				_timeZoneInfo = TimeZoneInfo.Local;
			}
		}

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
