using System.Collections.Concurrent;

namespace Acs.WinPakConnector.Providers.Com.Signatures;

/// <summary>
/// Jedno zaznamenané volání metody WIN-PAKu: co konektor posílá (typy argumentů
/// tak, jak by je viděla pozdní vazba) a odkud v konektoru volání pochází.
/// </summary>
public sealed record RecordedCall(string Method, IReadOnlyList<SentArgument> Arguments, string Origin)
{
    public override string ToString() => $"{Method}({string.Join(", ", Arguments)})";
}

/// <summary>Argument tak, jak ho konektor posílá: typ ve VB6 názvosloví a zda je to jen místo pro výstup.</summary>
public sealed record SentArgument(string Type, bool Placeholder)
{
    public override string ToString() => Placeholder ? $"{Type} (prázdné)" : Type;

    public static SentArgument Of(object? value) => value switch
    {
        null => new("null", true),
        int i => new("Long", i == 0),
        long l => new("Long", l == 0),
        uint or ulong => new("Long", false),
        short s => new("Integer", s == 0),
        byte => new("Byte", false),
        bool b => new("Boolean", !b),
        string s => new("String", s.Length == 0),
        DateTime => new("Date", false),
        double or float => new("Double", false),
        decimal => new("Currency", false),
        int[] or long[] => new("Long()", false),
        string[] => new("String()", false),
        byte[] => new("Byte()", false),
        IComDispatch => new("Object", false),
        _ => new("Object", false),
    };
}

/// <summary>
/// Atrapa COM objektů, která zaznamenává, jaké metody a s jakými argumenty konektor
/// volá. Nic nepočítá — vrací jen tolik, aby se kód konektoru dostal k dalšímu volání
/// (přihlášení projde, účet se najde, seznamy jsou prázdné, zápisy hlásí úspěch).
/// Výsledkem je katalog volání podle příručky, který se pak porovná se skutečnými
/// signaturami objektu WIN-PAKu.
/// </summary>
public sealed class RecordingComFactory(WinPakComOptions options) : IComFactory
{
    private readonly ConcurrentDictionary<string, RecordedCall> _calls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Odkud v konektoru právě volání pochází (název metody API); nastavuje řidič záznamu.</summary>
    public string Origin { get; set; } = "";

    public IReadOnlyList<RecordedCall> Calls => _calls.Values.OrderBy(c => c.Method, StringComparer.OrdinalIgnoreCase).ToList();

    public IComDispatch Create(string progId) => new Recorder(this, progId, []);

    public IComDispatch Wrap(object comObject)
        => comObject as IComDispatch ?? new Recorder(this, "objekt", []);

    public void Release(IComDispatch dispatch)
    {
    }

    private Recorder Record(string name, params (string Property, object? Value)[] properties)
        => new(this, name, properties.ToDictionary(p => p.Property, p => p.Value, StringComparer.OrdinalIgnoreCase));

    private void Note(string method, object?[] args)
    {
        // První výskyt vyhrává — konektor volá metodu vždy se stejným tvarem.
        _calls.TryAdd(method, new RecordedCall(method, args.Select(SentArgument.Of).ToList(), Origin));
    }

    private sealed class Recorder(RecordingComFactory owner, string name, Dictionary<string, object?> properties) : IComDispatch
    {
        public object Target => this;

        public object? Invoke(string method, object?[] args)
        {
            owner.Note(method, args);

            switch (method)
            {
                case "Login":
                    args[^1] = 1;
                    return null;
                case "ConnectWPDatabase":
                    args[3] = 0;
                    return null;
                case "GetAccounts":
                    args[0] = new object[]
                    {
                        owner.Record("účet",
                            ("AccountID", 1), ("AccountName", string.IsNullOrWhiteSpace(owner.Options.AccountName) ? "Account" : owner.Options.AccountName)),
                    };
                    return null;
                case "GetSubAccountsByAccountID":
                    args[1] = new object[]
                    {
                        owner.Record("podúčet",
                            ("AccountID", 2), ("AccountName", string.IsNullOrWhiteSpace(owner.Options.SubAccountName) ? "Default" : owner.Options.SubAccountName)),
                    };
                    return null;
                case "InitServer" or "InitServer2":
                    return true;
                default:
                    // Výstupní kolekce zůstanou null (prázdné seznamy), stavové kódy 0 (úspěch).
                    return null;
            }
        }

        public object? GetProperty(string name) => properties.GetValueOrDefault(name);

        public object? GetProperty(string name, object?[] index) => null;

        public void SetProperty(string name, object? value) => properties[name] = value;

        public void SetProperty(string name, object?[] index, object? value)
        {
        }

        public override string ToString() => name;
    }

    private WinPakComOptions Options => options;
}
