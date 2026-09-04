namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Prostá hodnota ze seznamu WIN-PAKu tvářící se jako objekt s vlastnostmi.
///
/// Příručka u řady výpisů slibuje objekty, skutečný WIN-PAK vrací seznam řetězců
/// (názvy) nebo čísel (id): pole vyhledávání držitelů, skupiny svátků panelu,
/// větve přístupových oblastí… Místo opravy každého mapování zvlášť, jak se
/// rozdíly objevují („Method 'System.String.DeviceID' not found“), se prostá
/// hodnota obalí: řetězec odpoví na vlastnosti s názvem (<c>*Name</c>), číslo na
/// vlastnosti s id (<c>*ID</c>), ostatní vlastnosti jsou prázdné. Mapování pak
/// dostane aspoň to, co WIN-PAK poslal, a stránka se zobrazí.
/// </summary>
public sealed class ScalarDispatch(object value) : IComDispatch
{
    public object Target => value;

    public object Value => value;

    public object? Invoke(string method, object?[] args)
        => throw new ComCallException(method,
            new MissingMethodException($"WIN-PAK vrátil prostou hodnotu '{value}', ne objekt — metoda {method} na ní není."));

    public object? GetProperty(string name)
    {
        var isName = name.EndsWith("Name", StringComparison.OrdinalIgnoreCase)
                     || name.EndsWith("Desc", StringComparison.OrdinalIgnoreCase)
                     || name.EndsWith("Description", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("Field", StringComparison.OrdinalIgnoreCase);
        var isId = name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("Index", StringComparison.OrdinalIgnoreCase);

        return value switch
        {
            string when isName => value,
            string when isId => null,
            string => null,
            _ when isId => value,
            _ => null,
        };
    }

    public object? GetProperty(string name, object?[] index) => null;

    public void SetProperty(string name, object? value)
        => throw new ComCallException(name, new MissingMemberException($"WIN-PAK vrátil prostou hodnotu, ne objekt — {name} nelze nastavit."));

    public void SetProperty(string name, object?[] index, object? value)
        => throw new ComCallException(name, new MissingMemberException($"WIN-PAK vrátil prostou hodnotu, ne objekt — {name} nelze nastavit."));

    public override string ToString() => value.ToString() ?? "";
}
