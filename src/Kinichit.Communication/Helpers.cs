using System.Text.RegularExpressions;

namespace Kinichit.Communication;

public static class PhoneNumbers
{
    /// <summary>
    /// E.164 form (+CCNNN…). Strips spaces, dashes and brackets; a leading 0 or 00 is handled; a number without a
    /// country code gets <paramref name="defaultCountryCode"/>. Throws <see cref="ArgumentException"/> when it isn't a phone number.
    /// </summary>
    public static string ToE164(string phone, string defaultCountryCode = "91")
    {
        var raw = (phone ?? "").Trim();
        var plus = raw.StartsWith('+');
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (!plus && digits.StartsWith("00")) { digits = digits[2..]; plus = true; }
        if (!plus)
        {
            if (digits.StartsWith('0')) digits = digits.TrimStart('0');
            if (digits.Length <= 10) digits = defaultCountryCode + digits;
        }
        if (digits.Length is < 8 or > 15) throw new ArgumentException($"'{phone}' is not a valid phone number.");
        return "+" + digits;
    }

    /// <summary>Last four digits only, for logs.</summary>
    public static string Mask(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? "****" : new string('*', digits.Length - 4) + digits[^4..];
    }
}

/// <summary>Very small template engine: replaces <c>{{name}}</c> with values (HTML-encoded when asked).</summary>
public static partial class MessageTemplate
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Unknown placeholders are left as they are, so a missing value is visible in testing.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values, bool htmlEncode = false) =>
        Placeholder().Replace(template, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var v)) return m.Value;
            return htmlEncode ? System.Net.WebUtility.HtmlEncode(v ?? "") : v ?? "";
        });
}
