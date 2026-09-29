using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Core.Content;

/// <summary>PNG (RGBA, 8 bits per channel) from pixels read back from Unity. Runs on any thread.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes <paramref name="image"/> (rows bottom to top) as a PNG (rows top to bottom).</summary>
    public static byte[] Encode(ImagePixels image)
    {
        var stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            // Filter type 0 (none) per row; PNG rows go top to bottom.
            Buffer.BlockCopy(image.Rgba, (image.Height - 1 - y) * stride, raw, (y * (stride + 1)) + 1, stride);
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)image.Width);
        WriteBigEndian(header, 4, (uint)image.Height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", Zlib(raw));
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>A zlib stream: compressed when the runtime can deflate, otherwise stored blocks (some game runtimes lack
    /// the native deflate library; stored blocks are still a valid PNG).</summary>
    internal static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        byte[] body;
        try
        {
            using var deflated = new MemoryStream();
            using (var deflate = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }

            body = deflated.ToArray();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeLoadException or NotSupportedException or TypeInitializationException)
        {
            body = Stored(data);
        }

        output.Write(body, 0, body.Length);
        var adler = Adler32(data);
        var tail = new byte[4];
        WriteBigEndian(tail, 0, adler);
        output.Write(tail, 0, 4);
        return output.ToArray();
    }

    private static byte[] Stored(byte[] data)
    {
        using var output = new MemoryStream();
        var offset = 0;
        do
        {
            var length = Math.Min(65535, data.Length - offset);
            var last = offset + length >= data.Length;
            output.WriteByte((byte)(last ? 1 : 0));
            output.WriteByte((byte)(length & 0xFF));
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)(~length & 0xFF));
            output.WriteByte((byte)((~length >> 8) & 0xFF));
            output.Write(data, offset, length);
            offset += length;
        }
        while (offset < data.Length);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length, 0, 4);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);
        var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes, 0, 4);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
