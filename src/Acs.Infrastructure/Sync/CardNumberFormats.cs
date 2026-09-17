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
    /// Pomlčka → 0 — karty FN Motol (podtyp 1000003) v nativním tvaru <c>nnn-nnnnn</c>:
    /// čtečka čte <c>nnn0nnnnn</c> (<c>123-45678</c> → <c>123045678</c>). Tvar bez pomlčky
    /// (<c>initialFormat</c> NOHYPHEN, <c>17625930</c>) dostane nulu před posledních 5 číslic.
    /// </summary>
    DashToZero = 2,

    /// <summary>
    /// Podle tvaru hodnoty: <c>4D-…</c> (karta Homolky) → posledních 5 číslic doplněných
    /// nulami; <c>xxx-nnnnn</c> (nativní tvar FN Motol, i s písmenem — <c>22B-11012</c>)
    /// → pomlčka → 0; jinak beze změny. Služba vrací pod jedním podtypem oba druhy karet.
    /// </summary>
    Auto = 3,
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
    /// Výchozí pravidla podle skutečné služby ws-integrations: 3 = karty (Homolka
    /// <c>4d-07782</c> i Motol <c>22B-11012</c>, podle tvaru), 1000003 = karta FN Motol
    /// (pomlčka → 0; <c>initialFormat</c> NATIVE <c>176-25930</c> i NOHYPHEN <c>17625930</c>
    /// dají <c>176025930</c>), 4 = SPZ.
    /// </summary>
    public const string DefaultRules = "3 = Card : Auto\n1000003 = Card : DashToZero\n4 = LicensePlate";

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
            case "auto" or "podletvaru" or "automaticky":
                format = CardNumberFormat.Auto; return true;
            default:
                format = CardNumberFormat.Raw; return false;
        }
    }

    /// <summary>Nativní tvar FN Motol: 3–4 znaky (číslice nebo písmeno), pomlčka, pět číslic — <c>123-45678</c>, <c>22B-11012</c>, <c>EA69-24836</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex MotolNative =
        new(@"^[0-9A-Za-z]{3,4}-\d{5}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Karta Homolky: prefix <c>4D-</c> a 1–5 číslic (služba posílá <c>4d-07782</c> i <c>4D-7782</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex HomolkaCard =
        new(@"^4[Dd]-?\d{1,5}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Nativní tvar FN Motol bez pomlčky (initialFormat NOHYPHEN) rozpoznatelný i bez nápovědy služby:
    /// přesně 3 znaky + 5 číslic (<c>17625930</c>). Delší tvary (<c>EA6924836</c>) by se nedaly odlišit od
    /// už převedeného čísla (<c>123045678</c>) — ty se převádí jen s nápovědou NOHYPHEN.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex MotolNoHyphen =
        new(@"^[0-9A-Za-z]{3}\d{5}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Převede hodnotu ze zdroje na číslo pro čtečky podle formátu (bez závěrečné normalizace).
    /// <paramref name="sourceFormat"/> je <c>initialFormat</c> ze služby (NATIVE / NOHYPHEN), když ho posílá.
    /// </summary>
    public static string Apply(CardNumberFormat format, string raw, string? sourceFormat = null)
    {
        var value = raw.Trim().Replace(" ", "");
        var noHyphen = string.Equals(sourceFormat?.Trim(), "NOHYPHEN", StringComparison.OrdinalIgnoreCase);
        switch (format)
        {
            case CardNumberFormat.Last5:
            {
                // Číslice za poslední pomlčkou (u „4D-7782“ nesmí do čísla spadnout čtyřka z prefixu);
                // bez pomlčky všechny číslice. Doplní se nulami na 5 — 4D-7782 a 4d-07782 je tatáž karta.
                var dash = value.LastIndexOf('-');
                var segment = dash >= 0 ? value[(dash + 1)..] : value;
                var digits = new string(segment.Where(char.IsDigit).ToArray());
                if (digits.Length == 0)
                    digits = new string(value.Where(char.IsDigit).ToArray());
                if (digits.Length == 0)
                    return value;
                return digits.Length >= 5 ? digits[^5..] : digits.PadLeft(5, '0');
            }

            case CardNumberFormat.DashToZero:
                if (value.Contains('-'))
                    return value.Replace('-', '0');
                // Tvar bez pomlčky (NOHYPHEN, nebo bez pomlčky a s 5 číslicemi na konci): nula patří před posledních 5.
                if (value.Length > 5 && (noHyphen || MotolNoHyphen.IsMatch(value)) && value[^5..].All(char.IsDigit))
                    return value[..^5] + "0" + value[^5..];
                return value;

            case CardNumberFormat.Auto:
                if (MotolNative.IsMatch(value) || noHyphen)
                    return Apply(CardNumberFormat.DashToZero, value, sourceFormat);
                if (HomolkaCard.IsMatch(value))
                    return Apply(CardNumberFormat.Last5, value);
                return value;

            default:
                return value;
        }
    }

    public static string Describe(CardNumberFormat format) => format switch
    {
        CardNumberFormat.Last5 => "posledních 5 číslic (4d-07782 → 07782)",
        CardNumberFormat.DashToZero => "pomlčka → 0 (176-25930 i 17625930 → 176025930)",
        CardNumberFormat.Auto => "podle tvaru: 4D-… → posledních 5 číslic, xxx-nnnnn → pomlčka → 0",
        _ => "beze změny",
    };
}
