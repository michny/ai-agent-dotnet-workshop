using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class ConvertCurrencyTool : AITool
{
    // Rates expressed in USD per unit of the source currency.
    // Hardcoded for the workshop. In a real system this would come from a live rates API,
    // typically injected as an IRatesService in the constructor.
    private static readonly Dictionary<string, decimal> RatesToUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.00m,
        ["EUR"] = 1.10m,
        ["GBP"] = 1.27m,
        ["JPY"] = 0.0067m,
        ["CHF"] = 1.13m,
        ["CAD"] = 0.74m,
        ["AUD"] = 0.66m,
    };

    [Description("Gets the list of currencies supported for conversion.")]
    public IEnumerable<string> GetSupportedCurrencies()
    {
        return RatesToUsd.Keys;
    }

    [Description("Converts a specified amount from one currency to another. Return a string like '100 USD is approximately 90 EUR'.")]
    public async Task<string> Convert(
        [Description("The amount to convert.")] decimal value,
        [Description("The currency to convert from.")] string fromCurrency,
        [Description("The currency to convert to.")] string toCurrency)
    {
        if (!RatesToUsd.TryGetValue(fromCurrency, out var fromRate))
        {
            throw new ArgumentException($"Unsupported source currency: {fromCurrency}");
        }

        if (!RatesToUsd.TryGetValue(toCurrency, out var toRate))
        {
            throw new ArgumentException($"Unsupported target currency: {toCurrency}");
        }

        // Convert the amount to USD, then to the target currency.
        var valueInUsd = value * fromRate;
        var convertedValue = valueInUsd / toRate;

        return $"{value} {fromCurrency} is approximately {convertedValue:F2} {toCurrency}";
    }
}
