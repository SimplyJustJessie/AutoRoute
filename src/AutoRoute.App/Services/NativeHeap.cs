using System;
using System.Runtime.InteropServices;

namespace AutoRoute.App.Services;

/// <summary>
/// Hands memory that native code (Skia, the GL driver, fonts) has already freed back to the system.
/// glibc keeps freed blocks in its per-thread arenas for reuse, so after a big release (the window
/// going away) most of it would otherwise stay resident for the life of the process.
/// </summary>
internal static class NativeHeap
{
    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    public static void TrimFreePages()
    {
        try { MallocTrim(0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not glibc (musl, say): nothing to trim, and nothing lost.
        }
    }
}
