using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class ConvertCurrencyTool : AITool
{
    // Rates expressed in USD per unit of the source currency.
    // Hardcoded for the workshop. In a real system this would come from a live rates API,
    // typically injected as an IRatesService in the constructor.
    private static readonly Dictionary<string, decimal> RatesToUsd = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    [Description("Gets the list of currencies supported for conversion.")]
    public IEnumerable<string> GetSupportedCurrencies()
    {
        UpdateExchangeRates();
        return RatesToUsd.Keys;
    }

    [Description("Converts a specified amount from one currency to another. Return a string like '100 USD is approximately 90 EUR'.")]
    public async Task<string> Convert(
        [Description("The amount to convert.")] decimal value,
        [Description("The currency to convert from.")] string fromCurrency,
        [Description("The currency to convert to.")] string toCurrency)
    {
        UpdateExchangeRates();
        if (!RatesToUsd.TryGetValue(fromCurrency, out var fromRate))
        {
            throw new ArgumentException($"Unsupported source currency: {fromCurrency}");
        }

        if (!RatesToUsd.TryGetValue(toCurrency, out var toRate))
        {
            throw new ArgumentException($"Unsupported target currency: {toCurrency}");
        }

        // Convert the amount to USD, then to the target currency.
        var valueInUsd = value / fromRate;
        var convertedValue = valueInUsd * toRate;

        return $"{value} {fromCurrency} is approximately {convertedValue:F2} {toCurrency}";
    }

    private static void UpdateExchangeRates()
    {
        using var client = new HttpClient();
        var result = client.GetAsync("https://open.er-api.com/v6/latest/USD");
        var exchangeRates = JsonConvert.DeserializeObject<ExchangeRatesResponse>(result.Result.Content.ReadAsStringAsync().Result);
        if (exchangeRates?.Rates is not null)
        {
            foreach (var kvp in exchangeRates.Rates)
            {
                RatesToUsd[kvp.Key] = kvp.Value;
            }
        }
    }
}

public class ExchangeRatesResponse
{
    [JsonProperty("result")]
    public string? Result { get; set; }

    [JsonProperty("provider")]
    public string? Provider { get; set; }

    [JsonProperty("documentation")]
    public string? Documentation { get; set; }

    [JsonProperty("terms_of_use")]
    public string? TermsOfUse { get; set; }

    [JsonProperty("time_last_update_unix")]
    public long TimeLastUpdateUnix { get; set; }

    [JsonProperty("time_last_update_utc")]
    public string? TimeLastUpdateUtc { get; set; }

    [JsonProperty("time_next_update_unix")]
    public long TimeNextUpdateUnix { get; set; }

    [JsonProperty("time_next_update_utc")]
    public string? TimeNextUpdateUtc { get; set; }

    [JsonProperty("time_eol_unix")]
    public long TimeEolUnix { get; set; }

    [JsonProperty("base_code")]
    public string? BaseCode { get; set; }

    [JsonProperty("rates")]
    public Dictionary<string, decimal>? Rates { get; set; }
}
