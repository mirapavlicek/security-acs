using Acs.WinPakConnector.Providers.Com;

namespace Acs.WinPakConnector.Tests;

/// <summary>Jedno zaznamenané volání COM metody i s argumenty v okamžiku volání.</summary>
public sealed record ComCall(string Target, string Method, object?[] Args);

/// <summary>
/// Atrapa COM objektu. Umožňuje ověřit, že konektor volá přesně ty metody,
/// v tom pořadí a s těmi argumenty, jak je popisuje příručka WIN-PAK —
/// jinak by se mapování dalo zkontrolovat až na ostrém serveru.
/// </summary>
public sealed class FakeComDispatch(string name, FakeComFactory factory) : IComDispatch
{
    public string Name { get; } = name;

    public object Target => this;

    /// <summary>Hodnoty, které se mají zapsat do <c>[out]</c> parametrů: klíč je „metoda#index“.</summary>
    public Dictionary<string, object?> OutValues { get; } = [];

    /// <summary>Návratové hodnoty metod (pro volání, která vracejí přímo hodnotu).</summary>
    public Dictionary<string, object?> Returns { get; } = [];

    public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Metody, které mají selhat — WIN-PAK některá volání odmítá podle verze a licence.</summary>
    public Dictionary<string, Exception> Throws { get; } = [];

    /// <summary>Metody, které mají selhat jen napoprvé — pád COM+ serveru s následnou obnovou.</summary>
    public Dictionary<string, Queue<Exception>> ThrowsOnce { get; } = [];

    public object? Invoke(string method, object?[] args)
    {
        factory.Calls.Add(new ComCall(Name, method, [.. args]));
        if (Throws.TryGetValue(method, out var failure))
            throw failure;
        if (ThrowsOnce.TryGetValue(method, out var queue) && queue.Count > 0)
            throw queue.Dequeue();

        for (var i = 0; i < args.Length; i++)
        {
            if (OutValues.TryGetValue($"{method}#{i}", out var value))
                args[i] = value;
        }

        return Returns.GetValueOrDefault(method);
    }

    public object? GetProperty(string name)
    {
        if (Throws.TryGetValue(name, out var failure))
            throw failure;
        return Properties.GetValueOrDefault(name);
    }

    /// <summary>Indexovaná vlastnost se v atrapě ukládá pod klíčem „Název(index)“.</summary>
    public object? GetProperty(string name, object?[] index)
    {
        var key = IndexedKey(name, index);
        if (Throws.TryGetValue(key, out var failure))
            throw failure;
        return Properties.GetValueOrDefault(key);
    }

    public void SetProperty(string name, object?[] index, object? value)
    {
        var key = IndexedKey(name, index);
        factory.Calls.Add(new ComCall(Name, $"set_{key}", [value]));
        Properties[key] = value;
    }

    private static string IndexedKey(string name, object?[] index)
        => $"{name}({string.Join(",", index.Select(i => i?.ToString()))})";

    public void SetProperty(string name, object? value)
    {
        factory.Calls.Add(new ComCall(Name, $"set_{name}", [value]));
        Properties[name] = value;
    }
}

/// <summary>Vytváří atrapy COM objektů a sbírá všechna volání napříč nimi.</summary>
public sealed class FakeComFactory : IComFactory
{
    public List<ComCall> Calls { get; } = [];

    public Dictionary<string, FakeComDispatch> Created { get; } = [];

    public IComDispatch Create(string progId)
    {
        if (Created.TryGetValue(progId, out var existing))
            return existing;

        return Created[progId] = new FakeComDispatch(progId, this);
    }

    public IComDispatch Wrap(object comObject) => (FakeComDispatch)comObject;

    /// <summary>Vyrobí atrapu datového objektu (karta, držitel, čtečka…) s danými vlastnostmi.</summary>
    public FakeComDispatch Record(string name, params (string Property, object? Value)[] properties)
    {
        var dispatch = new FakeComDispatch(name, this);
        foreach (var (property, value) in properties)
            dispatch.Properties[property] = value;
        return dispatch;
    }

    public ComCall Call(string method)
        => Calls.SingleOrDefault(c => c.Method == method)
           ?? throw new InvalidOperationException(
               $"Volání '{method}' neproběhlo. Proběhla: {string.Join(", ", Calls.Select(c => c.Method))}");

    public IReadOnlyList<ComCall> AllCalls(string method)
        => Calls.Where(c => c.Method == method).ToList();
}
