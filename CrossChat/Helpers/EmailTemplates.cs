using static CrossChat.Helpers.TimeZoneHelper;

namespace CrossChat.Helpers
{
	public static class EmailTemplates
	{
		public static string GetHtml(string userName, string userEmail, string loginUrl, string logoUrl)
{
    return $@"
<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Добро пожаловать в CrossChat</title>
    <link href=""https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700&display=swap"" rel=""stylesheet"">
</head>
<body style=""margin: 0; padding: 0; background-color: #f8fafc; font-family: 'Plus Jakarta Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">

<table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""background-color: #f8fafc; padding: 40px 0;"">
    <tr>
        <td align=""center"">
            <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""600"" style=""background-color: #ffffff; border-radius: 24px; box-shadow: 0 20px 40px rgba(15, 23, 42, 0.04); border: 1px solid #e2e8f0; overflow: hidden;"">
                
                <!-- Hero Logo Section -->
                <tr>
                    <td style=""background-color: #ffffff; padding: 0; text-align: center;"">
                        <img src=""{logoUrl}"" 
                             alt=""CrossChat"" 
                             style=""width: 600px; height: auto; display: block; margin: 0 auto; border: 0;""
                        />
                    </td>
                </tr>
                
                <!-- Приветствие -->
                <tr>
                    <td style=""padding: 48px 48px 12px 48px; text-align: center;"">
                        <div style=""font-size: 48px; margin-bottom: 16px;"">
                            🎉
                        </div>
                        <h1 style=""color: #0f172a; font-size: 32px; font-weight: 700; margin: 0 0 8px 0; letter-spacing: -0.02em; line-height: 1.2;"">
                            Добро пожаловать <br>в <span style=""color: #4f46e5;"">CrossChat</span>!
                        </h1>
                        <p style=""color: #64748b; font-size: 16px; margin: 0; font-weight: 500;"">
                            Ваш аккаунт успешно создан ✨
                        </p>
                    </td>
                </tr>
                
                <!-- Разделитель -->
                <tr>
                    <td style=""padding: 24px 48px 0 48px;"">
                        <div style=""border-top: 1px solid #f1f5f9;""></div>
                    </td>
                </tr>
                
                <!-- Основной контент -->
                <tr>
                    <td style=""padding: 32px 48px 40px 48px;"">
                        
                        <!-- Greeting -->
                        <h2 style=""color: #0f172a; font-size: 20px; font-weight: 700; margin: 0 0 14px 0; letter-spacing: -0.01em;"">
                            Здравствуйте, {userName}! 👋
                        </h2>
                        
                        <p style=""color: #334155; font-size: 15px; line-height: 1.6; margin: 0 0 24px 0;"">
                            Мы рады приветствовать вас в <strong style=""color: #4f46e5; font-weight: 600;"">CrossChat</strong> — 
                            сервисе умных автоответов для социальных сетей! 
                            Теперь вы можете автоматизировать общение с вашей аудиторией и никогда не пропускать важные сообщения.
                        </p>
                        
                        <!-- Account Info Box -->
                        <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""background-color: #f8fafc; border-radius: 12px; border: 1px solid #e2e8f0; border-left: 4px solid #4f46e5; margin-bottom: 32px;"">
                            <tr>
                                <td style=""padding: 16px 20px;"">
                                    <p style=""color: #64748b; font-size: 11px; margin: 0 0 4px 0; text-transform: uppercase; letter-spacing: 0.05em; font-weight: 600;"">
                                        📧 Ваш email для входа
                                    </p>
                                    <p style=""color: #0f172a; font-size: 15px; font-weight: 600; margin: 0;"">
                                        {userEmail}
                                    </p>
                                </td>
                            </tr>
                        </table>
                        
                        <!-- Features Grid -->
                        <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""margin-bottom: 32px;"">
                            <tr>
                                <td width=""48%"" style=""padding: 20px; background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 16px; vertical-align: top;"">
                                    <div style=""font-size: 28px; margin-bottom: 12px;"">🔄</div>
                                    <h3 style=""color: #0f172a; font-size: 15px; margin: 0 0 6px 0; font-weight: 700;"">
                                        Автоответы
                                    </h3>
                                    <p style=""color: #475569; font-size: 13px; line-height: 1.5; margin: 0;"">
                                        Настройте автоматические ответы для любых соцсетей
                                    </p>
                                </td>
                                <td width=""4%""></td>
                                <td width=""48%"" style=""padding: 20px; background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 16px; vertical-align: top;"">
                                    <div style=""font-size: 28px; margin-bottom: 12px;"">🔌</div>
                                    <h3 style=""color: #0f172a; font-size: 15px; margin: 0 0 6px 0; font-weight: 700;"">
                                        Подключение соцсетей
                                    </h3>
                                    <p style=""color: #475569; font-size: 13px; line-height: 1.5; margin: 0;"">
                                        Поддерживаем почти любые популярные платформы
                                    </p>
                                </td>
                            </tr>
                            <tr>
                                <td colspan=""3"" style=""height: 16px;""></td>
                            </tr>
                            <tr>
                                <td width=""48%"" style=""padding: 20px; background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 16px; vertical-align: top;"">
                                    <div style=""font-size: 28px; margin-bottom: 12px;"">⚡</div>
                                    <h3 style=""color: #0f172a; font-size: 15px; margin: 0 0 6px 0; font-weight: 700;"">
                                        Мгновенная реакция
                                    </h3>
                                    <p style=""color: #475569; font-size: 13px; line-height: 1.5; margin: 0;"">
                                        Отвечайте за секунды, даже когда вы не в сети
                                    </p>
                                </td>
                                <td width=""4%""></td>
                                <td width=""48%"" style=""padding: 20px; background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 16px; vertical-align: top;"">
                                    <div style=""font-size: 28px; margin-bottom: 12px;"">📊</div>
                                    <h3 style=""color: #0f172a; font-size: 15px; margin: 0 0 6px 0; font-weight: 700;"">
                                        Статистика
                                    </h3>
                                    <p style=""color: #475569; font-size: 13px; line-height: 1.5; margin: 0;"">
                                        Отслеживайте эффективность автоответов в реальном времени
                                    </p>
                                </td>
                            </tr>
                        </table>
                        
                        <!-- CTA Button -->
                        <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"">
                            <tr>
                                <td align=""center"" style=""padding: 12px 0 28px 0;"">
                                    <a href=""{loginUrl}"" 
                                       style=""display: inline-block; 
                                              background-color: #4f46e5; 
                                              color: #ffffff; 
                                              font-size: 16px; 
                                              font-weight: 600; 
                                              text-decoration: none; 
                                              padding: 16px 40px; 
                                              border-radius: 12px; 
                                              letter-spacing: -0.01em;
                                              box-shadow: 0 10px 20px rgba(79, 70, 229, 0.15);"">
                                        🚀 Подключить соцсети
                                    </a>
                                </td>
                            </tr>
                        </table>
                        
                        <!-- Divider -->
                        <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""margin: 8px 0 28px 0;"">
                            <tr>
                                <td style=""border-top: 1px solid #e2e8f0;""></td>
                            </tr>
                        </table>
                        
                        <!-- Steps -->
                        <h3 style=""color: #0f172a; font-size: 16px; margin: 0 0 20px 0; text-align: center; font-weight: 700;"">
                            📋 С чего начать?
                        </h3>
                        
                        <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""margin-bottom: 28px;"">
                            <tr>
                                <td style=""padding: 8px 0;"">
                                    <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"">
                                        <tr>
                                            <td width=""36"" style=""font-size: 20px; vertical-align: top;"">1️⃣</td>
                                            <td style=""color: #334155; font-size: 14px; line-height: 1.5;"">Подключите ваши аккаунты социальных сетей</td>
                                        </tr>
                                    </table>
                                </td>
                            </tr>
                            <tr>
                                <td style=""padding: 8px 0;"">
                                    <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"">
                                        <tr>
                                            <td width=""36"" style=""font-size: 20px; vertical-align: top;"">2️⃣</td>
                                            <td style=""color: #334155; font-size: 14px; line-height: 1.5;"">Создайте сценарии автоответов под ваши задачи</td>
                                        </tr>
                                    </table>
                                </td>
                            </tr>
                            <tr>
                                <td style=""padding: 8px 0;"">
                                    <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"">
                                        <tr>
                                            <td width=""36"" style=""font-size: 20px; vertical-align: top;"">3️⃣</td>
                                            <td style=""color: #334155; font-size: 14px; line-height: 1.5;"">Расслабьтесь — CrossChat отвечает за вас 24/7</td>
                                        </tr>
                                    </table>
                                </td>
                            </tr>
                        </table>
                        
                        <!-- Help Text -->
                        <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 12px; margin-top: 8px;"">
                            <tr>
                                <td style=""padding: 20px; text-align: center;"">
                                    <p style=""color: #475569; font-size: 13px; line-height: 1.6; margin: 0 0 6px 0;"">
                                        💬 Есть вопросы? Наша поддержка всегда рядом:
                                    </p>
                                    <a href=""mailto:support@crosschat.ru"" style=""color: #4f46e5; text-decoration: none; font-weight: 700; font-size: 14px;"">
                                        support@crosschat.ru
                                    </a>
                                </td>
                            </tr>
                        </table>
                        
                    </td>
                </tr>
                
                <!-- Footer -->
                <tr>
                    <td style=""background-color: #0f172a; padding: 40px; text-align: center;"">
                        <p style=""color: #94a3b8; font-size: 13px; margin: 0 0 16px 0; font-weight: 500;"">
                            © {DateTimeNow.Year} CrossChat. Все права защищены.
                        </p>
                        <div style=""font-size: 16px; letter-spacing: 12px; margin-bottom: 16px; opacity: 0.8;"">
                            🔄 🔌 ⚡ 📊
                        </div>
                        <p style=""color: #64748b; font-size: 11px; line-height: 1.4; margin: 0;"">
                            Вы получили это письмо, потому что зарегистрировались на CrossChat.
                        </p>
                    </td>
                </tr>
                
            </table>
        </td>
    </tr>
</table>

</body>
</html>";
}

        public static string GetReauthEmailHtml(string userName, string botName, string socialType, string loginUrl)
        {
            return $@"
            <!DOCTYPE html>
            <html>
            <body style=""margin: 0; padding: 0; background-color: #f8fafc; font-family: sans-serif;"">
                <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""padding: 40px 0;"">
                    <tr>
                        <td align=""center"">
                            <table width=""600"" cellpadding=""0"" cellspacing=""0"" style=""background: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);"">
                                <tr>
                                    <td style=""padding: 40px; text-align: center;"">
                                        <div style=""font-size: 48px; margin-bottom: 20px;"">⚠️</div>
                                        <h1 style=""color: #1a202c; font-size: 24px; margin: 0 0 10px 0;"">Требуется внимание</h1>
                                        <p style=""color: #4a5568; font-size: 16px;"">Привет, <strong>{userName}</strong>!</p>
                                        <p style=""color: #4a5568; font-size: 16px;"">
                                            Ваш бот <strong>{botName}</strong> в сети <strong>{socialType}</strong> перестал отвечать. 
                                            Скорее всего, истек срок действия доступа или сменился пароль.
                                        </p>

                                        <!-- Bulletproof Button -->
                                        <table width=""100%"" cellspacing=""0"" cellpadding=""0"" style=""margin-top: 30px;"">
                                            <tr>
                                                <td align=""center"">
                                                    <a href=""{loginUrl}"" style=""background-color: #6366f1; color: #ffffff; padding: 15px 30px; text-decoration: none; border-radius: 8px; font-weight: bold; display: inline-block;"">
                                                        Переподключить бота
                                                    </a>
                                                </td>
                                            </tr>
                                        </table>

                                        <p style=""color: #a0aec0; font-size: 14px; margin-top: 30px;"">
                                            Если это были не вы, просто проигнорируйте письмо.
                                        </p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>";
        }
	}
}
