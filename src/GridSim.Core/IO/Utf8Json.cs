namespace GridSim.Core.IO;

/// <summary>
/// ANA-04: the bytes of a JSON input as the JSON reader takes them. <c>JsonDocument.Parse(byte[])</c> and
/// <c>JsonSerializer.Deserialize(ReadOnlySpan&lt;byte&gt;)</c> do not skip a UTF-8 byte order mark (only the stream overloads
/// do), and Windows PowerShell 5.1 (<c>Out-File -Encoding utf8</c>, <c>Set-Content -Encoding UTF8</c>), Notepad's "UTF-8
/// with BOM" and several editors write one. Every JSON input read as bytes goes through <see cref="WithoutBom"/> before it
/// is parsed; the bytes themselves are not changed, so a file's SHA-256 (its identity in a study) is the same as before.
/// </summary>
public static class Utf8Json
{
    /// <summary><paramref name="bytes"/> without a leading UTF-8 byte order mark (EF BB BF), if it has one.</summary>
    public static ReadOnlyMemory<byte> WithoutBom(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsMemory(3)
            : bytes.AsMemory();
    }
}
