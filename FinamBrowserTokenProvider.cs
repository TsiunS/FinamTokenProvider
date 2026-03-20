using PuppeteerSharp;
using System;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.IO;

public class FinamBrowserTokenProvider
{
    public enum TimeFrame
    {
        Ticks, Minutes1, Minutes5, Minutes10, Minutes15, Minutes30,
        Hour1, Hour2, Hour4, Daily, Weekly, Monthly
    }

    public class DownloadParams
    {
        public string Symbol { get; set; } = "SBER";
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public TimeFrame Timeframe { get; set; } = TimeFrame.Hour1;
        public string Format { get; set; } = "txt";
    }

    public async Task<string> GetTokenAsync(DownloadParams parameters = null)
    {
        if (parameters == null)
        {
            parameters = new DownloadParams
            {
                From = DateTime.Now.AddDays(-1),
                To = DateTime.Now,
                Timeframe = TimeFrame.Hour1
            };
        }

        if (parameters.Timeframe == TimeFrame.Ticks && parameters.From.Date != parameters.To.Date)
        {
            Console.WriteLine("ВНИМАНИЕ: Для тиков даты должны совпадать! Устанавливаем одну дату.");
            parameters.To = parameters.From;
        }

        Console.WriteLine("Проверяем наличие браузера...");
        await new BrowserFetcher().DownloadAsync();

        var launchOptions = new LaunchOptions
        {
            Headless = false,
            DefaultViewport = null,
            Args = new[] {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-web-security",
                "--start-maximized",
                "--window-size=1920,1080"
            }
        };

        using var browser = await Puppeteer.LaunchAsync(launchOptions);
        using var page = await browser.NewPageAsync();

        await page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });

        page.DefaultTimeout = 120000;
        page.DefaultNavigationTimeout = 120000;

        await page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");

        // Перехватываем ВСЕ ответы для отладки
        var tcs = new TaskCompletionSource<string>();
        var debugLog = new List<string>();

        page.Response += async (sender, e) =>
        {
            try
            {
                var url = e.Response.Url;
                var status = e.Response.Status;
                var logEntry = $"Статус: {status} URL: {url}";
                Console.WriteLine(logEntry);
                debugLog.Add($"{DateTime.Now:HH:mm:ss} - {logEntry}");

                if (url.Contains("/sessions/token"))
                {
                    if (status == System.Net.HttpStatusCode.OK)
                    {
                        var json = await e.Response.TextAsync();
                        Console.WriteLine($"ТОКЕН ПОЛУЧЕН! {json}");

                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("token", out var tokenElement))
                        {
                            tcs.TrySetResult(tokenElement.GetString());
                        }
                        else
                        {
                            tcs.TrySetResult(json.Trim('"'));
                        }
                    }
                    else if (status == System.Net.HttpStatusCode.Unauthorized)
                    {
                        var errorText = await e.Response.TextAsync();
                        Console.WriteLine($"Ошибка 401: {errorText}");
                        debugLog.Add($"Ошибка 401: {errorText}");
                    }
                }
                else if (url.Contains("/export9.out") || url.Contains(".out"))
                {
                    // Это сам файл с котировками
                    Console.WriteLine($"НАЙДЕН ФАЙЛ: {url}");
                    debugLog.Add($"НАЙДЕН ФАЙЛ: {url}");

                    // Если нашли файл, значит токен был в URL
                    var match = System.Text.RegularExpressions.Regex.Match(url, @"finam_token=([^&]+)");
                    if (match.Success)
                    {
                        var tokenFromUrl = match.Groups[1].Value;
                        Console.WriteLine($"Токен из URL: {tokenFromUrl}");
                        tcs.TrySetResult(tokenFromUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке ответа: {ex.Message}");
            }
        };

        page.Request += (sender, e) =>
        {
            if (e.Request.Url.Contains("/sessions/token") || e.Request.Url.Contains(".out"))
            {
                Console.WriteLine($"Запрос: {e.Request.Method} {e.Request.Url}");
            }
        };

        // Переходим на страницу
        Console.WriteLine("Переходим на страницу экспорта...");

        try
        {
            await page.GoToAsync($"https://www.finam.ru/quote/moex/{parameters.Symbol.ToLower()}/export/",
                WaitUntilNavigation.Networkidle0);
            Console.WriteLine("Страница загружена");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка навигации: {ex.Message}");
        }

        await page.WaitForFunctionAsync(@"() => document.readyState === 'complete'");
        await Task.Delay(3000);

        // Принимаем куки
        Console.WriteLine("Принимаем куки...");
        try
        {
            await page.WaitForFunctionAsync(@"() => {
                const buttons = Array.from(document.querySelectorAll('button, div[role=""button""]'));
                const acceptButton = buttons.find(b => 
                    (b.textContent || '').toLowerCase().includes('принять'));
                if (acceptButton) {
                    acceptButton.click();
                    return true;
                }
                return false;
            }", new WaitForFunctionOptions { Timeout = 10000 });

            Console.WriteLine("Куки приняты");
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Кнопка принятия кук не найдена: {ex.Message}");
        }

        // Сохраняем состояние до заполнения
        await page.ScreenshotAsync("finam-before-submit.png");

        // Заполняем форму
        Console.WriteLine("Заполняем форму...");

        string timeframeValue = GetTimeframeValue(parameters.Timeframe);
        string fromDateIso = parameters.From.ToString("yyyy-MM-dd");
        string toDateIso = parameters.To.ToString("yyyy-MM-dd");

        Console.WriteLine($"Параметры:");
        Console.WriteLine($"  Символ: {parameters.Symbol}");
        Console.WriteLine($"  От: {fromDateIso}");
        Console.WriteLine($"  До: {toDateIso}");
        Console.WriteLine($"  Таймфрейм: {parameters.Timeframe} (value={timeframeValue})");

        // Ждем появления формы
        await page.WaitForFunctionAsync(@"() => {
            return document.querySelector('input[name=""from""]') !== null;
        }", new WaitForFunctionOptions { Timeout = 30000 });

        // Заполняем и отправляем
        await page.EvaluateFunctionAsync($@"
            (from, to, tfValue) => {{
                // Заполняем даты
                const fromInput = document.querySelector('input[name=""from""]');
                const toInput = document.querySelector('input[name=""to""]');
                
                if (fromInput) {{
                    fromInput.value = from;
                    fromInput.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    fromInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
                }}
                
                if (toInput) {{
                    toInput.value = to;
                    toInput.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    toInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
                }}
                
                // Выбираем таймфрейм
                const select = document.querySelector('select[name=""p""]');
                if (select) {{
                    select.value = tfValue;
                    select.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    console.log('Таймфрейм установлен: ' + tfValue);
                }}
                
                // Ищем кнопку отправки
                const buttons = Array.from(document.querySelectorAll('button, input[type=""submit""]'));
                const submitButton = buttons.find(b => {{
                    const text = (b.textContent || b.value || '').toLowerCase();
                    return text.includes('получить') || b.type === 'submit';
                }});
                
                if (submitButton) {{
                    console.log('Нажимаем кнопку:', submitButton.textContent || submitButton.value);
                    
                    // Пробуем разные способы клика
                    submitButton.click();
                    
                    // Если кнопка не сработала, пробуем через событие
                    setTimeout(() => {{
                        const event = new MouseEvent('click', {{
                            view: window,
                            bubbles: true,
                            cancelable: true
                        }});
                        submitButton.dispatchEvent(event);
                    }}, 100);
                }}
            }}", fromDateIso, toDateIso, timeframeValue);

        Console.WriteLine("Форма заполнена, попытка клика выполнена");
        await page.ScreenshotAsync("finam-after-submit.png");

        // Ждем появления загрузки файла или токена
        Console.WriteLine("Ожидаем токен или файл (120 секунд)...");

        try
        {
            var token = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120));
            Console.WriteLine($"Токен успешно получен: {token}");

            await File.WriteAllTextAsync("token.txt", token);
            await File.WriteAllLinesAsync("debug.log", debugLog);

            await browser.CloseAsync();
            return token;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Таймаут - токен не получен за 120 секунд");

            var html = await page.GetContentAsync();
            await File.WriteAllTextAsync("page.html", html);
            await page.ScreenshotAsync("finam-timeout.png");
            await File.WriteAllLinesAsync("debug.log", debugLog);

            // Пробуем найти токен в HTML
            var tokenMatch = System.Text.RegularExpressions.Regex.Match(html, @"finam_token=([^""&\s]+)");
            if (tokenMatch.Success)
            {
                Console.WriteLine($"Найден токен в HTML: {tokenMatch.Groups[1].Value}");
                return tokenMatch.Groups[1].Value;
            }

            throw;
        }
    }

    private string GetTimeframeValue(TimeFrame tf)
    {
        return tf switch
        {
            TimeFrame.Ticks => "1",
            TimeFrame.Minutes1 => "2",
            TimeFrame.Minutes5 => "3",
            TimeFrame.Minutes10 => "4",
            TimeFrame.Minutes15 => "5",
            TimeFrame.Minutes30 => "6",
            TimeFrame.Hour1 => "7",
            TimeFrame.Hour2 => "8",
            TimeFrame.Hour4 => "9",
            TimeFrame.Daily => "10",
            TimeFrame.Weekly => "11",
            TimeFrame.Monthly => "12",
            _ => "7"
        };
    }
}
