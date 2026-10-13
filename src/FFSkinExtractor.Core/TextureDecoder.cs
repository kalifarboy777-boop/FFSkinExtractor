using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Etc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using System.Buffers.Binary;
using AssetsTools.NET;

namespace FFSkinExtractor.Core;

public sealed record DecodedTexture(string Name, int Width, int Height, byte[] Png);

/// <summary>Convertit les Texture2D du jeu (ASTC, ETC, RGBA…) en PNG.</summary>
public static class TextureDecoder
{
    public static DecodedTexture Decode(GameBundle bundle, AssetFileInfo info)
    {
        var tex = bundle.BaseField(info);
        string name = tex["m_Name"].AsString;
        int w = tex["m_Width"].AsInt, h = tex["m_Height"].AsInt;
        int format = tex["m_TextureFormat"].AsInt;

        byte[] data = tex["image data"].AsByteArray;
        var stream = tex["m_StreamData"];
        if ((data == null || data.Length == 0) && !stream.IsDummy && stream["size"].AsUInt > 0)
            data = bundle.ReadStreamData(stream["path"].AsString, (long)stream["offset"].AsULong, (int)stream["size"].AsUInt);

        byte[] bgra = DecodeToBgra(data!, w, h, format, name);
        // Unity stocke les lignes de bas en haut
        return new DecodedTexture(name, w, h, PngWriter.Encode(bgra, w, h, flipY: true));
    }

    /// <summary>
    /// Décode une Texture2D à partir de ses octets bruts, sans typetree :
    /// m_Name, m_ForcedFallbackFormat, 2 booléens alignés, largeur, hauteur, taille, mipsStripped, format…
    /// puis l'image, précédée de sa taille.
    /// </summary>
    public static DecodedTexture? DecodeRaw(byte[] raw)
    {
        var r = raw.AsSpan();
        int len = BinaryPrimitives.ReadInt32LittleEndian(r);
        string name = System.Text.Encoding.UTF8.GetString(r.Slice(4, len));
        int p = (4 + len + 3) & ~3;
        p += 4 + 4;
        int w = BinaryPrimitives.ReadInt32LittleEndian(r[p..]);
        int h = BinaryPrimitives.ReadInt32LittleEndian(r[(p + 4)..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(r[(p + 8)..]);
        int format = BinaryPrimitives.ReadInt32LittleEndian(r[(p + 16)..]);
        if (w <= 0 || h <= 0 || size <= 0 || size > raw.Length) return null;
        for (int q = p + 20; q + 4 + size <= raw.Length; q += 4)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(r[q..]) != size) continue;
            var bgra = DecodeToBgra(r.Slice(q + 4, size).ToArray(), w, h, format, name);
            return new DecodedTexture(name, w, h, PngWriter.Encode(bgra, w, h, flipY: true));
        }
        return null;
    }

    static byte[] DecodeToBgra(byte[] data, int w, int h, int format, string name)
    {
        byte[] output;
        switch (format)
        {
            case >= 48 and <= 59: // ASTC RGB/RGBA 4x4 … 12x12
                int block = ((format - 48) % 6) switch { 0 => 4, 1 => 5, 2 => 6, 3 => 8, 4 => 10, _ => 12 };
                AstcDecoder.DecodeASTC<ColorBGRA<byte>, byte>(data, w, h, block, block, out output);
                return output;
            case 34: EtcDecoder.DecompressETC<ColorBGRA<byte>, byte>(data, w, h, out output); return output;
            case 45: EtcDecoder.DecompressETC2<ColorBGRA<byte>, byte>(data, w, h, out output); return output;
            case 46: EtcDecoder.DecompressETC2A1<ColorBGRA<byte>, byte>(data, w, h, out output); return output;
            case 47: EtcDecoder.DecompressETC2A8<ColorBGRA<byte>, byte>(data, w, h, out output); return output;
            case 4: // RGBA32
                output = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    output[i * 4] = data[i * 4 + 2]; output[i * 4 + 1] = data[i * 4 + 1];
                    output[i * 4 + 2] = data[i * 4]; output[i * 4 + 3] = data[i * 4 + 3];
                }
                return output;
            case 3: // RGB24
                output = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    output[i * 4] = data[i * 3 + 2]; output[i * 4 + 1] = data[i * 3 + 1];
                    output[i * 4 + 2] = data[i * 3]; output[i * 4 + 3] = 255;
                }
                return output;
            default:
                throw new NotSupportedException($"Texture {name} : format {format} pas encore géré");
        }
    }
}
