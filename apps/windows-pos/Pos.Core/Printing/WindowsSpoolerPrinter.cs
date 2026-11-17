using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pos.Core.Printing;

/// <summary>
/// Raw printing through the Windows print spooler (winspool.drv): the bytes are passed unchanged to
/// the printer driver/port with the RAW datatype, which is how ESC/POS thermal printers are normally
/// driven on Windows. "Sent" means the spooler accepted the job — not that paper came out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSpoolerPrinter : IPrinterGateway
{
    private const int PrinterEnumLocal = 0x2;
    private const int PrinterEnumConnections = 0x4;
    private const int ErrorInsufficientBuffer = 122;
    private const int PrinterInfo2PointerFields = 13;
    private const int PrinterInfo2IntFields = 8;

    private const uint AttributeWorkOffline = 0x400;

    // PRINTER_STATUS_* flags that mean a job would not print now.
    private static readonly (uint Flag, string Label)[] BlockingStatuses =
    [
        (0x00000001, "En pause"),
        (0x00000002, "Erreur"),
        (0x00000004, "Suppression en cours"),
        (0x00000008, "Bourrage papier"),
        (0x00000010, "Plus de papier"),
        (0x00000080, "Hors ligne"),
        (0x00001000, "Non disponible"),
        (0x00100000, "Intervention requise"),
        (0x00400000, "Capot ouvert"),
    ];

    public IReadOnlyList<PrinterInfo> ListPrinters()
    {
        var defaultName = GetDefaultPrinterName();
        const int flags = PrinterEnumLocal | PrinterEnumConnections;
        EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(flags, null, 2, buffer, needed, out _, out var count))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var printers = new List<PrinterInfo>(count);
            for (var i = 0; i < count; i++)
            {
                printers.Add(ReadPrinterInfo2(buffer + (i * PrinterInfo2Size), defaultName));
            }

            return printers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public PrintAttemptResult PrintRaw(string printerName, string documentName, byte[] data) =>
        PrintRaw(printerName, documentName, data, outputFile: null);

    /// <param name="outputFile">Optional: makes the spooler write the job to a file instead of the port (tests).</param>
    internal static PrintAttemptResult PrintRaw(string printerName, string documentName, byte[] data, string? outputFile)
    {
        ArgumentNullException.ThrowIfNull(data);

        // --- Phase 1: nothing has been sent yet; any failure is a clean NotSent. ---
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            return PrintAttemptResult.NotSent($"Imprimante introuvable ou inaccessible ({Win32Message()}).");
        }

        try
        {
            var blocking = DescribeBlockingState(handle);
            if (blocking is not null)
            {
                return PrintAttemptResult.NotSent($"Imprimante indisponible : {blocking}.");
            }

            var docName = Marshal.StringToHGlobalUni(documentName);
            var dataType = Marshal.StringToHGlobalUni("RAW");
            var output = outputFile is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(outputFile);
            try
            {
                var docInfo = new DocInfo1 { DocName = docName, OutputFile = output, Datatype = dataType };
                var jobId = StartDocPrinter(handle, 1, ref docInfo);
                if (jobId == 0)
                {
                    return PrintAttemptResult.NotSent($"Le spouleur a refusé le travail ({Win32Message()}).");
                }

                // --- Phase 2: a spooler job exists. With "start printing immediately" (the Windows
                // default) bytes may reach the printer while still spooling, so failures are Uncertain. ---
                return WriteJob(handle, jobId, data);
            }
            finally
            {
                Marshal.FreeHGlobal(docName);
                Marshal.FreeHGlobal(dataType);
                if (output != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(output);
                }
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private static unsafe PrintAttemptResult WriteJob(IntPtr handle, int jobId, byte[] data)
    {
        if (!StartPagePrinter(handle))
        {
            var error = Win32Message();
            AbortPrinter(handle);
            return PrintAttemptResult.Uncertain($"Début de page refusé ({error}).", jobId);
        }

        int written;
        bool ok;
        fixed (byte* p = data)
        {
            ok = WritePrinter(handle, p, data.Length, out written);
        }

        if (!ok || written != data.Length)
        {
            var error = ok ? $"{written}/{data.Length} octets écrits" : Win32Message();
            AbortPrinter(handle);
            return PrintAttemptResult.Uncertain($"Écriture interrompue ({error}).", jobId);
        }

        if (!EndPagePrinter(handle) || !EndDocPrinter(handle))
        {
            return PrintAttemptResult.Uncertain($"Fin de travail non confirmée ({Win32Message()}).", jobId);
        }

        return PrintAttemptResult.Sent(jobId);
    }

    private static string? DescribeBlockingState(IntPtr handle)
    {
        GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            return null; // cannot query: let the spooler decide
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetPrinter(handle, 2, buffer, needed, out _))
            {
                return null;
            }

            var info = ReadPrinterInfo2(buffer, defaultName: null);
            return info.IsAvailable ? null : info.Status;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int PrinterInfo2Size
    {
        get
        {
            var raw = (PrinterInfo2PointerFields * IntPtr.Size) + (PrinterInfo2IntFields * sizeof(uint));
            return (raw + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
        }
    }

    /// <summary>Reads PRINTER_INFO_2 fields by offset (13 pointers followed by 8 DWORDs).</summary>
    private static PrinterInfo ReadPrinterInfo2(IntPtr p, string? defaultName)
    {
        string? Str(int index) => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, index * IntPtr.Size));
        uint Dword(int index) => (uint)Marshal.ReadInt32(p, (PrinterInfo2PointerFields * IntPtr.Size) + (index * sizeof(uint)));

        var name = Str(1) ?? string.Empty;
        var port = Str(3);
        var driver = Str(4);
        var attributes = Dword(0);
        var status = Dword(5);

        var problems = BlockingStatuses.Where(s => (status & s.Flag) != 0).Select(s => s.Label).ToList();
        if ((attributes & AttributeWorkOffline) != 0)
        {
            problems.Insert(0, "Hors ligne (Windows)");
        }

        var available = problems.Count == 0;
        return new PrinterInfo(
            name,
            string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
            available,
            available ? "Prête" : string.Join(", ", problems.Distinct()),
            port,
            driver);
    }

    private static unsafe string? GetDefaultPrinterName()
    {
        var size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = new char[size];
        fixed (char* p = buffer)
        {
            return GetDefaultPrinter(p, ref size) ? new string(p, 0, Math.Max(0, size - 1)) : null;
        }
    }

    private static string Win32Message()
    {
        var code = Marshal.GetLastPInvokeError();
        return code == 0 ? "erreur inconnue" : $"{new Win32Exception(code).Message} [{code}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DocInfo1
    {
        public IntPtr DocName;
        public IntPtr OutputFile;
        public IntPtr Datatype;
    }

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int bufferSize, out int needed, out int returned);

    [LibraryImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetDefaultPrinter(char* buffer, ref int size);

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr handle);

    [LibraryImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPrinter(IntPtr handle, int level, IntPtr buffer, int bufferSize, out int needed);

    [LibraryImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true)]
    private static partial int StartDocPrinter(IntPtr handle, int level, ref DocInfo1 docInfo);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartPagePrinter(IntPtr handle);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WritePrinter(IntPtr handle, byte* data, int count, out int written);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndPagePrinter(IntPtr handle);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndDocPrinter(IntPtr handle);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AbortPrinter(IntPtr handle);
}
