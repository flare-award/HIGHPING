using System.Globalization;
using System.Windows.Controls;

namespace LatencyStudio.App.Validation;

public sealed class IntegerRangeRule : ValidationRule
{
    public int Minimum { get; set; }
    public int Maximum { get; set; } = int.MaxValue;

    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var number))
        {
            return new ValidationResult(false, "Введите целое число.");
        }

        return number < Minimum || number > Maximum
            ? new ValidationResult(false, $"Допустимо от {Minimum} до {Maximum} мс.")
            : ValidationResult.ValidResult;
    }
}
