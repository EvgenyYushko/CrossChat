using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class HomeController : ControllerBase
	{
		[HttpGet]
		public ContentResult Index()
		{
			string html = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Вход через Instagram</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
        }
        
        .container {
            text-align: center;
            background: white;
            padding: 40px;
            border-radius: 20px;
            box-shadow: 0 20px 40px rgba(0,0,0,0.1);
            max-width: 400px;
            width: 90%;
        }
        
        h1 {
            color: #333;
            margin-bottom: 20px;
            font-size: 24px;
        }
        
        p {
            color: #666;
            margin-bottom: 30px;
            line-height: 1.5;
        }
        
        .instagram-btn {
            background: linear-gradient(45deg, #f09433 0%, #e6683c 25%, #dc2743 50%, #cc2366 75%, #bc1888 100%);
            color: white;
            border: none;
            padding: 12px 30px;
            font-size: 18px;
            border-radius: 50px;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
            transition: transform 0.2s, box-shadow 0.2s;
            border: 1px solid rgba(255,255,255,0.3);
            font-weight: 600;
        }
        
        .instagram-btn:hover {
            transform: scale(1.05);
            box-shadow: 0 10px 20px rgba(0,0,0,0.2);
        }
        
        .instagram-btn img {
            width: 24px;
            height: 24px;
            filter: brightness(0) invert(1);
        }
        
        .footer {
            margin-top: 30px;
            font-size: 12px;
            color: #999;
        }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Добро пожаловать!</h1>
        <p>Подключите Instagram чтобы продолжить</p>
        
        <!-- КНОПКА ВХОДА ЧЕРЕЗ INSTAGRAM -->
        <button onclick=""window.location.href='https://api.instagram.com/oauth/authorize?client_id=1660493108654598&redirect_uri=localhost/instagram/auth/callback&scope=user_profile,user_media&response_type=code'"" 
                class=""instagram-btn"">
            <span>Войти через Instagram</span>
        </button>
        
        <div class=""footer"">
            Нажимая кнопку, вы соглашаетесь с условиями использования
        </div>
    </div>
</body>
</html>";
			return Content(html, "text/html");
		}
	}
}
