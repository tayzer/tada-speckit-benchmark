using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CheckoutRetry.HiddenTests.Infrastructure;

public sealed record SubmitResult(HttpStatusCode? Status, string? FinalPath, string Body, Exception? Error, bool TimedOut)
{
    public bool ServerError => Error is not null && !TimedOut || Status is { } s && (int)s >= 500;
    public bool LandedOnConfirmation => FinalPath?.Contains("/Basket/Success", StringComparison.OrdinalIgnoreCase) == true
                                        || Body.Contains("Thanks for your Order", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A browser-like customer: cookies, antiforgery tokens, and forms posted exactly as rendered.</summary>
public sealed partial class ShopClient
{
    public const string Password = "Pass@word1";
    private readonly HttpClient _client;
    public string Email { get; }

    public ShopClient(EShopHost host, string email)
    {
        Email = email;
        _client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true, HandleCookies = true });
        _client.Timeout = TimeSpan.FromMinutes(2);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""|value=""([^""]+)""[^>]*name=""__RequestVerificationToken""")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"<form\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FormRegex();
    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputRegex();
    [GeneratedRegex(@"\b(name|value|type)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex AttrRegex();

    public static string Token(string html)
    {
        var m = TokenRegex().Match(html);
        Assert.True(m.Success, "No antiforgery token found on page.");
        return WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    public async Task LoginAsync()
    {
        var page = await _client.GetStringAsync("/Identity/Account/Login");
        var response = await _client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
        [
            new("Input.Email", Email), new("Input.Password", Password), new("Input.RememberMe", "false"),
            new("__RequestVerificationToken", Token(page)),
        ]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode && !body.Contains("Invalid login attempt", StringComparison.OrdinalIgnoreCase),
            $"Login failed for {Email} (status {(int)response.StatusCode}).");
    }

    public async Task AddItemAsync(int catalogItemId, string name, decimal price)
    {
        var home = await _client.GetStringAsync("/");
        var response = await _client.PostAsync("/Basket/Index", new FormUrlEncodedContent(
        [
            new("id", catalogItemId.ToString()), new("name", name), new("price", price.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("__RequestVerificationToken", Token(home)),
        ]));
        Assert.True(response.IsSuccessStatusCode, $"Adding item {catalogItemId} to the basket failed ({(int)response.StatusCode}).");
    }

    /// <summary>Loads the checkout page and captures the checkout form's fields exactly as the browser would post them.</summary>
    public async Task<List<KeyValuePair<string, string>>> OpenCheckoutAsync()
    {
        var html = await _client.GetStringAsync("/Basket/Checkout");
        var forms = FormRegex().Matches(html).Select(m => m.Value).ToList();
        var form = forms.FirstOrDefault(f => f.Contains("/Basket/Checkout", StringComparison.OrdinalIgnoreCase) && f.Contains("method=\"post\"", StringComparison.OrdinalIgnoreCase))
                   ?? forms.FirstOrDefault(f => f.Contains("__RequestVerificationToken", StringComparison.Ordinal) && f.Contains("Items[", StringComparison.Ordinal));
        Assert.True(form is not null, "Checkout page has no checkout form (is the basket empty?).");
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in InputRegex().Matches(form!))
        {
            var attrs = AttrRegex().Matches(input.Value).ToDictionary(a => a.Groups[1].Value.ToLowerInvariant(), a => WebUtility.HtmlDecode(a.Groups[2].Value));
            if (!attrs.TryGetValue("name", out var name) || string.IsNullOrEmpty(name)) continue;
            var type = attrs.GetValueOrDefault("type", "text").ToLowerInvariant();
            if (type is "submit" or "button" or "image" or "file") continue;
            if (type is "checkbox" or "radio" && !input.Value.Contains("checked", StringComparison.OrdinalIgnoreCase)) continue;
            fields.Add(new(name, attrs.GetValueOrDefault("value", "")));
        }
        return fields;
    }

    public async Task<SubmitResult> SubmitCheckoutAsync(IEnumerable<KeyValuePair<string, string>> form, TimeSpan? clientTimeout = null)
    {
        using var cts = clientTimeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        try
        {
            var response = await _client.PostAsync("/Basket/Checkout", new FormUrlEncodedContent(form), cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return new SubmitResult(response.StatusCode, response.RequestMessage?.RequestUri?.AbsolutePath, body, null, false);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            return new SubmitResult(null, null, "", ex, TimedOut: true);
        }
        catch (Exception ex)
        {
            // The server failed the request (unhandled exception surfaced by TestServer) — the customer saw an error.
            return new SubmitResult(HttpStatusCode.InternalServerError, null, "", ex, false);
        }
    }

    /// <summary>Posts to checkout with only an antiforgery token (e.g. an empty basket).</summary>
    public async Task<SubmitResult> SubmitEmptyCheckoutAsync()
    {
        var page = await _client.GetStringAsync("/Basket/Index");
        return await SubmitCheckoutAsync([new("__RequestVerificationToken", Token(page))]);
    }
}
