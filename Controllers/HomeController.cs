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
			// Убедитесь, что пути правильные (как мы настроили ранее)
			// Если используете wwwroot:
			string logoUrl = "/images/CrossChatLogo.jpeg";
			string platformImageUrl = "/images/CrossChat.jpeg";

			// Если используете Resources (как во 2-м варианте решения):
			// string logoUrl = "/Resources/Images/CrossChatLogo.jpeg";
			// string platformImageUrl = "/Resources/Images/CrossChat.jpeg";

			string html = $@"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>CrossChat — Connect. Prompt. Respond.</title>
    <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
    <link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
    <link href=""https://fonts.googleapis.com/css2?family=Inter:wght@300;400;600;800&display=swap"" rel=""stylesheet"">
    <style>
        :root {{
            --bg-color: #050505; /* Глубокий черный фон */
            --text-main: #ffffff;
            --text-secondary: #a1a1aa;
            --primary-gradient: linear-gradient(135deg, #6366f1 0%, #a855f7 50%, #ec4899 100%); /* Indigo -> Purple -> Pink */
            --glass-bg: rgba(255, 255, 255, 0.03);
            --glass-border: rgba(255, 255, 255, 0.1);
            --glass-blur: blur(20px);
        }}

        * {{
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }}

        body {{
            font-family: 'Inter', sans-serif;
            background-color: var(--bg-color);
            color: var(--text-main);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            overflow-x: hidden;
        }}

        /* Фоновые пятна (Glow effects) */
        body::before {{
            content: '';
            position: absolute;
            top: -10%;
            left: 20%;
            width: 600px;
            height: 600px;
            background: radial-gradient(circle, rgba(99, 102, 241, 0.15) 0%, rgba(0,0,0,0) 70%);
            z-index: -1;
            pointer-events: none;
        }}

        /* --- NAVBAR --- */
        .navbar {{
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            padding: 1rem 2rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            background: rgba(5, 5, 5, 0.7);
            backdrop-filter: blur(15px);
            border-bottom: 1px solid var(--glass-border);
            z-index: 1000;
        }}

        .logo-container {{
            display: flex;
            align-items: center;
            text-decoration: none;
            gap: 12px;
        }}

        .nav-logo-img {{
            height: 40px;
            width: 40px;
            border-radius: 10px;
            object-fit: cover;
        }}

        .logo-text {{
            font-size: 1.25rem;
            font-weight: 700;
            color: white;
            letter-spacing: -0.02em;
        }}

        .nav-links {{
            display: flex;
            gap: 2rem;
        }}

        .nav-links a {{
            color: var(--text-secondary);
            text-decoration: none;
            font-size: 0.95rem;
            font-weight: 500;
            transition: color 0.3s;
        }}

        .nav-links a:hover {{
            color: white;
        }}

        /* --- HERO SECTION --- */
        .container {{
            flex: 1;
            display: flex;
            flex-direction: column;
            align-items: center;
            padding: 8rem 2rem 4rem; /* Отступ сверху для навбара */
            text-align: center;
            max-width: 1200px;
            margin: 0 auto;
            width: 100%;
        }}

        .hero h1 {{
            font-size: 4rem;
            font-weight: 800;
            line-height: 1.1;
            margin-bottom: 1.5rem;
            letter-spacing: -0.03em;
        }}

        .gradient-text {{
            background: var(--primary-gradient);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
        }}

        .hero p {{
            font-size: 1.25rem;
            color: var(--text-secondary);
            max-width: 600px;
            margin: 0 auto 2.5rem;
            line-height: 1.6;
        }}

        /* --- BUTTONS --- */
        .btn {{
            display: inline-flex;
            align-items: center;
            justify-content: center;
            padding: 1rem 2.5rem;
            font-size: 1rem;
            font-weight: 600;
            text-decoration: none;
            border-radius: 12px;
            transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
            margin: 0 0.5rem;
            border: 1px solid transparent;
        }}

        .btn-primary {{
            background: white;
            color: black;
        }}

        .btn-primary:hover {{
            background: #e2e2e2;
            transform: translateY(-2px);
            box-shadow: 0 10px 20px rgba(255, 255, 255, 0.1);
        }}

        .btn-outline {{
            background: rgba(255, 255, 255, 0.05);
            color: white;
            border: 1px solid var(--glass-border);
        }}

        .btn-outline:hover {{
            background: rgba(255, 255, 255, 0.1);
            border-color: rgba(255, 255, 255, 0.3);
        }}

        /* --- PLATFORM PREVIEW (IMAGE) --- */
        .preview-wrapper {{
            margin-top: 4rem;
            position: relative;
            width: 100%;
            max-width: 1000px;
        }}

        /* Свечение за картинкой */
        .preview-glow {{
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            width: 100%;
            height: 100%;
            background: var(--primary-gradient);
            filter: blur(80px);
            opacity: 0.3;
            z-index: -1;
            border-radius: 50%;
        }}

        .platform-preview {{
            border-radius: 16px;
            overflow: hidden;
            border: 1px solid var(--glass-border);
            background: rgba(20, 20, 20, 0.6);
            backdrop-filter: blur(10px);
            padding: 8px; /* Рамка вокруг скриншота */
            box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);
            transform: perspective(1000px) rotateX(2deg); /* Легкий 3D эффект */
            transition: transform 0.5s ease;
        }}

        .platform-preview:hover {{
            transform: perspective(1000px) rotateX(0deg) scale(1.01);
        }}

        .platform-preview img {{
            width: 100%;
            height: auto;
            display: block;
            border-radius: 10px;
        }}

        /* --- FEATURES GRID --- */
        .features {{
            display: grid;
            grid-template-columns: repeat(3, 1fr);
            gap: 2rem;
            margin-top: 6rem;
            width: 100%;
        }}

        .feature {{
            background: var(--glass-bg);
            border: 1px solid var(--glass-border);
            padding: 2rem;
            border-radius: 20px;
            transition: 0.3s;
            text-align: left;
        }}

        .feature:hover {{
            background: rgba(255, 255, 255, 0.06);
            border-color: rgba(255, 255, 255, 0.2);
            transform: translateY(-5px);
        }}

        .feature-icon {{
            font-size: 2rem;
            margin-bottom: 1rem;
            display: inline-block;
        }}

        .feature h3 {{
            font-size: 1.25rem;
            margin-bottom: 0.5rem;
            color: white;
        }}

        .feature p {{
            color: var(--text-secondary);
            font-size: 0.95rem;
            line-height: 1.5;
        }}

        /* --- FOOTER --- */
        .footer {{
            border-top: 1px solid var(--glass-border);
            padding: 3rem 2rem;
            text-align: center;
            color: var(--text-secondary);
            font-size: 0.875rem;
            background: #020202;
        }}

        @media (max-width: 900px) {{
            .hero h1 {{ font-size: 2.5rem; }}
            .features {{ grid-template-columns: 1fr; }}
            .navbar {{ padding: 1rem; }}
        }}
    </style>
</head>
<body>
    <!-- Navigation -->
    <nav class=""navbar"">
        <a href=""/"" class=""logo-container"">
            <img src=""{logoUrl}"" alt=""Logo"" class=""nav-logo-img"">
            <span class=""logo-text"">CrossChat</span>
        </a>
        <div class=""nav-links"">
            <a href=""/"">Главная</a>
            <a href=""/instagram"">Интеграция</a>
            <a href=""#"">API</a>
        </div>
    </nav>

    <!-- Main Content -->
    <div class=""container"">
        
        <!-- Hero Section -->
        <div class=""hero"">
            <h1>
                Единый центр <br>
                <span class=""gradient-text"">управления диалогами</span>
            </h1>
            <p>
                Подключите Instagram Business API за секунды. 
                Управляйте сообщениями, контентом и аналитикой в одном месте.
            </p>
            
            <div class=""buttons"">
                <a href=""/instagram"" class=""btn btn-primary"">Подключить Instagram</a>
                <a href=""#"" class=""btn btn-outline"">Документация</a>
            </div>
        </div>

        <!-- Platform Screenshot with Glow -->
        <div class=""preview-wrapper"">
            <div class=""preview-glow""></div>
            <div class=""platform-preview"">
                <img src=""{platformImageUrl}"" alt=""CrossChat Dashboard Interface"">
            </div>
        </div>

        <!-- Features Grid -->
        <div class=""features"">
            <div class=""feature"">
                <span class=""feature-icon"">⚡</span>
                <h3>Мгновенное подключение</h3>
                <p>Используем официальный Graph API. Никаких серых схем, полная безопасность данных.</p>
            </div>
            <div class=""feature"">
                <span class=""feature-icon"">📊</span>
                <h3>Глубокая аналитика</h3>
                <p>Отслеживайте рост подписчиков, охваты сторис и вовлеченность аудитории в реальном времени.</p>
            </div>
            <div class=""feature"">
                <span class=""feature-icon"">🤖</span>
                <h3>Автоматизация</h3>
                <p>Настраивайте автоответы и сценарии общения. Экономьте время операторов поддержки.</p>
            </div>
        </div>
    </div>

    <!-- Footer -->
    <footer class=""footer"">
        <p>&copy; {DateTime.Now.Year} CrossChat Inc. Все права защищены.</p>
        <p style=""margin-top: 10px; opacity: 0.6;"">
            Designed for Instagram Business API
        </p>
    </footer>
</body>
</html>";

			return Content(html, "text/html");
		}
	}
}
