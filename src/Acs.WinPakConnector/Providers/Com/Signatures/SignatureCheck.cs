namespace Acs.WinPakConnector.Providers.Com.Signatures;

/// <summary>Výsledek porovnání jedné metody: co posílá konektor, co má WIN-PAK a jak to spolu vychází.</summary>
public sealed record SignatureCheckResult(
    string Method,
    string Origin,
    string Sent,
    string? Actual,
    SignatureVerdict Verdict,
    string Note);

public enum SignatureVerdict
{
    /// <summary>Počet i typy sedí.</summary>
    Ok,
    /// <summary>Liší se, ale konektor rozdíl vyrovná sám za běhu (výstupní řetězec, výstupní variant, chybějící parametry na konci).</summary>
    Learnable,
    /// <summary>Liší se způsobem, který konektor sám nevyrovná — je třeba upravit kód.</summary>
    Mismatch,
    /// <summary>Objekt WIN-PAKu metodu nemá.</summary>
    Missing,
    /// <summary>Typová informace není k dispozici, porovnat nejde.</summary>
    Unknown,
}

/// <summary>
/// Porovnání katalogu volání konektoru (přepis příručky) se skutečnými signaturami
/// objektu WIN-PAKu z jeho typové informace. Bez jediného volání do databáze — čte
/// se jen popis metod. Na ostrém serveru tak jde zkontrolovat všech ~90 metod najednou
/// místo objevování rozdílů jeden po druhém při používání.
/// </summary>
public static class SignatureCheck
{
    /// <summary>Zdroj skutečných signatur a seznamu členů objektu; na Windows typová informace, v testech atrapa.</summary>
    public sealed record ObjectDescription(
        Func<string, ComMembers.ComMethodSignature?> Signature,
        IReadOnlyCollection<string> Members);

    public static ObjectDescription Describe(IComDispatch target)
        => new(method => ComMembers.DescribeMethod(target, method), ComMembers.Describe(target));

    public static IReadOnlyList<SignatureCheckResult> Run(IEnumerable<RecordedCall> calls, ObjectDescription description)
        => calls.Select(call => Check(call, description)).ToList();

    public static SignatureCheckResult Check(RecordedCall call, ObjectDescription description)
    {
        var sent = string.Join(", ", call.Arguments);
        var signature = description.Signature(call.Method);
        if (signature is null)
        {
            var exists = description.Members.Contains(call.Method, StringComparer.OrdinalIgnoreCase);
            return description.Members.Count == 0
                ? new(call.Method, call.Origin, sent, null, SignatureVerdict.Unknown, "typová informace objektu není k dispozici")
                : exists
                    ? new(call.Method, call.Origin, sent, null, SignatureVerdict.Unknown, "metoda existuje, ale její popis se nepodařilo přečíst")
                    : new(call.Method, call.Origin, sent, null, SignatureVerdict.Missing, "objekt WIN-PAKu tuto metodu nemá (jiná verze nebo licence API)");
        }

        var actual = signature.ToString();
        var parameters = signature.Parameters;
        var notes = new List<string>();
        var verdict = SignatureVerdict.Ok;

        void Worse(SignatureVerdict candidate, string note)
        {
            notes.Add(note);
            if (candidate > verdict)
                verdict = candidate;
        }

        if (call.Arguments.Count > parameters.Count)
        {
            Worse(SignatureVerdict.Mismatch,
                $"konektor posílá {call.Arguments.Count} parametrů, WIN-PAK má {parameters.Count} — přebývající se neořezávají, kód je třeba upravit");
        }
        else if (call.Arguments.Count < signature.RequiredCount)
        {
            var missing = parameters.Skip(call.Arguments.Count).Where(p => !p.Optional).Select(p => p.ToString());
            Worse(SignatureVerdict.Learnable, $"chybí {string.Join(", ", missing)} — konektor doplní za běhu");
        }

        for (var i = 0; i < Math.Min(call.Arguments.Count, parameters.Count); i++)
        {
            var (candidate, note) = Compare(call.Arguments[i], parameters[i]);
            if (candidate != SignatureVerdict.Ok)
                Worse(candidate, $"{i + 1}. {parameters[i].Name}: {note}");
        }

        return new(call.Method, call.Origin, sent, actual, verdict,
            notes.Count == 0 ? "sedí" : string.Join("; ", notes));
    }

    private static (SignatureVerdict, string) Compare(SentArgument sent, ComMembers.ComParameter parameter)
    {
        var real = parameter.Type;
        if (real is "Variant" or "Object")
        {
            // Variant vezme cokoli; by-ref Variant ale ne číslo — to konektor přeučí na null.
            return parameter.ByRef && sent is { Type: "Long" or "Integer", Placeholder: true }
                ? (SignatureVerdict.Learnable, $"posílá se 0, chce {real} — konektor přeučí na prázdný variant")
                : (SignatureVerdict.Ok, "");
        }

        if (sent.Type == "null")
        {
            return real == "String" && parameter.ByRef
                ? (SignatureVerdict.Learnable, "posílá se null, chce ByRef String — konektor přeučí na \"\"")
                : real.EndsWith("()")
                    ? (SignatureVerdict.Ok, "")
                    : (SignatureVerdict.Mismatch, $"posílá se null, chce {real}");
        }

        if (sent.Type == real)
            return (SignatureVerdict.Ok, "");

        return (sent.Type, real) switch
        {
            ("Long", "Integer" or "Byte") or ("Integer", "Long" or "Byte") or ("Byte", "Long" or "Integer")
                => parameter.ByRef
                    ? (SignatureVerdict.Mismatch, $"posílá se {sent.Type}, chce ByRef {real} — by-ref vyžaduje přesný typ")
                    : (SignatureVerdict.Ok, ""),
            ("Long", "Double" or "Currency") => (SignatureVerdict.Ok, ""),
            ("Long()", "Variant()") or ("String()", "Variant()") => (SignatureVerdict.Ok, ""),
            _ => (SignatureVerdict.Mismatch, $"posílá se {sent.Type}, chce {real}"),
        };
    }
}
