using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace CrossChat.Helpers
{
	public class CleanConsoleFormatter : ConsoleFormatter
	{
		// Имя нашего форматтера - "clean"
		public CleanConsoleFormatter() : base("clean") { }

		public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider scopeProvider, TextWriter textWriter)
		{
			// 1. Пишем время
			//textWriter.Write($"[{DateTime.Now:HH:mm:ss}] ");

			// 2. (Опционально) Красим ошибки в красный
			if (logEntry.LogLevel >= LogLevel.Error)
			{
				textWriter.Write("❌ "); // Добавляем иконку для ошибок
			}

			// 3. Пишем САМО СООБЩЕНИЕ (без имени класса и info:)
			textWriter.Write(logEntry.Formatter(logEntry.State, logEntry.Exception));

			// 4. Если есть ошибка (Exception) - пишем её на новой строке
			if (logEntry.Exception != null)
			{
				textWriter.WriteLine();
				textWriter.Write(logEntry.Exception.ToString());
			}

			textWriter.WriteLine(); // Перенос строки в конце
		}
	}
}
