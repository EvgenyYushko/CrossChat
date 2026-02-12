using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
	public class HomeController : ControllerBase
	{
		private readonly ILogger<HomeController> _logger;

		public HomeController(ILogger<HomeController> logger)
		{
			_logger = logger;
		}

		[HttpGet("")]
		[HttpGet("index")]
		public ContentResult Index()
		{
			string html = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>CrossChat</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }

        .navbar {
            background: rgba(255, 255, 255, 0.1);
            backdrop-filter: blur(10px);
            padding: 1rem 2rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-bottom: 1px solid rgba(255, 255, 255, 0.2);
        }

        .logo {
            font-size: 1.5rem;
            font-weight: bold;
            color: white;
            text-decoration: none;
        }

        .nav-links a {
            color: white;
            text-decoration: none;
            margin-left: 2rem;
            opacity: 0.9;
            transition: opacity 0.2s;
        }

        .nav-links a:hover {
            opacity: 1;
        }

        .container {
            flex: 1;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            padding: 2rem;
            text-align: center;
        }

        .hero {
            max-width: 800px;
            margin-bottom: 3rem;
        }

        h1 {
            font-size: 3rem;
            margin-bottom: 1rem;
            font-weight: 700;
        }

        .subtitle {
            font-size: 1.25rem;
            opacity: 0.95;
            margin-bottom: 2rem;
            line-height: 1.6;
        }

        .features {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
            gap: 2rem;
            margin: 3rem 0;
            width: 100%;
            max-width: 1000px;
        }

        .feature {
            background: rgba(255, 255, 255, 0.1);
            padding: 2rem;
            border-radius: 20px;
            backdrop-filter: blur(10px);
            border: 1px solid rgba(255, 255, 255, 0.2);
            transition: transform 0.2s;
        }

        .feature:hover {
            transform: translateY(-5px);
            background: rgba(255, 255, 255, 0.15);
        }

        .feature h3 {
            font-size: 1.5rem;
            margin-bottom: 1rem;
        }

        .feature p {
            opacity: 0.9;
            line-height: 1.5;
        }

        .btn {
            display: inline-block;
            padding: 1rem 2.5rem;
            font-size: 1.125rem;
            font-weight: 600;
            text-decoration: none;
            border-radius: 50px;
            transition: all 0.2s;
            margin: 0.5rem;
            cursor: pointer;
            border: none;
        }

        .btn-primary {
            background: white;
            color: #667eea;
            box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
        }

        .btn-primary:hover {
            transform: scale(1.05);
            box-shadow: 0 6px 12px rgba(0, 0, 0, 0.15);
        }

        .btn-outline {
            background: transparent;
            color: white;
            border: 2px solid white;
        }

        .btn-outline:hover {
            background: white;
            color: #667eea;
        }

        .footer {
            background: rgba(0, 0, 0, 0.2);
            padding: 2rem;
            text-align: center;
            font-size: 0.875rem;
            opacity: 0.9;
        }

        @media (max-width: 768px) {
            h1 { font-size: 2rem; }
            .navbar { flex-direction: column; gap: 1rem; }
            .nav-links a { margin: 0 1rem; }
            .features { grid-template-columns: 1fr; }
        }
    </style>
</head>
<body>
    <nav class=""navbar"">
        <a href=""/"" class=""logo"">CrossChat</a>
        <div class=""nav-links"">
            <a href=""/"">Главная</a>
            <a href=""/instagram"">Instagram</a>
            <a href=""#"">О проекте</a>
        </div>
    </nav>

    <div class=""container"">
        <div class=""hero"">
            <h1>Управляйте Instagram аккаунтами</h1>
            <p class=""subtitle"">
                Единая панель управления для ваших Instagram аккаунтов. 
                Аналитика, контент, сообщения — всё в одном месте.
            </p>
            
            <!-- КНОПКА ПЕРЕХОДА В НАСТРОЙКИ INSTAGRAM -->
            <a href=""/instagram"" class=""btn btn-primary"">
                Настройки Instagram
            </a>
            <a href=""#"" class=""btn btn-outline"">
                Узнать больше
            </a>
        </div>

        <div class=""features"">
            <div class=""feature"">
                <h3>📊 Аналитика</h3>
                <p>Детальная статистика по аккаунтам: охваты, вовлеченность, рост подписчиков</p>
            </div>
            <div class=""feature"">
                <h3>📝 Контент</h3>
                <p>Планирование постов, управление публикациями, автоматический постинг</p>
            </div>
            <div class=""feature"">
                <h3>💬 Сообщения</h3>
                <p>Единый inbox для всех диалогов, быстрые ответы, автоответчики</p>
            </div>
        </div>
    </div>

    <footer class=""footer"">
        <p>© 2026 CrossChat. Все права защищены.</p>
        <p style=""margin-top: 0.5rem; font-size: 0.75rem;"">
            Работает с Instagram Basic Display API
        </p>
    </footer>
</body>
</html>";

			return Content(html, "text/html");
		}
	}
}
