using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Creates the tool pipe with a security descriptor that <see cref="System.IO.Pipes.PipeSecurity"/>
/// cannot express: the current user as owner, a DACL that lets in only that user, plus a medium
/// mandatory integrity label that also blocks reads from below medium.
///
/// <para>
/// The label is why this is native code. A pipe created by an elevated MicaStats would carry
/// the high integrity level of its creator, and the no-write-up rule would then refuse the
/// unelevated bridge that Claude Desktop starts. <c>S:(ML;;NWNR;;;ME)</c> puts the pipe at medium
/// and denies both write-up and read-up, so the same user's normal processes can connect while
/// lower ones (a sandboxed browser tab) cannot connect at all, not even read-only. Setting it in
/// the call that creates the pipe leaves no moment when the pipe exists without it.
/// </para>
///
/// <para>
/// The explicit owner and label are also what a client checks (<see cref="IsTrustedServer"/>)
/// before it sends anything: a squatter that created the name first cannot produce both.
/// </para>
/// </summary>
internal static class ToolPipeNative
{
    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const uint SDDL_REVISION_1 = 1;
    private const int SE_KERNEL_OBJECT = 6;
    private const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    private const int MediumRid = 0x2000;

    /// <summary>Asks <see cref="ReadSddl"/> for the DACL.</summary>
    internal const uint DACL_SECURITY_INFORMATION = 0x00000004;

    /// <summary>Asks <see cref="ReadSddl"/> for the mandatory integrity label.</summary>
    internal const uint LABEL_SECURITY_INFORMATION = 0x00000010;

    /// <summary>Win32 ERROR_ACCESS_DENIED: the first-instance flag met a name that already exists.</summary>
    internal const int ErrorAccessDenied = 5;

    /// <summary>Win32 ERROR_PIPE_BUSY: every allowed instance of the name exists already.</summary>
    internal const int ErrorPipeBusy = 231;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out uint SecurityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr SecurityDescriptor, uint RequestedStringSDRevision, uint SecurityInformation,
        out IntPtr StringSecurityDescriptor, out uint StringSecurityDescriptorLen);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle handle, int ObjectType, uint SecurityInfo,
        IntPtr ppsidOwner, IntPtr ppsidGroup, IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    private static extern uint GetSecurityInfoWithOwner(SafeHandle handle, int ObjectType, uint SecurityInfo,
        out IntPtr ppsidOwner, IntPtr ppsidGroup, IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode,
        uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// The descriptor for <paramref name="user"/>: that SID as owner, a protected DACL (no
    /// inherited entries) granting only that SID full access, and a medium integrity label with
    /// no-write-up and no-read-up.
    /// </summary>
    internal static string SddlFor(SecurityIdentifier user) =>
        "O:" + user.Value + "D:P(A;;GA;;;" + user.Value + ")S:(ML;;NWNR;;;ME)";

    /// <summary>
    /// Creates one server instance of <c>\\.\pipe\&lt;pipeName&gt;</c>: duplex, overlapped, byte
    /// mode, remote clients rejected. <paramref name="firstInstance"/> adds
    /// FILE_FLAG_FIRST_PIPE_INSTANCE, so creation fails if any other process already owns the name
    /// (it could otherwise read every request the bridge sends). Every instance of one name must
    /// pass the same <paramref name="maxInstances"/>. Throws <see cref="Win32Exception"/>.
    /// </summary>
    internal static SafePipeHandle Create(string pipeName, string sddl, bool firstInstance, int maxInstances)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, out IntPtr sd, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0,
            };
            uint openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (firstInstance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0u);
            SafePipeHandle handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName, openMode,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                (uint)maxInstances, 0, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return handle;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// The SDDL text of one part (<see cref="DACL_SECURITY_INFORMATION"/> or
    /// <see cref="LABEL_SECURITY_INFORMATION"/>) of the descriptor of the object behind
    /// <paramref name="handle"/>. The handle needs READ_CONTROL, which a connected client end
    /// has. Throws <see cref="Win32Exception"/>.
    /// </summary>
    internal static string ReadSddl(SafeHandle handle, uint information)
    {
        uint rc = GetSecurityInfo(handle, SE_KERNEL_OBJECT, information, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
        if (rc != 0) throw new Win32Exception((int)rc);
        try
        {
            return DescriptorText(sd, information);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// True when the pipe behind <paramref name="handle"/> is owned by <paramref name="expectedOwner"/>
    /// and carries an explicit mandatory label of Medium or higher: what the real server sets and
    /// what a squatting process (default descriptor, no label) does not. False when either check
    /// fails or the descriptor cannot be read. The handle needs READ_CONTROL.
    /// </summary>
    internal static bool IsTrustedServer(SafeHandle handle, SecurityIdentifier expectedOwner)
    {
        try
        {
            uint rc = GetSecurityInfoWithOwner(handle, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION,
                out IntPtr ownerSid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
            if (rc != 0) return false;
            try
            {
                if (ownerSid == IntPtr.Zero) return false;
                if (!new SecurityIdentifier(ownerSid).Equals(expectedOwner)) return false;
                return LabelIsMediumOrHigher(DescriptorText(sd, LABEL_SECURITY_INFORMATION));
            }
            finally
            {
                LocalFree(sd);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when SDDL text holds a mandatory-label ACE (<c>ML</c>) whose level is Medium, Medium
    /// Plus, High or System, either as the alias or as <c>S-1-16-&lt;rid&gt;</c> at 0x2000 or above.
    /// </summary>
    internal static bool LabelIsMediumOrHigher(string sddl)
    {
        int start = sddl.IndexOf("(ML;", StringComparison.Ordinal);
        if (start < 0) return false;
        int end = sddl.IndexOf(')', start);
        if (end < 0) return false;
        string[] fields = sddl.Substring(start + 1, end - start - 1).Split(';');
        if (fields.Length < 6) return false;
        string level = fields[5];
        if (level is "ME" or "MP" or "HI" or "SI") return true;
        const string prefix = "S-1-16-";
        return level.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(level.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int rid) &&
            rid >= MediumRid;
    }

    private static string DescriptorText(IntPtr sd, uint information)
    {
        if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, SDDL_REVISION_1, information, out IntPtr text, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Marshal.PtrToStringUni(text) ?? "";
        }
        finally
        {
            LocalFree(text);
        }
    }
}
