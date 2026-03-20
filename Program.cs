namespace FinamTokenProvider
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var provider = new FinamBrowserTokenProvider();

                // Пример 1: Тики за один день
                var token1 = await provider.GetTokenAsync(new FinamBrowserTokenProvider.DownloadParams
                {
                    Symbol = "SBER",
                    From = DateTime.Parse("2026-03-18"),
                    To = DateTime.Parse("2026-03-18"),  // Одинаковые даты для тиков
                    Timeframe = FinamBrowserTokenProvider.TimeFrame.Ticks
                });

                // Пример 2: Часовые свечи за неделю
                var token2 = await provider.GetTokenAsync(new FinamBrowserTokenProvider.DownloadParams
                {
                    Symbol = "SBER",
                    From = DateTime.Now.AddDays(-7),
                    To = DateTime.Now,
                    Timeframe = FinamBrowserTokenProvider.TimeFrame.Hour1
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }

            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }
    }
}
