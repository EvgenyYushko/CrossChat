using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("instagramFromFaceBook")]
	public class InstagramFromFaceBookController : ControllerBase
	{
		private readonly ILogger<InstagramFromFaceBookController> _logger;
		private const string INSTAGRAM_API_AUTH = "https://api.instagram.com/oauth/authorize";

		private string AppId => "738768042417714";
		private string AppSecret => "00b4b6c30bec609ff42c86bcdffb0aa9";

		private string faceBookRedirectUri = $"{APP_URL}/faceBook/auth/callback";

		public InstagramFromFaceBookController(ILogger<InstagramFromFaceBookController> logger)
		{
			_logger = logger;
		}

		// Эндпоинт 1: Получение OAuth ссылки
		[HttpGet]
		public ContentResult Index()
		{
			// 1. Формируем ссылку (Механизм Facebook, так как он 100% работает)
			string callbackUrl = $"{APP_URL}/instagramFromFaceBook/auth/callback";
			string encodedRedirectUri = HttpUtility.UrlEncode(callbackUrl);

			// Убираем auth_type=reauthenticate, чтобы не вводить пароль каждый раз.
			// Если снова что-то сломается с правами, вернете его временно.
			string loginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?" +
							  $"client_id={AppId}&" +
							  $"redirect_uri={encodedRedirectUri}&" +
							  $"response_type=code&" +
							  $"auth_type=reauthenticate&" +
							  $"scope=instagram_basic" +
							  $",instagram_manage_messages" +
							  $",instagram_manage_comments" +
							  $",instagram_manage_insights" +
							  $",pages_show_list" +
							  $",pages_manage_metadata" +
							  $",pages_read_engagement" +
							  $",business_management";

			// 2. Визуальная часть (Стиль Instagram)
			string html = $@"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Подключение instagramFromFaceBook</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #f9f9f9 0%, #e0e0e0 100%);
        }}
        
        .card {{
            background: white;
            padding: 40px;
            border-radius: 24px;
            box-shadow: 0 10px 40px rgba(0,0,0,0.08);
            text-align: center;
            max-width: 380px;
            width: 90%;
        }}

        h1 {{ margin: 0 0 15px; font-size: 22px; color: #333; }}
        p {{ color: #666; font-size: 15px; margin-bottom: 30px; line-height: 1.5; }}

        /* СТИЛЬ КНОПКИ INSTAGRAM */
        .insta-btn {{
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 12px;
            width: 100%;
            padding: 14px;
            border: none;
            border-radius: 12px;
            cursor: pointer;
            text-decoration: none;
            font-size: 16px;
            font-weight: 600;
            color: white;
            /* Знаменитый градиент Instagram */
            background: linear-gradient(45deg, #f09433 0%, #e6683c 25%, #dc2743 50%, #cc2366 75%, #bc1888 100%);
            transition: transform 0.2s, box-shadow 0.2s;
        }}

        .insta-btn:hover {{
            transform: translateY(-2px);
            box-shadow: 0 8px 16px rgba(220, 39, 67, 0.3);
        }}

        .insta-icon {{
            width: 24px;
            height: 24px;
            fill: white;
        }}
    </style>
</head>
<body>
    <div class=""card"">
        <h1>Подключение чат-бота</h1>
        <p>Подключите ваш бизнес-аккаунт Instagram, чтобы начать работу с сообщениями.</p>
        
        <a href=""{loginUrl}"" class=""insta-btn"">
            <!-- Иконка Instagram (SVG) -->
            <svg class=""insta-icon"" viewBox=""0 0 24 24"">
                <path d=""M12 2.163c3.204 0 3.584.012 4.85.07 3.252.148 4.771 1.691 4.919 4.919.058 1.265.069 1.645.069 4.849 0 3.205-.012 3.584-.069 4.849-.149 3.225-1.664 4.771-4.919 4.919-1.266.058-1.644.07-4.85.07-3.204 0-3.584-.012-4.849-.07-3.26-.149-4.771-1.699-4.919-4.92-.058-1.265-.07-1.644-.07-4.849 0-3.204.013-3.583.07-4.849.149-3.227 1.664-4.771 4.919-4.919 1.266-.057 1.645-.069 4.849-.069zm0-2.163c-3.259 0-3.667.014-4.947.072-4.358.2-6.78 2.618-6.98 6.98-.059 1.281-.073 1.689-.073 4.948 0 3.259.014 3.668.072 4.948.2 4.358 2.618 6.78 6.98 6.98 1.281.058 1.689.072 4.948.072 3.259 0 3.668-.014 4.948-.072 4.354-.2 6.782-2.618 6.979-6.98.059-1.28.073-1.689.073-4.948 0-3.259-.014-3.667-.072-4.947-.196-4.354-2.617-6.78-6.979-6.98-1.281-.059-1.69-.073-4.949-.073zm0 5.838c-3.403 0-6.162 2.759-6.162 6.162s2.759 6.163 6.162 6.163 6.162-2.759 6.162-6.163c0-3.403-2.759-6.162-6.162-6.162zm0 10.162c-2.209 0-4-1.79-4-4 0-2.209 1.791-4 4-4s4 1.791 4 4c0 2.21-1.791 4-4 4zm6.406-11.845c-.796 0-1.441.645-1.441 1.44s.645 1.44 1.441 1.44c.795 0 1.439-.645 1.439-1.44s-.644-1.44-1.439-1.44z""/>
            </svg>
            <span>Войти через Instagram</span>
        </a>
    </div>
</body>
</html>";
			return Content(html, "text/html");
		}

		// Эндпоинт 2: Callback от Instagram (OAuth redirect)
		
	}

	#region Models
	public class InstagramShortLivedToken
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("user_id")]
		public long UserId { get; set; }

		[JsonProperty("permissions")]
		public List<string> Permissions { get; set; }

		[JsonIgnore]
		public string RawResponse { get; set; }
	}

	public class InstagramLongLivedToken
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("token_type")]
		public string TokenType { get; set; }

		[JsonProperty("expires_in")]
		public int ExpiresIn { get; set; }

		[JsonIgnore]
		public string RawResponse { get; set; }
	}

	// Models/InstagramAuthModels.cs
	public class InstagramAuthState
	{
		[JsonProperty("user_id")]
		public string UserId { get; set; }

		[JsonProperty("provider")]
		public string Provider { get; set; }

		[JsonProperty("token")]
		public string Token { get; set; }

		[JsonProperty("callback_url")]
		public string CallbackUrl { get; set; }

		[JsonProperty("messages_callback")]
		public string MessagesCallback { get; set; }
	}

	public class InstagramTokenResponse
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("user_id")]
		public long UserId { get; set; }

		[JsonProperty("expires_in")]
		public int ExpiresIn { get; set; }
	}

	#endregion
}
