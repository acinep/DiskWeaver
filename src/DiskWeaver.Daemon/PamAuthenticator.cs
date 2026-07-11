using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static DiskWeaver.Daemon.PamNative;

namespace DiskWeaver.Daemon;

// Authenticates a username/password pair against the host's own PAM stack -- the same mechanism
// a real login session or `sudo` goes through -- so the standalone SPA gets a login story for
// free instead of DiskWeaver inventing its own user store. Deliberately hand-rolled rather than
// an existing NuGet PAM wrapper: the only two that exist (Npam, PamSharp) are both abandoned
// (last releases 2021/2019) and predate NativeAOT; PAM's conversation callback is a native
// function pointer, and old-style P/Invoke marshals a delegate for that via
// Marshal.GetFunctionPointerForDelegate, which NativeAOT can't do (no JIT to produce the thunk).
// This authenticates one username/password pair with no interactive prompts, so the whole surface
// needed is small enough to own directly with an [UnmanagedCallersOnly] callback instead.
public static class PamAuthenticator
{
    // Requires an /etc/pam.d/<serviceName> file on the host -- see packaging/pam.d/diskweaver and
    // docs/deployment.md for what that installs and why.
    public static unsafe bool Authenticate(string serviceName, string username, string password, out string? errorMessage)
    {
        var passwordHandle = GCHandle.Alloc(password);
        try
        {
            delegate* unmanaged[Cdecl]<int, nint, nint, nint, int> conv = &ConversationCallback;
            var pamConv = new PamConv
            {
                Conv = (nint)conv,
                AppdataPtr = GCHandle.ToIntPtr(passwordHandle),
            };

            var startResult = pam_start(serviceName, username, in pamConv, out var pamh);
            if (startResult != PAM_SUCCESS)
            {
                // No pamh yet (pam_start itself failed) -- nothing valid to pass to pam_strerror.
                errorMessage = $"pam_start failed with code {startResult}";
                return false;
            }

            try
            {
                var authResult = pam_authenticate(pamh, 0);
                if (authResult != PAM_SUCCESS)
                {
                    errorMessage = DescribeError(pamh, authResult);
                    return false;
                }

                // pam_authenticate only proves the credential is correct -- pam_acct_mgmt is the
                // separate check for account-level restrictions (expired password, locked account,
                // time-of-day restrictions, etc.) that PAM modules apply independently.
                var acctResult = pam_acct_mgmt(pamh, 0);
                if (acctResult != PAM_SUCCESS)
                {
                    errorMessage = DescribeError(pamh, acctResult);
                    return false;
                }

                errorMessage = null;
                return true;
            }
            finally
            {
                pam_end(pamh, PAM_SUCCESS);
            }
        }
        finally
        {
            passwordHandle.Free();
        }
    }

    private static unsafe string DescribeError(nint pamh, int code)
    {
        var messagePtr = pam_strerror(pamh, code);
        return Marshal.PtrToStringUTF8(messagePtr) ?? $"PAM error {code}";
    }

    // Called directly by libpam (native code), never by managed callers -- must be
    // [UnmanagedCallersOnly] rather than a normal static method so NativeAOT emits a real native
    // entry point instead of relying on a reflection-built thunk. Only ever answers the one prompt
    // style this authentication flow expects (a password prompt with echo off); anything else
    // (text info/error banners some PAM modules emit, or an echo-on prompt) gets a null response,
    // which is the documented way to say "no answer" for that message.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ConversationCallback(int numMsg, nint msgArray, nint respArrayOut, nint appdataPtr)
    {
        try
        {
            var password = (string)GCHandle.FromIntPtr(appdataPtr).Target!;
            var responses = (PamResponse*)NativeMemory.AllocZeroed((nuint)numMsg, (nuint)sizeof(PamResponse));

            for (var i = 0; i < numMsg; i++)
            {
                var message = *(PamMessage*)(*((nint*)msgArray + i));
                if (message.MsgStyle == PAM_PROMPT_ECHO_OFF)
                {
                    responses[i].Resp = (nint)DuplicateAsNativeUtf8(password);
                }
            }

            *(nint*)respArrayOut = (nint)responses;
            return PAM_SUCCESS;
        }
        catch
        {
            // No response array on failure -- per the PAM conversation contract, libpam only reads
            // *resp when the conv function returns PAM_SUCCESS.
            return PAM_CONV_ERR;
        }
    }

    // Allocated with NativeMemory.Alloc (malloc-backed on every platform, not just CoTaskMem's
    // Unix behavior) because libpam free()s every non-null resp[i].resp itself once it's done with
    // it -- the conversation function is required to hand back malloc-compatible memory, and this
    // is the .NET API documented to guarantee that.
    private static unsafe byte* DuplicateAsNativeUtf8(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var buffer = (byte*)NativeMemory.Alloc((nuint)(byteCount + 1));
        var span = new Span<byte>(buffer, byteCount + 1);
        Encoding.UTF8.GetBytes(value, span);
        span[byteCount] = 0;
        return buffer;
    }
}
