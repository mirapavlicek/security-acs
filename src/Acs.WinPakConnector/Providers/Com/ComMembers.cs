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
    /// <summary>Parametr metody podle typové informace.</summary>
    public sealed record ComParameter(string Name, string Type, bool ByRef, bool Optional)
    {
        public override string ToString()
            => $"{(Optional ? "Optional " : "")}{(ByRef ? "ByRef " : "ByVal ")}{Name} As {Type}";
    }

    /// <summary>Skutečná signatura metody COM objektu.</summary>
    public sealed record ComMethodSignature(string Name, IReadOnlyList<ComParameter> Parameters, string? ReturnType)
    {
        public int RequiredCount => Parameters.Count(p => !p.Optional);

        public override string ToString()
            => $"{Name}({string.Join(", ", Parameters)}){(ReturnType is null ? "" : $" As {ReturnType}")}";
    }

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

    /// <summary>
    /// Skutečná signatura metody podle typové informace objektu — název, parametry
    /// s typy a směrem, návratový typ. Příručka WIN-PAKu se od ní liší (u
    /// <c>AddUpdateCard</c> jiný počet parametrů); z typové informace je to najisto.
    /// Mimo Windows nebo bez typové informace null.
    /// </summary>
    public static ComMethodSignature? DescribeMethod(IComDispatch target, string method)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return DescribeMethodWindows(target.Target, method);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static ComMethodSignature? DescribeMethodWindows(object comObject, string method)
    {
        if (comObject is not IDispatchInfo dispatch)
            return null;

        dispatch.GetTypeInfoCount(out var count);
        if (count == 0)
            return null;

        dispatch.GetTypeInfo(0, 0, out var typeInfo);
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
                    if (func.memid < 0 || func.invkind != INVOKEKIND.INVOKE_FUNC)
                        continue;

                    var names = new string[func.cParams + 1];
                    typeInfo.GetNames(func.memid, names, names.Length, out var got);
                    if (got == 0 || !string.Equals(names[0], method, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parameters = new List<ComParameter>();
                    var returnType = TypeName(typeInfo, func.elemdescFunc.tdesc);
                    var size = Marshal.SizeOf<ELEMDESC>();
                    for (var p = 0; p < func.cParams; p++)
                    {
                        var elem = Marshal.PtrToStructure<ELEMDESC>(func.lprgelemdescParam + p * size);
                        var flags = (PARAMFLAG)elem.desc.paramdesc.wParamFlags;
                        var type = TypeName(typeInfo, elem.tdesc);
                        if (flags.HasFlag(PARAMFLAG.PARAMFLAG_FRETVAL))
                        {
                            // VB6 funkce: návratová hodnota je v typové knihovně poslední
                            // [out, retval] parametr; přes IDispatch se nepředává.
                            returnType = type.TrimStart('&');
                            continue;
                        }

                        var name = p + 1 < got ? names[p + 1] : $"p{p + 1}";
                        parameters.Add(new ComParameter(name, type.TrimStart('&'), type.StartsWith('&'),
                            flags.HasFlag(PARAMFLAG.PARAMFLAG_FOPT)));
                    }

                    return new ComMethodSignature(names[0], parameters, returnType is "HRESULT" or "void" ? null : returnType);
                }
                finally
                {
                    typeInfo.ReleaseFuncDesc(funcPtr);
                }
            }
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(attrPtr);
        }

        return null;
    }

    /// <summary>Název typu jako ve VB6; ukazatel (ByRef) se značí úvodním <c>&amp;</c>.</summary>
    [SupportedOSPlatform("windows")]
    private static string TypeName(ITypeInfo typeInfo, TYPEDESC desc)
    {
        switch ((VarEnum)desc.vt)
        {
            case VarEnum.VT_PTR:
                return "&" + TypeName(typeInfo, Marshal.PtrToStructure<TYPEDESC>(desc.lpValue)).TrimStart('&');
            case VarEnum.VT_SAFEARRAY:
                return TypeName(typeInfo, Marshal.PtrToStructure<TYPEDESC>(desc.lpValue)) + "()";
            case VarEnum.VT_USERDEFINED:
                try
                {
                    typeInfo.GetRefTypeInfo((int)desc.lpValue, out var referenced);
                    referenced.GetDocumentation(-1, out var name, out _, out _, out _);
                    return name ?? "Object";
                }
                catch (Exception)
                {
                    return "Object";
                }
            default:
                return VbTypeName((VarEnum)desc.vt);
        }
    }

    public static string VbTypeName(VarEnum vt) => vt switch
    {
        VarEnum.VT_I2 => "Integer",
        VarEnum.VT_I4 or VarEnum.VT_INT => "Long",
        VarEnum.VT_UI4 or VarEnum.VT_UINT => "UInt32",
        VarEnum.VT_I8 => "LongLong",
        VarEnum.VT_UI1 => "Byte",
        VarEnum.VT_R4 => "Single",
        VarEnum.VT_R8 => "Double",
        VarEnum.VT_CY => "Currency",
        VarEnum.VT_DATE => "Date",
        VarEnum.VT_BSTR => "String",
        VarEnum.VT_BOOL => "Boolean",
        VarEnum.VT_VARIANT => "Variant",
        VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN => "Object",
        VarEnum.VT_HRESULT => "HRESULT",
        VarEnum.VT_VOID => "void",
        _ => vt.ToString(),
    };

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
