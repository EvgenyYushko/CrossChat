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
			string logoUrl = "/images/CrossChatLogo.jpeg";
			string platformImageUrl = "/images/CrossChat.jpeg";

			string html = $@"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>CrossChat — Connect. Prompt. Respond.</title>
    <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@300;400;600;800&display=swap"" rel=""stylesheet"">
    <style>
        :root {{
            --bg-color: #050505;
            --text-main: #ffffff;
            --text-secondary: #a1a1aa;
            --primary-gradient: linear-gradient(135deg, #6366f1 0%, #a855f7 50%, #ec4899 100%);
            --glass-bg: rgba(255, 255, 255, 0.03);
            --glass-border: rgba(255, 255, 255, 0.1);
        }}

        * {{ margin: 0; padding: 0; box-sizing: border-box; }}

        body {{
            font-family: 'Inter', sans-serif;
            background-color: var(--bg-color);
            color: var(--text-main);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            overflow-x: hidden; /* Блокируем скролл вбок */
            position: relative;
        }}

        /* Фоновое свечение */
        body::before {{
            content: '';
            position: absolute;
            top: -10%; left: 50%; transform: translateX(-50%);
            width: 80vw; height: 60vh;
            background: radial-gradient(circle, rgba(99, 102, 241, 0.2) 0%, rgba(0,0,0,0) 70%);
            z-index: -1; pointer-events: none;
        }}

        /* --- NAVBAR --- */
        .navbar {{
            position: fixed;
            top: 0; left: 0; width: 100%;
            padding: 1rem 2rem;
            display: flex;
            justify-content: flex-start; /* Логотип СЛЕВА */
            align-items: center;
            background: rgba(5, 5, 5, 0.8);
            backdrop-filter: blur(15px);
            border-bottom: 1px solid var(--glass-border);
            z-index: 1000;
        }}

        .logo-container {{ display: flex; align-items: center; text-decoration: none; gap: 12px; }}
        .nav-logo-img {{ height: 36px; width: 36px; border-radius: 8px; object-fit: cover; }}
        .logo-text {{ font-size: 1.2rem; font-weight: 700; color: white; letter-spacing: -0.02em; }}

        /* --- CONTAINER --- */
        .container {{
            flex: 1; display: flex; flex-direction: column; align-items: center;
            padding: 7rem 1.5rem 4rem; text-align: center;
            max-width: 1200px; margin: 0 auto; width: 100%;
        }}

        /* --- 1. КАРТИНКА (ВВЕРХУ) --- */
        .preview-wrapper {{
            position: relative; width: 100%; max-width: 900px;
            margin-bottom: 3rem; /* Отступ до текста */
        }}

        .preview-glow {{
            position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%);
            width: 100%; height: 100%;
            background: var(--primary-gradient); filter: blur(60px); opacity: 0.25;
            z-index: -1; border-radius: 50%;
        }}

        .platform-preview {{
            border-radius: 16px; overflow: hidden;
            border: 1px solid var(--glass-border);
            background: rgba(20, 20, 20, 0.6);
            backdrop-filter: blur(10px);
            padding: 6px;
            box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);
        }}

        .platform-preview img {{ width: 100%; height: auto; display: block; border-radius: 10px; }}

        /* --- 2. ТЕКСТ (СНИЗУ) --- */
        .hero h1 {{ font-size: 3.5rem; font-weight: 800; line-height: 1.1; margin-bottom: 1.5rem; }}
        .gradient-text {{
            background: var(--primary-gradient);
            -webkit-background-clip: text; -webkit-text-fill-color: transparent;
        }}
        .hero p {{
            font-size: 1.2rem; color: var(--text-secondary);
            max-width: 600px; margin: 0 auto 2.5rem; line-height: 1.6;
        }}

        /* КНОПКИ */
        .buttons {{ display: flex; justify-content: center; flex-wrap: wrap; gap: 1rem; }}
        
        .btn {{
            display: inline-flex; align-items: center; justify-content: center;
            padding: 1rem 2rem; font-size: 1rem; font-weight: 600;
            text-decoration: none; border-radius: 12px;
            transition: all 0.2s; border: 1px solid transparent;
        }}

        .btn-primary {{ background: white; color: black; }}
        .btn-primary:hover {{ background: #e2e2e2; transform: translateY(-2px); }}

        .btn-outline {{ background: rgba(255, 255, 255, 0.05); color: white; border-color: var(--glass-border); }}
        .btn-outline:hover {{ background: rgba(255, 255, 255, 0.1); border-color: rgba(255, 255, 255, 0.3); }}

        /* ОСОБЕННОСТИ */
        .features {{
            display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
            gap: 1.5rem; margin-top: 4rem; width: 100%;
        }}
        .feature {{
            background: var(--glass-bg); border: 1px solid var(--glass-border);
            padding: 2rem; border-radius: 20px; text-align: left;
        }}
        .feature h3 {{ color: white; margin-bottom: 0.5rem; font-size: 1.2rem; }}
        .feature p {{ color: var(--text-secondary); line-height: 1.5; font-size: 0.95rem; }}
        .feature-icon {{ font-size: 2rem; margin-bottom: 1rem; display: block; }}

        @media (max-width: 768px) {{
            .hero h1 {{ font-size: 2.2rem; }}
            .container {{ padding-top: 6rem; }}
            .btn {{ width: 100%; }}
        }}
    </style>
</head>
<body>
    <nav class=""navbar"">
        <a href=""/"" class=""logo-container"">
            <img src=""{logoUrl}"" alt=""Logo"" class=""nav-logo-img"">
            <span class=""logo-text"">CrossChat</span>
        </a>
    </nav>

    <div class=""container"">
        <!-- 1. Картинка -->
        <div class=""preview-wrapper"">
            <div class=""preview-glow""></div>
            <div class=""platform-preview"">
                <img src=""{platformImageUrl}"" alt=""CrossChat Dashboard"">
            </div>
        </div>

        <!-- 2. Текст -->
        <div class=""hero"">
            <h1>
                Единый центр <br>
                <span class=""gradient-text"">управления диалогами</span>
            </h1>
            <p>
                Подключите Instagram Business API за секунды. 
                Автоответы, аналитика и управление контентом в одном месте.
            </p>
            
            <div class=""buttons"">
                {(User.Identity?.IsAuthenticated == true
							? @"<a href=""/auth/profile"" class=""btn btn-primary"">Открыть кабинет</a>"
							: @"<a href=""/auth/login"" class=""btn btn-primary"">Начать бесплатно</a>")}
            </div>
        </div>

        <div class=""features"">
            <div class=""feature"">
                <span class=""feature-icon"">⚡</span>
                <h3>Быстрый старт</h3>
                <p>Официальный API Meta. Никаких банов, полная безопасность.</p>
            </div>
            <div class=""feature"">
                <span class=""feature-icon"">🤖</span>
                <h3>AI Автоответы</h3>
                <p>Gemini отвечает клиентам по вашему сценарию 24/7.</p>
            </div>
            <div class=""feature"">
                <span class=""feature-icon"">📈</span>
                <h3>Аналитика</h3>
                <p>Следите за вовлеченностью и ростом аудитории.</p>
            </div>
        </div>
    </div>
</body>
</html>";

			return Content(html, "text/html");
		}
	}
}
