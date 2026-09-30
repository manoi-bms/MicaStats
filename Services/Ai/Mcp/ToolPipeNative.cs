using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Creates the tool pipe with a security descriptor that <see cref="System.IO.Pipes.PipeSecurity"/>
/// cannot express: a DACL that lets in only the current user, plus a medium mandatory
/// integrity label.
///
/// <para>
/// The label is why this is native code. A pipe created by an elevated MicaStats would carry
/// the high integrity level of its creator, and the no-write-up rule would then refuse the
/// unelevated bridge that Claude Desktop starts. <c>S:(ML;;NW;;;ME)</c> puts the pipe at medium,
/// so the same user's normal processes can connect while lower ones (a sandboxed browser tab)
/// still cannot. Setting it in the call that creates the pipe leaves no moment when the pipe
/// exists without it.
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode,
        uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// The descriptor for <paramref name="user"/>: a protected DACL (no inherited entries)
    /// granting only that SID full access, and a medium no-write-up integrity label.
    /// </summary>
    internal static string SddlFor(SecurityIdentifier user) => "D:P(A;;GA;;;" + user.Value + ")S:(ML;;NW;;;ME)";

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
        finally
        {
            LocalFree(sd);
        }
    }
}
