using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace FFSkinExtractor.Core;

/// <summary>Encodeur PNG minimal (RGBA 8 bits), pour éviter une dépendance externe.</summary>
public static class PngWriter
{
    /// <param name="bgra">Pixels BGRA, ligne du haut en premier si <paramref name="flipY"/> est faux.</param>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, bool flipY)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (int y = 0; y < height; y++)
        {
            int src = (flipY ? height - 1 - y : y) * width * 4;
            int dst = y * (width * 4 + 1);
            raw[dst++] = 0; // filtre « None »
            for (int x = 0; x < width; x++, src += 4, dst += 4)
            {
                raw[dst] = bgra[src + 2];
                raw[dst + 1] = bgra[src + 1];
                raw[dst + 2] = bgra[src];
                raw[dst + 3] = bgra[src + 3];
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 6; // 8 bits, RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc.GetCurrentHashAsUInt32());
        s.Write(c);
    }
}
