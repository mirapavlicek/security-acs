using System.Reflection;
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

    void SetProperty(string name, object? value);
}

/// <summary>Vytváří a obaluje COM objekty. V testech se nahrazuje atrapou.</summary>
public interface IComFactory
{
    /// <summary>Vytvoří COM objekt podle ProgID (např. <c>NCIHelper.Application</c>).</summary>
    IComDispatch Create(string progId);

    /// <summary>Obalí objekt vrácený z jiného COM volání (prvek kolekce, vnořený objekt).</summary>
    IComDispatch Wrap(object comObject);
}

/// <summary>Skutečná pozdní vazba přes <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComDispatch(object instance) : IComDispatch
{
    public object Target { get; } = instance;

    private object Instance => Target;

    public object? Invoke(string method, object?[] args)
    {
        // Všechny parametry se předávají by-ref, jinak by se [out] hodnoty
        // nedostaly zpět do pole args.
        var modifiers = new ParameterModifier(Math.Max(args.Length, 1));
        for (var i = 0; i < args.Length; i++)
            modifiers[i] = true;

        return Instance.GetType().InvokeMember(
            method,
            BindingFlags.InvokeMethod,
            binder: null,
            target: Instance,
            args: args,
            modifiers: args.Length > 0 ? [modifiers] : null,
            culture: null,
            namedParameters: null);
    }

    public object? GetProperty(string name)
        => Instance.GetType().InvokeMember(name, BindingFlags.GetProperty, null, Instance, null);

    public void SetProperty(string name, object? value)
        => Instance.GetType().InvokeMember(name, BindingFlags.SetProperty, null, Instance, [value]);
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
}
