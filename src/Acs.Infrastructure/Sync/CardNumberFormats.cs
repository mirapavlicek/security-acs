using Acs.Domain.Entities;

namespace Acs.Infrastructure.Sync;

/// <summary>
/// Jak se z hodnoty identifikátoru ze zdroje udělá číslo, které znají čtečky (WIN-PAK).
/// Každá nemocnice tiskne číslo karty jinak a integrační služba ho vrací v „nativním“
/// tvaru — do čteček ale musí jít přesně to, co čtečka z karty přečte.
/// </summary>
public enum CardNumberFormat
{
    /// <summary>Hodnota beze změny (jen sjednocení: bez mezer a pomlček, velká písmena).</summary>
    Raw = 0,

    /// <summary>
    /// Posledních 5 číslic — karty Homolky (podtyp 3): služba vrací <c>4d-07782</c>,
    /// čtečka čte <c>07782</c>.
    /// </summary>
    Last5 = 1,

    /// <summary>
    /// Pomlčka → 0 — karty FN Motol (podtyp 100003) v nativním tvaru <c>nnn-nnnnn</c>:
    /// čtečka čte <c>nnn0nnnnn</c> (<c>123-45678</c> → <c>123045678</c>).
    /// </summary>
    DashToZero = 2,
}

/// <summary>Pravidlo pro jeden podtyp identifikátoru (<c>idIdentifierSubType</c>) z integrační služby.</summary>
public sealed record IdentifierSubTypeRule(int SubType, IdentifierType Type, CardNumberFormat Format)
{
    public override string ToString()
        => $"{SubType} = {(Type == IdentifierType.LicensePlate ? "SPZ" : Type.ToString())}"
           + (Type == IdentifierType.LicensePlate || Format == CardNumberFormat.Raw ? "" : $" : {Format}");
}

public static class CardNumberFormats
{
    /// <summary>
    /// Výchozí pravidla podle skutečné služby ws-integrations: 3 = karta Homolka
    /// (posledních 5 číslic), 100003 = karta FN Motol (pomlčka → 0), 4 = SPZ.
    /// </summary>
    public const string DefaultRules = "3 = Card : Last5\n100003 = Card : DashToZero\n4 = LicensePlate";

    public static IReadOnlyList<IdentifierSubTypeRule> Default => ParseRules(DefaultRules, out _);

    /// <summary>
    /// Rozparsuje pravidla z nastavení — řádek (nebo položka oddělená <c>;</c>) ve tvaru
    /// <c>podtyp = typ [: formát]</c>, např. <c>100003 = Card : DashToZero</c>,
    /// <c>4 = SPZ</c>. Typ i formát se berou česky i anglicky. Prázdný vstup = výchozí
    /// pravidla. Nesrozumitelné řádky skončí v <paramref name="errors"/> a přeskočí se.
    /// </summary>
    public static IReadOnlyList<IdentifierSubTypeRule> ParseRules(string? text, out List<string> errors)
    {
        errors = [];
        if (string.IsNullOrWhiteSpace(text))
            text = DefaultRules;

        var rules = new List<IdentifierSubTypeRule>();
        foreach (var rawLine in text.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine;
            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment].Trim();
            if (line.Length == 0)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || !int.TryParse(line[..eq].Trim(), out var subType) || subType <= 0)
            {
                errors.Add($"„{rawLine}“ — očekává se „podtyp = typ : formát“, podtyp je kladné číslo.");
                continue;
            }

            var rest = line[(eq + 1)..].Split(':', 2, StringSplitOptions.TrimEntries);
            var type = CardSyncService.ParseType(rest[0]);
            if (type == IdentifierType.Other)
            {
                errors.Add($"„{rawLine}“ — neznámý typ „{rest[0]}“ (Card / karta, LicensePlate / SPZ).");
                continue;
            }

            var format = CardNumberFormat.Raw;
            if (rest.Length > 1 && rest[1].Length > 0)
            {
                if (TryParseFormat(rest[1], out var parsed))
                    format = parsed;
                else
                {
                    errors.Add($"„{rawLine}“ — neznámý formát „{rest[1]}“ (Raw, Last5, DashToZero).");
                    continue;
                }
            }

            if (rules.Any(r => r.SubType == subType))
            {
                errors.Add($"„{rawLine}“ — podtyp {subType} je uveden dvakrát.");
                continue;
            }

            rules.Add(new IdentifierSubTypeRule(subType, type, format));
        }

        return rules;
    }

    public static bool TryParseFormat(string? raw, out CardNumberFormat format)
    {
        // Bez diakritiky, mezer a interpunkce: „posledních 5“ i „pomlčka→0“ projdou.
        var folded = (raw ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var key = new string(folded.ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c))
            .ToArray());
        switch (key)
        {
            case "" or "raw" or "beze zmeny" or "bezezmeny" or "nativ" or "native":
                format = CardNumberFormat.Raw; return true;
            case "last5" or "poslednich5" or "posledni5" or "5":
                format = CardNumberFormat.Last5; return true;
            case "dashtozero" or "dash0" or "pomlcka0" or "pomlckanula" or "nula":
                format = CardNumberFormat.DashToZero; return true;
            default:
                format = CardNumberFormat.Raw; return false;
        }
    }

    /// <summary>Převede hodnotu ze zdroje na číslo pro čtečky podle formátu (bez závěrečné normalizace).</summary>
    public static string Apply(CardNumberFormat format, string raw)
    {
        var value = raw.Trim();
        switch (format)
        {
            case CardNumberFormat.Last5:
            {
                var digits = new string(value.Where(char.IsDigit).ToArray());
                if (digits.Length == 0)
                    return value;
                return digits.Length > 5 ? digits[^5..] : digits;
            }

            case CardNumberFormat.DashToZero:
                return value.Replace(" ", "").Replace('-', '0');

            default:
                return value;
        }
    }

    public static string Describe(CardNumberFormat format) => format switch
    {
        CardNumberFormat.Last5 => "posledních 5 číslic (4d-07782 → 07782)",
        CardNumberFormat.DashToZero => "pomlčka → 0 (123-45678 → 123045678)",
        _ => "beze změny",
    };
}
