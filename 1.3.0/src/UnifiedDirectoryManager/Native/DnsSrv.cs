using System.Runtime.InteropServices;

namespace UnifiedDirectoryManager.Native;

/// <summary>Result of one SRV record: target host, port, and selection weights.</summary>
internal sealed record SrvRecord(string Target, int Port, int Priority, int Weight);

/// <summary>
/// Minimal DNS SRV lookup via dnsapi.dll (DnsQuery_W). Used to discover domain controllers from
/// "_ldap._tcp.dc._msdcs.&lt;domain&gt;". Returns an empty list rather than throwing when DNS can't answer
/// (common on Entra-only clients without line-of-sight to corporate DNS).
/// </summary>
internal static class DnsSrv
{
    private const ushort DNS_TYPE_SRV = 33;
    private const uint DNS_QUERY_STANDARD = 0;
    private const int DnsFreeRecordList = 1;

    public static IReadOnlyList<SrvRecord> Query(string name)
    {
        IntPtr results = IntPtr.Zero;
        try
        {
            var status = DnsQuery_W(name, DNS_TYPE_SRV, DNS_QUERY_STANDARD, IntPtr.Zero, ref results, IntPtr.Zero);
            if (status != 0 || results == IntPtr.Zero)
                return Array.Empty<SrvRecord>();

            var list = new List<SrvRecord>();
            var ptr = results;
            while (ptr != IntPtr.Zero)
            {
                var rec = Marshal.PtrToStructure<DNS_SRV_RECORD>(ptr);
                if (rec.wType == DNS_TYPE_SRV && rec.pNameTarget != IntPtr.Zero)
                {
                    var target = Marshal.PtrToStringUni(rec.pNameTarget);
                    if (!string.IsNullOrEmpty(target))
                        list.Add(new SrvRecord(target.TrimEnd('.'), rec.wPort, rec.wPriority, rec.wWeight));
                }
                ptr = rec.pNext;
            }

            // RFC 2782 ordering: lowest priority first, then highest weight.
            return list
                .OrderBy(r => r.Priority)
                .ThenByDescending(r => r.Weight)
                .ToList();
        }
        catch
        {
            return Array.Empty<SrvRecord>();
        }
        finally
        {
            if (results != IntPtr.Zero)
                DnsRecordListFree(results, DnsFreeRecordList);
        }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr extra, ref IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll", EntryPoint = "DnsRecordListFree")]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);

    // Header fields followed by the SRV data union member. Pointer fields are native-sized,
    // so this layout is correct on both x64 and arm64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DNS_SRV_RECORD
    {
        public IntPtr pNext;
        public IntPtr pName;
        public ushort wType;
        public ushort wDataLength;
        public uint Flags;
        public uint dwTtl;
        public uint dwReserved;
        public IntPtr pNameTarget;
        public ushort wPriority;
        public ushort wWeight;
        public ushort wPort;
        public ushort Pad;
    }
}
