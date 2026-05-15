using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using WTelegram;

namespace CrossChat.Integrations.Helpers
{
	public class UserBotHelper
	{
		public static void InjectSession(Client client, int dcId, string authKeyHex, long userId)
		{
			byte[] authKey = StringToByteArray(authKeyHex);

			var sessionField = typeof(Client).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance);
			var session = sessionField.GetValue(client);
			var sessionType = session.GetType();

			// Устанавливаем основные параметры
			SetMemberValue(session, "UserId", userId);
			SetMemberValue(session, "MainDC", dcId);

			// Получаем словарь сессий
			var dcSessionsField = sessionType.GetField("DCSessions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			var dcSessions = (IDictionary)dcSessionsField.GetValue(session);

			// Создаем объект DCSession
			var dcSessionType = sessionType.GetNestedType("DCSession", BindingFlags.NonPublic | BindingFlags.Public);
			var dcSession = Activator.CreateInstance(dcSessionType);

			// ВАЖНО: Генерируем случайный ID сессии (как это делает реальный клиент)
			long sessionId = 0;
			using (var rng = RandomNumberGenerator.Create())
			{
				byte[] idBytes = new byte[8];
				rng.GetBytes(idBytes);
				sessionId = BitConverter.ToInt64(idBytes, 0);
			}

			// Заполняем DCSession
			SetMemberValue(dcSession, "AuthKey", authKey);
			SetMemberValue(dcSession, "UserId", userId);
			SetMemberValue(dcSession, "id", sessionId); // session_id
			SetMemberValue(dcSession, "Client", client);
			// Устанавливаем текущую версию протокола (Layer)
			SetMemberValue(dcSession, "Layer", 184);

			// Устанавливаем соль (даже если 0, сервер потом сам её обновит)
			SetMemberValue(dcSession, "Salt", 0L);

			// Вычисляем authKeyID (Fingerprint) - последние 8 байт SHA1
			using var sha1 = SHA1.Create();
			byte[] hash = sha1.ComputeHash(authKey);
			long authKeyId = BitConverter.ToInt64(hash, hash.Length - 8);
			SetMemberValue(dcSession, "authKeyID", authKeyId);

			// Сохраняем в словарь
			dcSessions[dcId] = dcSession;

			// Делаем эту сессию активной в клиенте
			var activeDcField = typeof(Client).GetField("_dcSession", BindingFlags.NonPublic | BindingFlags.Instance);
			activeDcField.SetValue(client, dcSession);

			Console.WriteLine($"[Hack] Сессия внедрена. AuthKeyID: {authKeyId:X}, SessionID: {sessionId:X}");
		}

		private static void SetMemberValue(object obj, string name, object value)
		{
			var type = obj.GetType();
			var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (field != null) { field.SetValue(obj, value); return; }
			var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (prop != null) { prop.SetValue(obj, value); return; }
		}

		private static byte[] StringToByteArray(string hex)
		{
			byte[] bytes = new byte[hex.Length / 2];
			for (int i = 0; i < hex.Length; i += 2)
				bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
			return bytes;
		}

		public static void ForceSaveSession(Client client)
		{
			// Достаем приватный объект _session
			var sessionField = typeof(Client).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance);
			var session = sessionField.GetValue(client);

			// Вызываем приватный метод Save() у объекта Session
			var saveMethod = session.GetType().GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance);
			if (saveMethod != null)
			{
				saveMethod.Invoke(session, null);
				Console.WriteLine("[Hack] Сессия принудительно сохранена в хранилище.");
			}
		}
	}
}
