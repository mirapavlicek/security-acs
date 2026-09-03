using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Tenká abstrakce nad pozdní vazbou na COM objekt. Existuje proto, aby šlo
/// v testech ověřit, že voláme přesně ty metody a v tom pořadí, jak je popisuje
/// příručka WIN-PAK — bez nainstalovaného WIN-PAKu se to jinak ověřit nedá.
/// </summary>
public interface IComDispatch
{
    /// <summary>Samotný COM objekt — metody WIN-PAKu chtějí jeho, ne naši obálku.</summary>
    object Target { get; }

    /// <summary>
    /// Zavolá metodu COM objektu. Hodnoty <c>[out]</c> parametrů se vrací zpět
    /// v poli <paramref name="args"/> (COM je předává by-ref).
    /// </summary>
    object? Invoke(string method, object?[] args);

    object? GetProperty(string name);

    /// <summary>Indexovaná vlastnost, např. <c>NoteField(1)</c> u držitele karty.</summary>
    object? GetProperty(string name, object?[] index);

    void SetProperty(string name, object? value);

    /// <summary>Zápis do indexované vlastnosti, např. <c>NoteField(1) = "…"</c>.</summary>
    void SetProperty(string name, object?[] index, object? value);
}

/// <summary>Vytváří a obaluje COM objekty. V testech se nahrazuje atrapou.</summary>
public interface IComFactory
{
    /// <summary>Vytvoří COM objekt podle ProgID (např. <c>NCIHelper.Application</c>).</summary>
    IComDispatch Create(string progId);

    /// <summary>Obalí objekt vrácený z jiného COM volání (prvek kolekce, vnořený objekt).</summary>
    IComDispatch Wrap(object comObject);

    /// <summary>
    /// Uvolní COM objekt hned, ne až při GC. U WIN-PAKu tím zanikne relace operátora
    /// i databázové spojení, které objekt držel — proto se volá při recyklaci relace.
    /// </summary>
    void Release(IComDispatch dispatch);
}

/// <summary>
/// Který člen COM objektu se právě volá. Volání jdou za sebou pod jedním zámkem,
/// takže stačí jedna hodnota; čte ji hlídání limitu, aby řeklo, ve kterém volání
/// WIN-PAKu konektor uvázl — bez toho by se to na ostrém serveru nedalo zjistit.
/// </summary>
public static class ComCallTrace
{
    private static volatile string? _current;

    public static string? Current
    {
        get => _current;
        set => _current = value;
    }
}

/// <summary>Skutečná pozdní vazba přes <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComDispatch(object instance) : IComDispatch
{
    public object Target { get; } = instance;

    private object Instance => Target;

    /// <summary>DISP_E_TYPEMISMATCH — pozdní vazba odmítla typ argumentu, metoda se nespustila.</summary>
    private const int TypeMismatch = unchecked((int)0x80020005);

    /// <summary>Jak se musí u dané metody upravit argument na dané pozici, aby ho WIN-PAK přijal.</summary>
    private enum Shape
    {
        /// <summary>Výstupní řetězec (<c>ByRef As String</c>): místo null prázdný řetězec.</summary>
        EmptyString,
        /// <summary>Výstupní variant (<c>ByRef As Variant</c>): místo číselné nuly null.</summary>
        NullVariant,
    }

    /// <summary>
    /// Naučené tvary argumentů po metodách. Naučí se při prvním „Type mismatch“
    /// a pak se použijí rovnou — jinak by každé volání metody s výstupním
    /// parametrem stálo dva COM roundtripy (u 785 čteček 785 volání navíc).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Index, Shape Shape)[]> LearnedShapes = new();

    public object? Invoke(string method, object?[] args)
    {
        // WIN-PAK je VB6/COM: jeho Long je 32bitový (VT_I4). C# long by šel jako
        // VT_I8 a skončil „Type mismatch“, proto se identifikátory posílají jako int.
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is long l && l is >= int.MinValue and <= int.MaxValue)
                args[i] = (int)l;
        }

        if (LearnedShapes.TryGetValue(method, out var learned))
            Apply(args, learned);

        try
        {
            return InvokeOnce(method, args);
        }
        catch (ComCallException ex) when (IsTypeMismatch(ex) && RetryVariants(args).Any())
        {
            // Pozdní vazba odmítá by-ref argument, když nesedí typ přesně: výstupní
            // řetězec (ByRef As String) nesmí být null, výstupní variant (ByRef As
            // Variant) zase nesmí být číslo. Které z nich to je, příručka neříká,
            // takže se zkouší postupně. Odmítnuté volání se neprovedlo, opakování je
            // bezpečné i u zápisů.
            foreach (var (candidate, shapes) in RetryVariants(args))
            {
                try
                {
                    var result = InvokeOnce(method, candidate);
                    Array.Copy(candidate, args, args.Length);
                    LearnedShapes[method] = shapes;
                    return result;
                }
                catch (ComCallException retry) when (IsTypeMismatch(retry))
                {
                }
            }

            throw;
        }
    }

    private static void Apply(object?[] args, (int Index, Shape Shape)[] shapes)
    {
        foreach (var (index, shape) in shapes)
        {
            if (index >= args.Length)
                continue;

            args[index] = shape switch
            {
                Shape.EmptyString when args[index] is null => "",
                Shape.NullVariant when args[index] is 0 => null,
                _ => args[index],
            };
        }
    }

    private object? InvokeOnce(string method, object?[] args)
    {
        // Všechny parametry se předávají by-ref, jinak by se [out] hodnoty
        // nedostaly zpět do pole args.
        var modifiers = new ParameterModifier(Math.Max(args.Length, 1));
        for (var i = 0; i < args.Length; i++)
            modifiers[i] = true;

        return Unwrap(method, () => Instance.GetType().InvokeMember(
            method,
            BindingFlags.InvokeMethod,
            binder: null,
            target: Instance,
            args: args,
            modifiers: args.Length > 0 ? [modifiers] : null,
            culture: null,
            namedParameters: null));
    }

    /// <summary>
    /// Nejdřív všechny null → "" najednou (obvyklý případ: jeden výstupní řetězec),
    /// pak každý zvlášť — pro volání, kde je vedle řetězce i výstupní kolekce.
    /// Potom totéž pro číselné nuly → null (výstupní <c>ByRef As Variant</c>,
    /// do kterého konektor posílal 0 jako místo pro výsledek).
    /// </summary>
    private static IEnumerable<(object?[] Args, (int Index, Shape Shape)[] Shapes)> RetryVariants(object?[] args)
    {
        foreach (var variant in VariantsFor(args, Shape.EmptyString, a => a is null, ""))
            yield return variant;
        foreach (var variant in VariantsFor(args, Shape.NullVariant, a => a is 0, null))
            yield return variant;
    }

    private static IEnumerable<(object?[] Args, (int Index, Shape Shape)[] Shapes)> VariantsFor(
        object?[] args, Shape shape, Func<object?, bool> matches, object? replacement)
    {
        var indexes = Enumerable.Range(0, args.Length).Where(i => matches(args[i])).ToArray();
        if (indexes.Length == 0)
            yield break;

        var all = (object?[])args.Clone();
        foreach (var i in indexes)
            all[i] = replacement;
        yield return (all, indexes.Select(i => (i, shape)).ToArray());

        if (indexes.Length < 2)
            yield break;

        foreach (var index in indexes)
        {
            var single = (object?[])args.Clone();
            single[index] = replacement;
            yield return (single, [(index, shape)]);
        }
    }

    private static bool IsTypeMismatch(ComCallException ex)
        => ex.InnerException is COMException { HResult: TypeMismatch };

    public object? GetProperty(string name)
        => Unwrap(name, () => Instance.GetType().InvokeMember(name, BindingFlags.GetProperty, null, Instance, null));

    public object? GetProperty(string name, object?[] index)
        => Unwrap(name, () => Instance.GetType().InvokeMember(name, BindingFlags.GetProperty, null, Instance, Normalize(index)));

    public void SetProperty(string name, object? value)
        => Unwrap(name, () => Instance.GetType().InvokeMember(name, BindingFlags.SetProperty, null, Instance, [value]));

    public void SetProperty(string name, object?[] index, object? value)
        => Unwrap(name, () => Instance.GetType().InvokeMember(name, BindingFlags.SetProperty, null, Instance, [.. Normalize(index), value]));

    private static object?[] Normalize(object?[] args)
    {
        var copy = (object?[])args.Clone();
        for (var i = 0; i < copy.Length; i++)
        {
            if (copy[i] is long l && l is >= int.MinValue and <= int.MaxValue)
                copy[i] = (int)l;
        }

        return copy;
    }

    /// <summary>
    /// <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/> balí
    /// každou chybu volaného objektu do <see cref="TargetInvocationException"/> s textem
    /// „Exception has been thrown by the target of an invocation“ — a ten nikomu nic
    /// neřekne. Skutečná hláška WIN-PAKu (i HRESULT) je až ve vnitřní výjimce; ta se
    /// vyhodí místo obalu a v textu se uvede, které volání selhalo.
    /// </summary>
    private static object? Unwrap(string member, Func<object?> call)
    {
        ComCallTrace.Current = member;
        try
        {
            var result = call();
            ComCallTrace.Current = null;
            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw new ComCallException(member, inner);
        }
        catch (COMException ex)
        {
            // Chyby před samotným vyvoláním (neznámý název členu, spadlý RPC) přijdou
            // bez obalu — bez názvu členu by z nich nešlo poznat, co WIN-PAK nezná.
            throw new ComCallException(member, ex);
        }
        catch (MissingMemberException ex)
        {
            throw new ComCallException(member, ex);
        }
    }
}

/// <summary>Chyba konkrétního volání COM objektu WIN-PAKu s původní hláškou a HRESULT.</summary>
public sealed class ComCallException(string member, Exception inner)
    : InvalidOperationException(Describe(member, inner), inner)
{
    public string Member { get; } = member;

    /// <summary>
    /// Spojení s COM+ serverem WIN-PAKu je pryč — proces spadl nebo byl recyklován
    /// (RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED, RPC_E_DISCONNECTED,
    /// RPC_E_SERVERFAULT). Proxy objekt je od té chvíle mrtvý a každé další volání
    /// selže stejně, dokud se objekt nevytvoří znovu.
    /// </summary>
    public bool IsConnectionLost => InnerException is COMException com
        && com.HResult is unchecked((int)0x800706BA) or unchecked((int)0x800706BE)
            or unchecked((int)0x80010108) or unchecked((int)0x80010105) or unchecked((int)0x800706D9);

    private static string Describe(string member, Exception inner)
    {
        var hresult = inner is COMException com ? $" (HRESULT 0x{com.HResult:X8})" : "";
        return $"WIN-PAK {member}: {inner.Message}{hresult}";
    }
}

/// <summary>Vytváří COM objekty podle ProgID na Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComFactory : IComFactory
{
    public IComDispatch Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"COM objekt '{progId}' není na tomto stroji zaregistrován. Nainstalujte WIN-PAK " +
                "s volbou Web, nebo nasaďte COM+ application proxy (viz docs/winpak-api/README.md).");

        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"COM objekt '{progId}' se nepodařilo vytvořit.");

        return new ComDispatch(instance);
    }

    public IComDispatch Wrap(object comObject) => new ComDispatch(comObject);

    public void Release(IComDispatch dispatch)
    {
        if (Marshal.IsComObject(dispatch.Target))
            Marshal.FinalReleaseComObject(dispatch.Target);
    }
}
