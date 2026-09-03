using System.Collections;
using System.Globalization;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>Převody hodnot vracených z COM (VARIANT, SAFEARRAY, DATE) na typy .NET.</summary>
public static class ComValue
{
    /// <summary>
    /// WIN-PAK vrací kolekce jako VARIANT se SAFEARRAY; podle volání to může být
    /// pole objektů, jediný objekt, nebo nic.
    /// </summary>
    /// <summary>
    /// Prostá hodnota (řetězec, číslo libovolné šířky, datum, logická hodnota) na
    /// rozdíl od COM objektu s vlastnostmi. WIN-PAK v seznamech obojí střídá
    /// podle volání — třeba id skupin svátků panelu chodí jako <c>UInt32</c>.
    /// </summary>
    public static bool IsScalar(object? value) => value is string or IConvertible;

    public static IEnumerable<object> AsEnumerable(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string:
                yield return value;
                yield break;
            case IEnumerable sequence:
                foreach (var item in sequence)
                {
                    if (item is not null)
                        yield return item;
                }

                yield break;
            default:
                yield return value;
                yield break;
        }
    }

    public static string? ToStringOrNull(object? value)
    {
        var text = value switch
        {
            null or DBNull => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static string ToStringOrEmpty(object? value) => ToStringOrNull(value) ?? "";

    public static int ToInt(object? value, int fallback = 0)
        => value switch
        {
            null or DBNull => fallback,
            int i => i,
            long l => (int)l,
            short s => s,
            bool b => b ? 1 : 0,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible c => c.ToInt32(CultureInfo.InvariantCulture),
            _ => fallback,
        };

    public static long ToLong(object? value, long fallback = 0)
        => value switch
        {
            null or DBNull => fallback,
            long l => l,
            int i => i,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
            _ => fallback,
        };

    public static bool ToBool(object? value)
        => value switch
        {
            null or DBNull => false,
            bool b => b,
            string s => bool.TryParse(s, out var parsed) ? parsed : ToInt(s) != 0,
            _ => ToInt(value) != 0,
        };

    /// <summary>COM DATE; nulové/minimální datum bereme jako „nenastaveno“.</summary>
    public static DateTime? ToDate(object? value)
    {
        var date = value switch
        {
            null or DBNull => (DateTime?)null,
            DateTime d => d,
            double oa => DateTime.FromOADate(oa),
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
            _ => null,
        };

        return date is null || date.Value.Year <= 1900 ? null : date;
    }
}
