using System.Runtime.InteropServices;

namespace DiskWeaver.Daemon;

// Raw libpam bindings -- source-generated (LibraryImport, not DllImport) and the conversation
// callback below is [UnmanagedCallersOnly], so this is safe under PublishAot: no
// Marshal.GetFunctionPointerForDelegate (needs a JIT-produced thunk, not available in NativeAOT)
// and no reflection-based marshaling. "libpam.so.0" (not "libpam" or "libpam.so") because that's
// the exact filename the runtime libpam0g package installs on Debian/Ubuntu -- the unversioned
// "libpam.so" symlink only exists in the -dev package, which a production host has no reason to
// have installed.
internal static partial class PamNative
{
    private const string LibPam = "libpam.so.0";

    // PAM message styles (security/_pam_types.h) -- what kind of prompt/notice a module is sending
    // to the conversation function.
    internal const int PAM_PROMPT_ECHO_OFF = 1;
    internal const int PAM_PROMPT_ECHO_ON = 2;
    internal const int PAM_ERROR_MSG = 3;
    internal const int PAM_TEXT_INFO = 4;

    internal const int PAM_SUCCESS = 0;
    internal const int PAM_CONV_ERR = 20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PamMessage
    {
        public int MsgStyle;
        public nint Msg;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PamResponse
    {
        public nint Resp;
        public int RespRetCode;
    }

    // Matches struct pam_conv exactly: a function pointer plus an opaque appdata_ptr libpam passes
    // through to every conv() call unchanged -- used here to carry a GCHandle to the password for
    // this one Authenticate() call, since the callback itself is a static method with no closure.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PamConv
    {
        public nint Conv;
        public nint AppdataPtr;
    }

    [LibraryImport(LibPam, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pam_start(string serviceName, string user, in PamConv pamConversation, out nint pamh);

    [LibraryImport(LibPam)]
    internal static partial int pam_authenticate(nint pamh, int flags);

    [LibraryImport(LibPam)]
    internal static partial int pam_acct_mgmt(nint pamh, int flags);

    [LibraryImport(LibPam)]
    internal static partial int pam_end(nint pamh, int pamStatus);

    [LibraryImport(LibPam)]
    internal static partial nint pam_strerror(nint pamh, int errnum);
}
