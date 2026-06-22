using System.Runtime.InteropServices;
using System.Text;

namespace UnifiedDirectoryManager.Native;

/// <summary>
/// Thin P/Invoke wrapper over the Windows Credential Manager (advapi32 CredRead/Write/Delete).
/// Stores generic credentials in the per-user vault. Passwords are written as UTF-16 blobs.
/// </summary>
internal static class CredentialManager
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public static void Write(string target, string userName, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        if (blob.Length > 512 * 5)
            throw new ArgumentOutOfRangeException(nameof(secret), "Credential secret exceeds the 2560-byte limit.");

        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlob = blobPtr,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userName,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = null,
                TargetAlias = null,
            };

            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Array.Clear(blob, 0, blob.Length);
        }
    }

    public static bool TryRead(string target, out string userName, out string secret)
    {
        userName = string.Empty;
        secret = string.Empty;

        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return false;
            throw new InvalidOperationException($"CredRead failed (Win32 error {err}).");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            userName = cred.UserName ?? string.Empty;
            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
            {
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
                secret = Encoding.Unicode.GetString(bytes);
            }
            return true;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static bool Delete(string target)
    {
        if (CredDeleteW(target, CRED_TYPE_GENERIC, 0)) return true;
        var err = Marshal.GetLastWin32Error();
        if (err == ERROR_NOT_FOUND) return false;
        throw new InvalidOperationException($"CredDelete failed (Win32 error {err}).");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }
}
