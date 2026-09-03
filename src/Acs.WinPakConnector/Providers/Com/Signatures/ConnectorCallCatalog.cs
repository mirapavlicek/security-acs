using System.Collections;
using System.Reflection;

namespace Acs.WinPakConnector.Providers.Com.Signatures;

/// <summary>
/// Katalog všech volání WIN-PAKu, která konektor umí — získaný tak, že se každá
/// veřejná metoda <see cref="WinPakDatabaseApi"/> a <see cref="WinPakCommApi"/>
/// spustí nad zaznamenávající atrapou se vzorovými argumenty. Kód konektoru je
/// přepis příručky WIN-PAK API, katalog je tedy „co říká příručka“; porovnává se
/// se skutečnými signaturami objektu (<see cref="SignatureCheck"/>).
/// </summary>
public static class ConnectorCallCatalog
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        // Životní cyklus a pomocné metody — nejsou to volání podle příručky.
        nameof(WinPakDatabaseApi.EnsureSession), nameof(WinPakDatabaseApi.Close),
        nameof(WinPakDatabaseApi.RecycleSession), nameof(WinPakDatabaseApi.InspectApplication),
        nameof(WinPakCommApi.EnsureStarted), nameof(WinPakCommApi.GetRecentEvents),
    };

    /// <summary>Zaznamenaná volání a metody API, které se nepodařilo spustit (s důvodem).</summary>
    public sealed record Catalog(IReadOnlyList<RecordedCall> Calls, IReadOnlyDictionary<string, string> Failures);

    public static Catalog Record(WinPakComOptions options)
    {
        var recorder = new RecordingComFactory(options);
        var failures = new Dictionary<string, string>();

        var database = new WinPakDatabaseApi(recorder, options);
        Exercise(database, recorder, failures);

        var comm = new WinPakCommApi(recorder, options);
        Exercise(comm, recorder, failures);

        return new Catalog(recorder.Calls, failures);
    }

    private static void Exercise(object api, RecordingComFactory recorder, Dictionary<string, string> failures)
    {
        var methods = api.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !Skipped.Contains(m.Name) && !m.IsGenericMethodDefinition)
            .OrderBy(m => m.Name, StringComparer.Ordinal);

        foreach (var method in methods)
        {
            recorder.Origin = $"{api.GetType().Name}.{method.Name}";
            try
            {
                method.Invoke(api, method.GetParameters().Select(p => Sample(p.ParameterType, p.Name ?? "")).ToArray());
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                // Atrapa vrací prázdno, takže řada metod skončí „nenalezeno“ — volání
                // do WIN-PAKu, o která jde, už jsou v tu chvíli zaznamenaná.
                failures[recorder.Origin] = inner.Message;
            }
            catch (Exception ex)
            {
                failures[recorder.Origin] = ex.Message;
            }
        }
    }

    /// <summary>Vzorová hodnota daného typu; u záznamů (request DTO) se skládá z konstruktoru.</summary>
    internal static object? Sample(Type type, string name = "")
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return Sample(underlying, name);

        if (type == typeof(string))
            return name.Contains("base64", StringComparison.OrdinalIgnoreCase) || name.Contains("content", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToBase64String([1, 2, 3])
                : "1";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return Convert.ChangeType(1, type);
        if (type == typeof(bool))
            return true;
        if (type == typeof(DateTime))
            return new DateTime(2026, 1, 1);
        if (type == typeof(CancellationToken))
            return CancellationToken.None;
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().FirstOrDefault(v => Convert.ToInt32(v) != 0) ?? Enum.GetValues(type).GetValue(0);

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var array = Array.CreateInstance(element, 1);
            array.SetValue(Sample(element), 0);
            return array;
        }

        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            list.Add(Sample(element));
            return list;
        }

        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
            return type.IsValueType ? Activator.CreateInstance(type) : null;

        return constructor.Invoke(constructor.GetParameters().Select(p => Sample(p.ParameterType, p.Name ?? "")).ToArray());
    }
}
