using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Encoding-aware text file IO for the read/edit tools. `File.ReadAllTextAsync` +
/// `WriteAllTextAsync` silently strips BOMs, transcodes UTF-16 to UTF-8, and mangles
/// legacy-codepage files — corrupting bytes an edit never touched. This helper reads
/// the bytes, detects the encoding by BOM (strict UTF-8 otherwise), and hands back
/// the exact encoding to write with so a one-character edit stays a one-character diff.
/// </summary>
internal static class FileText
{
    /// <summary>
    /// Read a text file preserving its encoding identity. Throws
    /// <see cref="DecoderFallbackException"/> when the file has no BOM and is not
    /// valid UTF-8 — the caller decides whether to refuse (edit) or degrade (read).
    /// </summary>
    public static async Task<(string text, Encoding encoding)> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return Decode(bytes);
    }

    public static (string text, Encoding encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return (enc.GetString(bytes, 3, bytes.Length - 3), enc);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);

        // No BOM: decode as strict UTF-8 so a legacy-codepage file throws instead of
        // being silently rewritten full of U+FFFD replacement characters.
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return (strict.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>True when the first block of the file smells binary (NUL bytes).</summary>
    public static bool LooksBinary(byte[] bytes)
    {
        var scan = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < scan; i++)
            if (bytes[i] == 0) return true;
        return false;
    }
}
