using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Acs.WinPakConnector.Providers.Com;

/// <summary>
/// Záznam (VB6 <c>Type</c>, COM VT_RECORD) přečtený po polích — chová se jako objekt
/// s vlastnostmi, aby s ním mapování konektoru pracovalo stejně jako s COM objektem.
/// </summary>
public sealed class RecordDispatch(IReadOnlyDictionary<string, object?> fields) : IComDispatch
{
    public object Target => this;

    public IReadOnlyDictionary<string, object?> Fields => fields;

    public object? GetProperty(string name) => fields.GetValueOrDefault(name);

    public object? GetProperty(string name, object?[] index) => null;

    public object? Invoke(string method, object?[] args)
        => throw new ComCallException(method, new MissingMethodException($"Záznam WIN-PAKu nemá metody; má pole {string.Join(", ", fields.Keys)}."));

    public void SetProperty(string name, object? value)
        => throw new ComCallException(name, new MissingMemberException("Záznam WIN-PAKu je jen ke čtení."));

    public void SetProperty(string name, object?[] index, object? value)
        => throw new ComCallException(name, new MissingMemberException("Záznam WIN-PAKu je jen ke čtení."));

    public override string ToString() => string.Join(", ", fields.Select(f => $"{f.Key}={f.Value}"));
}

/// <summary>
/// Volání COM metody, která vrací strukturované záznamy (VT_RECORD, typicky pole VB6
/// <c>Type</c>). Pozdní vazba .NET je odmítá („The specified record cannot be mapped
/// to a managed value class“) — na ostrém WIN-PAKu tak padá <c>ListConnectedDevices</c>.
/// Tady se <c>IDispatch::Invoke</c> volá přímo, výsledný VARIANT se přečte ručně a
/// záznamy se rozeberou po polích přes <c>IRecordInfo</c>. Argumenty se předávají
/// hodnotou (řetězec, číslo, logická hodnota, prázdno) — metody vracející záznamy
/// výstupní parametry nemají.
/// </summary>
public static class RecordInvoker
{
    private const int DispatchMethod = 1;
    private const int LocaleUserDefault = 0x0400;
    private const ushort VtEmpty = 0, VtI4 = 3, VtBstr = 8, VtBool = 11, VtVariant = 12, VtRecord = 36, VtArray = 0x2000;

    /// <summary>Rozpozná odmítnutí .NET marshalleru, kvůli kterému se sem volání přesměruje.</summary>
    public static bool IsUnmappableRecord(Exception ex)
        => (ex.Message?.Contains("cannot be mapped to a managed value class", StringComparison.OrdinalIgnoreCase) ?? false)
           || (ex.InnerException is { } inner && IsUnmappableRecord(inner));

    [SupportedOSPlatform("windows")]
    public static object? Invoke(object comObject, string method, object?[] args)
    {
        if (comObject is not IDispatchRaw dispatch)
            throw new InvalidOperationException("Objekt není COM objekt s IDispatch — záznamy z něj přečíst nejde.");

        var names = new[] { method };
        var dispIds = new int[1];
        var iid = Guid.Empty;
        dispatch.GetIDsOfNames(ref iid, names, 1, LocaleUserDefault, dispIds);

        var variantSize = Marshal.SizeOf<Variant>();
        var argsMemory = IntPtr.Zero;
        var resultMemory = Marshal.AllocCoTaskMem(variantSize);
        var bstrs = new List<IntPtr>();
        try
        {
            VariantInit(resultMemory);
            if (args.Length > 0)
            {
                argsMemory = Marshal.AllocCoTaskMem(variantSize * args.Length);
                // DISPPARAMS chce argumenty v obráceném pořadí.
                for (var i = 0; i < args.Length; i++)
                {
                    var slot = argsMemory + variantSize * (args.Length - 1 - i);
                    VariantInit(slot);
                    Marshal.StructureToPtr(ToVariant(args[i], bstrs), slot, false);
                }
            }

            var parameters = new DISPPARAMS { cArgs = args.Length, rgvarg = argsMemory, cNamedArgs = 0, rgdispidNamedArgs = IntPtr.Zero };
            var excepInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<EXCEPINFO>());
            Marshal.Copy(new byte[Marshal.SizeOf<EXCEPINFO>()], 0, excepInfo, Marshal.SizeOf<EXCEPINFO>());
            try
            {
                var hr = dispatch.Invoke(dispIds[0], ref iid, LocaleUserDefault, DispatchMethod, ref parameters, resultMemory, excepInfo, IntPtr.Zero);
                if (hr < 0)
                {
                    var info = Marshal.PtrToStructure<EXCEPINFO>(excepInfo);
                    throw new COMException(string.IsNullOrEmpty(info.bstrDescription) ? Marshal.GetExceptionForHR(hr)?.Message : info.bstrDescription, hr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(excepInfo);
            }

            return Read(Marshal.PtrToStructure<Variant>(resultMemory));
        }
        finally
        {
            VariantClear(resultMemory);
            Marshal.FreeCoTaskMem(resultMemory);
            if (argsMemory != IntPtr.Zero)
                Marshal.FreeCoTaskMem(argsMemory);
            foreach (var bstr in bstrs)
                Marshal.FreeBSTR(bstr);
        }
    }

    private static Variant ToVariant(object? value, List<IntPtr> bstrs)
    {
        switch (value)
        {
            case null:
                return new Variant { vt = VtEmpty };
            case string s:
                var bstr = Marshal.StringToBSTR(s);
                bstrs.Add(bstr);
                return new Variant { vt = VtBstr, data1 = bstr };
            case bool b:
                return new Variant { vt = VtBool, data1 = (IntPtr)(b ? -1 : 0) };
            case int or long or short or byte or uint:
                return new Variant { vt = VtI4, data1 = (IntPtr)Convert.ToInt32(value) };
            default:
                throw new ArgumentException($"Argument typu {value.GetType().Name} přes přímé volání předat neumím.");
        }
    }

    /// <summary>VARIANT → .NET hodnota; záznamy a pole záznamů po polích, ostatní přes marshaller.</summary>
    [SupportedOSPlatform("windows")]
    private static object? Read(Variant variant)
    {
        if (variant.vt == (VtArray | VtRecord))
            return ReadRecordArray(variant.data1);
        if (variant.vt == VtRecord)
            return ReadRecord(variant.data1, (IRecordInfo)Marshal.GetObjectForIUnknown(variant.data2));
        if (variant.vt == (VtArray | VtVariant))
            return ReadVariantArray(variant.data1);

        var memory = Marshal.AllocCoTaskMem(Marshal.SizeOf<Variant>());
        try
        {
            Marshal.StructureToPtr(variant, memory, false);
            return Marshal.GetObjectForNativeVariant(memory);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object?[] ReadRecordArray(IntPtr safeArray)
    {
        var recordInfo = SafeArrayGetRecordInfo(safeArray);
        var elementSize = SafeArrayGetElemsize(safeArray);
        var lower = SafeArrayGetLBound(safeArray, 1);
        var upper = SafeArrayGetUBound(safeArray, 1);
        var count = Math.Max(0, upper - lower + 1);

        var data = SafeArrayAccessData(safeArray);
        try
        {
            var result = new object?[count];
            for (var i = 0; i < count; i++)
                result[i] = ReadRecord(data + i * (int)elementSize, recordInfo);
            return result;
        }
        finally
        {
            SafeArrayUnaccessData(safeArray);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object?[] ReadVariantArray(IntPtr safeArray)
    {
        var lower = SafeArrayGetLBound(safeArray, 1);
        var upper = SafeArrayGetUBound(safeArray, 1);
        var count = Math.Max(0, upper - lower + 1);
        var size = Marshal.SizeOf<Variant>();

        var data = SafeArrayAccessData(safeArray);
        try
        {
            var result = new object?[count];
            for (var i = 0; i < count; i++)
                result[i] = Read(Marshal.PtrToStructure<Variant>(data + i * size));
            return result;
        }
        finally
        {
            SafeArrayUnaccessData(safeArray);
        }
    }

    [SupportedOSPlatform("windows")]
    private static RecordDispatch ReadRecord(IntPtr record, IRecordInfo info)
    {
        uint count = 0;
        info.GetFieldNames(ref count, null);
        var names = new string[count];
        info.GetFieldNames(ref count, names);

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            try
            {
                info.GetField(record, name, out var value);
                fields[name] = value;
            }
            catch (Exception ex)
            {
                // Vnořený záznam nebo typ, který marshaller nezná — pole se vynechá, ostatní zůstanou.
                fields[name] = $"<{ex.GetType().Name}>";
            }
        }

        return new RecordDispatch(fields);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data1;
        public IntPtr data2;
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchRaw
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, int lcid, out IntPtr typeInfo);
        void GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names, int count, int lcid, [Out] int[] dispIds);
        [PreserveSig] int Invoke(int dispId, ref Guid riid, int lcid, short flags, ref DISPPARAMS parameters, IntPtr result, IntPtr excepInfo, IntPtr argErr);
    }

    /// <summary>Jen část IRecordInfo potřebná ke čtení polí; pořadí metod odpovídá vtable.</summary>
    [ComImport]
    [Guid("0000002F-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRecordInfo
    {
        void RecordInit(IntPtr pvNew);
        void RecordClear(IntPtr pvExisting);
        void RecordCopy(IntPtr pvExisting, IntPtr pvNew);
        void GetGuid(out Guid guid);
        void GetName([MarshalAs(UnmanagedType.BStr)] out string name);
        void GetSize(out uint size);
        void GetTypeInfo(out ITypeInfo typeInfo);
        void GetField(IntPtr pvData, [MarshalAs(UnmanagedType.LPWStr)] string fieldName, [MarshalAs(UnmanagedType.Struct)] out object value);
        void GetFieldNoCopy(IntPtr pvData, [MarshalAs(UnmanagedType.LPWStr)] string fieldName, [MarshalAs(UnmanagedType.Struct)] out object value, out IntPtr dataCArray);
        void PutField(uint flags, IntPtr pvData, [MarshalAs(UnmanagedType.LPWStr)] string fieldName, [MarshalAs(UnmanagedType.Struct)] ref object value);
        void PutFieldNoCopy(uint flags, IntPtr pvData, [MarshalAs(UnmanagedType.LPWStr)] string fieldName, [MarshalAs(UnmanagedType.Struct)] ref object value);
        void GetFieldNames(ref uint count, [In, Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.BStr, SizeParamIndex = 0)] string[]? names);
    }

    [DllImport("oleaut32.dll")]
    private static extern void VariantInit(IntPtr variant);

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr variant);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern IRecordInfo SafeArrayGetRecordInfo(IntPtr safeArray);

    [DllImport("oleaut32.dll")]
    private static extern uint SafeArrayGetElemsize(IntPtr safeArray);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern int SafeArrayGetLBound(IntPtr safeArray, uint dimension);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern int SafeArrayGetUBound(IntPtr safeArray, uint dimension);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern IntPtr SafeArrayAccessData(IntPtr safeArray);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void SafeArrayUnaccessData(IntPtr safeArray);
}
