using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("privacy")]
	public class PrivacyController : ControllerBase
	{
		private readonly ILogger<PrivacyController> _logger;

		public PrivacyController(ILogger<PrivacyController> logger)
		{
			_logger = logger;
		}

		[HttpGet("")]
		[HttpGet("policy")]
		public ContentResult Index()
		{
			string html = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Политика конфиденциальности | CrossChat</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: #333;
            line-height: 1.6;
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
            max-width: 1000px;
            margin: 40px auto;
            padding: 0 20px;
        }

        .privacy-card {
            background: white;
            border-radius: 30px;
            padding: 50px;
            box-shadow: 0 30px 60px rgba(0,0,0,0.1);
        }

        h1 {
            font-size: 2.5rem;
            color: #2d3748;
            margin-bottom: 20px;
            font-weight: 700;
        }

        .last-updated {
            color: #718096;
            font-size: 0.9rem;
            margin-bottom: 40px;
            padding-bottom: 20px;
            border-bottom: 2px solid #e2e8f0;
        }

        h2 {
            color: #4a5568;
            font-size: 1.5rem;
            margin-top: 40px;
            margin-bottom: 20px;
            font-weight: 600;
        }

        h3 {
            color: #2d3748;
            font-size: 1.2rem;
            margin-top: 30px;
            margin-bottom: 15px;
            font-weight: 600;
        }

        p {
            color: #4a5568;
            margin-bottom: 20px;
            font-size: 1rem;
        }

        ul, ol {
            margin-bottom: 20px;
            padding-left: 30px;
            color: #4a5568;
        }

        li {
            margin-bottom: 10px;
        }

        .highlight {
            background: #ebf4ff;
            padding: 20px;
            border-radius: 12px;
            border-left: 4px solid #667eea;
            margin: 30px 0;
        }

        .footer {
            background: rgba(0, 0, 0, 0.2);
            padding: 2rem;
            text-align: center;
            color: white;
            margin-top: 60px;
        }

        .footer a {
            color: white;
            text-decoration: underline;
            opacity: 0.9;
        }

        .footer a:hover {
            opacity: 1;
        }

        @media (max-width: 768px) {
            .navbar {
                flex-direction: column;
                gap: 1rem;
                padding: 1rem;
            }
            
            .nav-links a {
                margin: 0 0.5rem;
            }
            
            .privacy-card {
                padding: 30px;
            }
            
            h1 {
                font-size: 2rem;
            }
        }
    </style>
</head>
<body>
    <nav class=""navbar"">
        <a href=""/"" class=""logo"">CrossChat</a>
        <div class=""nav-links"">
            <a href=""/"">Главная</a>
            <a href=""/instagram"">Instagram</a>
            <a href=""/privacy"">Конфиденциальность</a>
        </div>
    </nav>

    <div class=""container"">
        <div class=""privacy-card"">
            <h1>Политика конфиденциальности</h1>
            <div class=""last-updated"">
                Последнее обновление: 12 февраля 2026 года
            </div>

            <div class=""highlight"">
                <strong>Важно:</strong> CrossChat использует Instagram Basic Display API 
                для предоставления функциональности управления Instagram аккаунтами. 
                Мы серьезно относимся к защите ваших данных.
            </div>

            <h2>1. Какую информацию мы собираем</h2>
            <p>При использовании нашего приложения через Instagram Basic Display API мы можем собирать:</p>
            <ul>
                <li><strong>Публичную информацию профиля:</strong> Instagram ID, имя пользователя, имя профиля</li>
                <li><strong>Медиафайлы:</strong> Фотографии и видео из вашего аккаунта (только с вашего явного согласия)</li>
                <li><strong>Токены доступа:</strong> Временные ключи для доступа к вашему аккаунту через API</li>
                <li><strong>Email адрес:</strong> Если вы предоставили его в настройках профиля</li>
            </ul>

            <h2>2. Как мы используем вашу информацию</h2>
            <ul>
                <li>Для предоставления функциональности управления Instagram аккаунтом</li>
                <li>Для отображения вашего контента в интерфейсе приложения</li>
                <li>Для технической поддержки и улучшения сервиса</li>
                <li>Никогда не продаем ваши данные третьим лицам</li>
            </ul>

            <h2>3. Хранение и защита данных</h2>
            <ul>
                <li>Все токены доступа хранятся в зашифрованном виде</li>
                <li>Мы используем HTTPS шифрование для всех передаваемых данных</li>
                <li>Срок жизни токенов: 60 дней (долгоживущие токены)</li>
                <li>Вы можете в любой момент отозвать доступ в настройках Instagram</li>
            </ul>

            <h2>4. Instagram Basic Display API</h2>
            <p>Наше приложение работает в строгом соответствии с политиками Meta Platforms, Inc.:</p>
            <ul>
                <li>Мы запрашиваем только минимально необходимые разрешения (user_profile, user_media)</li>
                <li>Мы не запрашиваем доступ к direct messages через Basic Display API</li>
                <li>Мы не храним данные дольше необходимого срока</li>
                <li>Мы не передаем данные третьим лицам без вашего согласия</li>
            </ul>

            <div class=""highlight"">
                <strong>Для бизнес-аккаунтов:</strong> Instagram Basic Display API работает только с 
                личными аккаунтами. Для бизнес-аккаунтов Instagram требуется Instagram Graph API, 
                который в текущей версии приложения не используется.
            </div>

            <h2>5. Ваши права</h2>
            <p>Вы имеете право:</p>
            <ul>
                <li>Запросить полную информацию о хранящихся данных</li>
                <li>Потребовать удаления всех ваших данных</li>
                <li>Отозвать доступ приложения через настройки Instagram</li>
                <li>Получить копию ваших данных в машиночитаемом формате</li>
            </ul>

            <h2>6. Удаление данных</h2>
            <p>Чтобы удалить все ваши данные из нашего сервиса:</p>
            <ol>
                <li>Перейдите в настройки Instagram → Приложения и сайты</li>
                <li>Найдите ""CrossChat"" в списке активных приложений</li>
                <li>Нажмите ""Удалить""</li>
            </ol>
            <p>Автоматическое удаление данных происходит через 90 дней после отзыва доступа.</p>

            <h2>7. Cookies и технические данные</h2>
            <p>Мы используем минимально необходимый набор cookies для:</p>
            <ul>
                <li>Поддержания сессии пользователя</li>
                <li>Защиты от CSRF атак</li>
                <li>Анализа ошибок и производительности</li>
            </ul>

            <h2>8. Изменения политики конфиденциальности</h2>
            <p>
                Мы оставляем за собой право обновлять данную политику конфиденциальности. 
                При существенных изменениях мы уведомим пользователей через интерфейс приложения 
                или по электронной почте (при наличии).
            </p>

            <h2>9. Контактная информация</h2>
            <p>
                По всем вопросам, связанным с конфиденциальностью и обработкой данных:
            </p>
            <ul>
                <li><strong>Email:</strong> privacy@alikrossmanager.com</li>
                <li><strong>Адрес:</strong> [Ваш юридический адрес]</li>
                <li><strong>Представитель:</strong> [Имя контактного лица]</li>
            </ul>

            <div style=""margin-top: 50px; padding-top: 30px; border-top: 2px solid #e2e8f0; text-align: center; color: #718096;"">
                <p style=""font-size: 0.9rem;"">
                    Это базовая страница конфиденциальности для Instagram Basic Display API. 
                    Для прохождения ревью Facebook может потребоваться более детальная политика.
                </p>
            </div>
        </div>
    </div>

    <footer class=""footer"">
        <p>© 2026 CrossChat Manager. Все права защищены.</p>
        <p style=""margin-top: 0.5rem;"">
            <a href=""/"">Главная</a> • 
            <a href=""/instagram"">Instagram</a> • 
            <a href=""/privacy"">Политика конфиденциальности</a>
        </p>
    </footer>
</body>
</html>";

			return Content(html, "text/html");
		}
	}
}
