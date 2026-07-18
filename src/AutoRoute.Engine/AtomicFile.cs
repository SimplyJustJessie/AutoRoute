using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AutoRoute.Engine;

/// <summary>
/// The atomic-write primitive shared by <see cref="RuleStore"/> and <see cref="SinkDropInWriter"/>:
/// write a sibling temp file, set its mode, then <see cref="File.Move(string,string,bool)"/> it over
/// the target — a reader never sees a torn write, and a failed write leaves no partial file behind.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAsync(string path, string text, UnixFileMode mode, CancellationToken ct = default)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tempPath, text, ct).ConfigureAwait(false);
            // Final mode before the move — the file lands at its final path already restricted.
            File.SetUnixFileMode(tempPath, mode);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
