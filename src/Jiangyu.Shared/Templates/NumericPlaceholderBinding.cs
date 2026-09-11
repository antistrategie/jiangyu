using System.Globalization;
using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

/// <summary>A numeric source for one entry in a localised string's placeholder array.</summary>
public sealed class NumericPlaceholderBinding
{
    [JsonPropertyName("source")]
    public CompiledTemplateReference Source { get; set; } = new();

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "number";

    [JsonIgnore]
    public bool IsValid => Source != null
        && !string.IsNullOrWhiteSpace(Source.TemplateType)
        && !string.IsNullOrWhiteSpace(Source.TemplateId)
        && TemplatePatchPathValidator.IsSupportedFieldPath(Path)
        && IsSupportedFormat(Format);

    public static bool IsSupportedFormat(string? format)
        => format is "number" or "percent" or "bonus-percent" or "reduction-percent" or "magnitude";

    public bool TryFormat(object? value, out string text)
    {
        text = string.Empty;
        if (value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            return false;

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        number = Format switch
        {
            "number" => number,
            "percent" => number * 100,
            "bonus-percent" => (number - 1) * 100,
            "reduction-percent" => (1 - number) * 100,
            "magnitude" => Math.Abs(number),
            _ => double.NaN,
        };
        if (double.IsNaN(number) || double.IsInfinity(number))
            return false;

        // Two decimal places hide single-precision noise without rounding 12.5 to 13.
        number = Math.Round(number, 2);
        text = (number == 0 ? 0 : number).ToString("0.##", CultureInfo.InvariantCulture);
        return true;
    }
}
