using PuppeteerSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

    public async Task<string> GetTokenAsync(DownloadParams? parameters = null)
    {
        parameters ??= new DownloadParams
        {
            From = DateTime.UtcNow.AddDays(-1),
            To = DateTime.UtcNow,
            Timeframe = TimeFrame.Hour1
        };

        if (parameters.Timeframe == TimeFrame.Ticks && parameters.From.Date != parameters.To.Date)
        {
            Console.WriteLine("ВНИМАНИЕ: Для тиков даты должны совпадать. Используем только дату From.");
            parameters.To = parameters.From;
        }

        Console.WriteLine("Скачиваем/проверяем Chromium для PuppeteerSharp...");
        await new BrowserFetcher().DownloadAsync();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var debugLog = new List<string>();

        var launchOptions = new LaunchOptions
        {
            Headless = false,
            DefaultViewport = null,
            Args = new[]
            {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-web-security",
                "--start-maximized",
                "--window-size=1920,1080"
            }
        };

        using var browser = await Puppeteer.LaunchAsync(launchOptions);
        using var page = await browser.NewPageAsync();

        page.DefaultTimeout = 120000;
        page.DefaultNavigationTimeout = 120000;

        await page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");

        void TryExtractTokenFromUrl(string? url, string source)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var decoded = Uri.UnescapeDataString(url);
            var match = Regex.Match(decoded, @"[?&]finam_token=([^&]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return;
            }

            var token = match.Groups[1].Value;
            var message = $"[{DateTime.Now:HH:mm:ss}] Токен найден в {source}: {url}";
            Console.WriteLine(message);
            debugLog.Add(message);
            tcs.TrySetResult(token);
        }

        void AttachNetworkTracing(IPage tracedPage, string pageName)
        {
            tracedPage.Request += (_, e) =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] [{pageName}] REQUEST {e.Request.Method} {e.Request.Url}";
                debugLog.Add(line);

                if (e.Request.Url.Contains("export", StringComparison.OrdinalIgnoreCase)
                    || e.Request.Url.Contains("sessions/token", StringComparison.OrdinalIgnoreCase)
                    || e.Request.Url.Contains(".out", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(line);
                }

                TryExtractTokenFromUrl(e.Request.Url, $"request/{pageName}");
            };

            tracedPage.Response += (_, e) =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] [{pageName}] RESPONSE {(int)e.Response.Status} {e.Response.Url}";
                debugLog.Add(line);

                if (e.Response.Url.Contains("export", StringComparison.OrdinalIgnoreCase)
                    || e.Response.Url.Contains("sessions/token", StringComparison.OrdinalIgnoreCase)
                    || e.Response.Url.Contains(".out", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(line);
                }

                TryExtractTokenFromUrl(e.Response.Url, $"response/{pageName}");
            };

            tracedPage.FrameNavigated += (_, e) =>
            {
                TryExtractTokenFromUrl(e.Frame.Url, $"frame/{pageName}");
            };
        }

        AttachNetworkTracing(page, "main");

        var popupCounter = 0;

        browser.TargetCreated += async (_, e) =>
        {
            try
            {
                if (e.Target.Type == TargetType.Page)
                {
                    var popup = await e.Target.PageAsync();
                    if (popup != null)
                    {
                        var popupName = $"popup-{Interlocked.Increment(ref popupCounter)}";
                        AttachNetworkTracing(popup, popupName);
                    }
                }
            }
            catch
            {
                // ignore popup attach errors
            }
        };

        var symbol = parameters.Symbol.ToLowerInvariant();
        var exportUrl = $"https://www.finam.ru/quote/moex/{symbol}/export/";

        Console.WriteLine($"Переходим: {exportUrl}");
        try
        {
            await page.GoToAsync(exportUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = 60000
            });
        }
        catch (NavigationException ex) when (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                                            && page.Url.Contains("finam.ru/quote/moex", StringComparison.OrdinalIgnoreCase))
        {
            // На странице много аналитических трекеров, из-за которых NetworkIdle/жёсткий таймаут часто срабатывает ложноположительно.
            // Если уже на нужном URL, продолжаем работу.
            var navWarning = $"Навигация завершилась по таймауту, но целевая страница открыта: {page.Url}";
            Console.WriteLine(navWarning);
            debugLog.Add($"[{DateTime.Now:HH:mm:ss}] {navWarning}");
        }

        await page.WaitForSelectorAsync("body");

        await AcceptCookiesAsync(page, debugLog);
        await FillFormAsync(page, parameters, debugLog);

        await page.ScreenshotAsync("finam-before-download-click.png");
        Console.WriteLine("Нажимаем кнопку 'Получить файл'...");

        var clicked = await page.EvaluateFunctionAsync<bool>(@"() => {
            const buttons = Array.from(document.querySelectorAll('button, input[type=""submit""], a'));
            const submit = buttons.find(b => {
                const t = (b.textContent || b.value || '').trim().toLowerCase();
                return t.includes('получить файл') || t === 'получить';
            });
            if (!submit) return false;
            submit.click();
            return true;
        }");

        if (!clicked)
        {
            throw new InvalidOperationException("Не удалось найти кнопку 'Получить файл'.");
        }

        await page.ScreenshotAsync("finam-after-download-click.png");

        try
        {
            var token = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120));
            await File.WriteAllTextAsync("token.txt", token);
            await File.WriteAllLinesAsync("debug.log", debugLog);

            Console.WriteLine("Токен получен и сохранён в token.txt");
            return token;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Таймаут ожидания токена (120 сек). Сохраняем диагностику...");
            await File.WriteAllLinesAsync("debug.log", debugLog);
            await File.WriteAllTextAsync("page.html", await page.GetContentAsync());
            await page.ScreenshotAsync("finam-timeout.png");
            throw;
        }
    }

    private static async Task AcceptCookiesAsync(IPage page, List<string> debugLog)
    {
        try
        {
            var accepted = await page.EvaluateFunctionAsync<bool>(@"() => {
                const controls = Array.from(document.querySelectorAll('button, div[role=""button""], a'));
                const accept = controls.find(c => {
                    const text = (c.textContent || '').toLowerCase();
                    return text.includes('принять') || text.includes('соглас');
                });
                if (!accept) return false;
                accept.click();
                return true;
            }");

            if (accepted)
            {
                debugLog.Add($"[{DateTime.Now:HH:mm:ss}] Cookie banner accepted");
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            debugLog.Add($"[{DateTime.Now:HH:mm:ss}] Cookie accept skipped: {ex.Message}");
        }
    }

    private static async Task FillFormAsync(IPage page, DownloadParams parameters, List<string> debugLog)
    {
        var fromDate = parameters.From.ToString("dd.MM.yyyy");
        var toDate = parameters.To.ToString("dd.MM.yyyy");
        var timeframeValue = GetTimeframeValue(parameters.Timeframe);

        await page.WaitForSelectorAsync("input");

        var formFilled = await page.EvaluateFunctionAsync<bool>(@"(fromDate, toDate, timeframeValue) => {
            const allInputs = Array.from(document.querySelectorAll('input'));
            const allSelects = Array.from(document.querySelectorAll('select'));

            const fromInput = allInputs.find(i => {
                const n = (i.name || '').toLowerCase();
                const p = (i.placeholder || '').toLowerCase();
                return n === 'from' || p.includes('дд.мм.гггг');
            });

            const toInput = allInputs.find(i => {
                const n = (i.name || '').toLowerCase();
                return n === 'to';
            }) || allInputs.filter(i => (i.placeholder || '').toLowerCase().includes('дд.мм.гггг'))[1];

            const periodSelect = allSelects.find(s => (s.name || '').toLowerCase() === 'p');

            const writeValue = (el, value) => {
                if (!el) return;
                el.focus();
                el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                el.blur();
            };

            writeValue(fromInput, fromDate);
            writeValue(toInput, toDate);

            if (periodSelect) {
                periodSelect.value = timeframeValue;
                periodSelect.dispatchEvent(new Event('change', { bubbles: true }));
            }

            return !!fromInput || !!toInput || !!periodSelect;
        }", fromDate, toDate, timeframeValue);

        debugLog.Add($"[{DateTime.Now:HH:mm:ss}] Form fill result = {formFilled}. from={fromDate} to={toDate} tf={parameters.Timeframe}({timeframeValue})");
    }

    private static string GetTimeframeValue(TimeFrame tf)
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
