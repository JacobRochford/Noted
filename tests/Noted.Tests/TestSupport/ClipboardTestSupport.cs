using System.Runtime.InteropServices;

namespace Noted.Tests.Services;

internal static class ClipboardTestSupport
{
    // only retry clipboard access that can safely repeat, never paste or editor commands.
    internal static void RetryClipboard(Action action)
    {
        const int MaxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < MaxAttempts)
            {
                Thread.Sleep(50);
            }
        }
    }
}
