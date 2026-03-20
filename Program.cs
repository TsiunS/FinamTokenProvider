namespace FinamTokenProvider
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var provider = new FinamBrowserTokenProvider();

                var token = await provider.GetTokenAsync(new FinamBrowserTokenProvider.DownloadParams
                {
                    Symbol = "SBER",
                    From = DateTime.UtcNow.AddDays(-7),
                    To = DateTime.UtcNow,
                    Timeframe = FinamBrowserTokenProvider.TimeFrame.Minutes30
                });

                Console.WriteLine($"Готово. Токен: {token}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex}");
            }
        }
    }
}
