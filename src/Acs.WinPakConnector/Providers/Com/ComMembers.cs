using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Čtení vlastností, u kterých si příručka a skutečný WIN-PAK nerozumí v názvu.
///
/// Objekty poznámkových polí podle příručky mají <c>NoteFieldName</c>; na ostrém
/// serveru přišlo „Unknown name“. Místo hádání dalšího názvu se zkusí několik
/// obvyklých variant a když nesedí žádná, vypíšou se skutečné členy objektu
/// z jeho typové informace — příště se tak název doplní najisto.
/// </summary>
public static class ComMembers
{
    private const int UnknownName = unchecked((int)0x80020006);

    /// <summary>
    /// První z uvedených vlastností, kterou objekt zná a která má hodnotu. Když
    /// existuje jen s prázdnou hodnotou, vrátí null; když neexistuje žádná, chyba
    /// s výpisem členů.
    /// </summary>
    public static object? ReadAny(IComDispatch target, params string[] candidates)
    {
        var anyExists = false;
        foreach (var name in candidates)
        {
            try
            {
                var value = target.GetProperty(name);
                anyExists = true;
                if (value is not null && value is not DBNull)
                    return value;
            }
            catch (ComCallException ex) when (IsUnknownName(ex))
            {
            }
        }

        if (anyExists)
            return null;

        var members = Describe(target);
        var hint = members.Count == 0
            ? "typová informace objektu není k dispozici"
            : $"objekt má členy: {string.Join(", ", members)}";
        throw new ComCallException(
            string.Join("/", candidates),
            new COMException($"Žádná z vlastností {string.Join(", ", candidates)} neexistuje — {hint}.", UnknownName))
        {
            Data = { ["members"] = members },
        };
    }

    public static bool IsUnknownName(ComCallException ex)
        => ex.InnerException is COMException { HResult: UnknownName } or MissingMemberException;

    /// <summary>
    /// Názvy členů COM objektu podle jeho ITypeInfo (metody i vlastnosti, bez
    /// IUnknown/IDispatch). Mimo Windows nebo bez typové informace prázdné.
    /// </summary>
    public static IReadOnlyList<string> Describe(IComDispatch target)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            return DescribeWindows(target.Target);
        }
        catch (Exception)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> DescribeWindows(object comObject)
    {
        if (comObject is not IDispatchInfo dispatch)
            return [];

        dispatch.GetTypeInfoCount(out var count);
        if (count == 0)
            return [];

        dispatch.GetTypeInfo(0, 0, out var typeInfo);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        typeInfo.GetTypeAttr(out var attrPtr);
        try
        {
            var attr = Marshal.PtrToStructure<TYPEATTR>(attrPtr);
            for (var i = 0; i < attr.cFuncs; i++)
            {
                typeInfo.GetFuncDesc(i, out var funcPtr);
                try
                {
                    var func = Marshal.PtrToStructure<FUNCDESC>(funcPtr);
                    // Záporné memid = členy IUnknown/IDispatch, ty nikoho nezajímají.
                    if (func.memid < 0)
                        continue;

                    var buffer = new string[1];
                    typeInfo.GetNames(func.memid, buffer, 1, out var got);
                    if (got > 0 && !string.IsNullOrEmpty(buffer[0]))
                        names.Add(buffer[0]);
                }
                finally
                {
                    typeInfo.ReleaseFuncDesc(funcPtr);
                }
            }

            for (var i = 0; i < attr.cVars; i++)
            {
                typeInfo.GetVarDesc(i, out var varPtr);
                try
                {
                    var variable = Marshal.PtrToStructure<VARDESC>(varPtr);
                    var buffer = new string[1];
                    typeInfo.GetNames(variable.memid, buffer, 1, out var got);
                    if (got > 0 && !string.IsNullOrEmpty(buffer[0]))
                        names.Add(buffer[0]);
                }
                finally
                {
                    typeInfo.ReleaseVarDesc(varPtr);
                }
            }
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(attrPtr);
        }

        return [.. names];
    }

    /// <summary>Jen část IDispatch potřebná k získání typové informace.</summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchInfo
    {
        void GetTypeInfoCount(out uint count);

        void GetTypeInfo(uint index, uint lcid, [MarshalAs(UnmanagedType.Interface)] out ITypeInfo typeInfo);
    }
}
