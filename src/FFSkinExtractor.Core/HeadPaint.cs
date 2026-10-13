using System.Buffers.Binary;
using System.Text.RegularExpressions;
using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>Rectangle en coordonnées de texture (0 à 1, origine en bas à gauche comme dans Unity).</summary>
public readonly record struct UvRect(float X, float Y, float W, float H)
{
    public bool IsEmpty => W <= 0 || H <= 0;
}

/// <summary>
/// Calque peint sur la texture du visage : maquillage (« Headadditive ») ou racines de cheveux (« Hairroot »).
/// Ces overlays n'ont pas de maillage : le jeu les fusionne dans la texture de la tête.
/// </summary>
/// <param name="Alpha">Texture « _sa » : opacité en niveaux de gris, quand la couleur n'a pas de canal alpha.</param>
/// <param name="Dest">Zone de la texture du visage couverte (vide : toute la texture).</param>
/// <param name="Source">Zone de la texture du calque à prendre (overlays « WithAtlas »), sinon toute la texture.</param>
public sealed record HeadLayer(string Name, DecodedTexture Color, DecodedTexture? Alpha, UvRect Dest, UvRect? Source);

public enum HeadLayerKind { FacePaint, HairRoot }

public static partial class HeadPaint
{
    [GeneratedRegex(@"^(?<gender>male|female)_(?<kind>headadditive|hairroot)_(?<name>.+?)(?<facial>_facial)?(_(?<index>\d))?_overlay$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>Reconnaît un overlay de maquillage ou de racines : genre, type et nom de la tenue (« Cos_Aurora_White »).</summary>
    public static (string Gender, HeadLayerKind Kind, string Name)? Classify(string overlayName)
    {
        var m = NameRegex().Match(overlayName);
        if (!m.Success) return null;
        string g = m.Groups["gender"].Value;
        string gender = char.ToUpper(g[0]) + g[1..].ToLowerInvariant();
        var kind = m.Groups["kind"].Value.Equals("hairroot", StringComparison.OrdinalIgnoreCase) ? HeadLayerKind.HairRoot : HeadLayerKind.FacePaint;
        return (gender, kind, m.Groups["name"].Value);
    }

    /// <summary>
    /// Lit un overlay de calque. Disposition : m_Name, overlayName, un entier, le rectangle de destination,
    /// …, textureList, matériau ; les overlays « WithAtlas » ajoutent le rectangle source (4 flottants) à la fin.
    /// </summary>
    public static HeadLayer? Parse(string name, byte[] raw, bool atlas, Func<PPtr, bool> isTexture, Func<PPtr, DecodedTexture?> texture)
    {
        int end = atlas ? raw.Length - 16 : raw.Length;
        if (end < 64) return null;
        var refs = UmaAssets.OverlayTextures(raw[..end], isTexture);
        if (refs.Count == 0) return null;
        var color = texture(refs[0]);
        if (color?.Pixels == null) return null;
        var alpha = refs.Skip(1).Select(texture)
            .FirstOrDefault(t => t?.Pixels != null && t.Name.EndsWith("_sa", StringComparison.OrdinalIgnoreCase));

        var s = raw.AsSpan();
        int p = 28;
        p = SkipString(s, p);
        p = SkipString(s, p);
        var dest = new UvRect(F(s, p + 4), F(s, p + 8), F(s, p + 12), F(s, p + 16));
        UvRect? source = atlas ? new UvRect(F(s, end), F(s, end + 4), F(s, end + 8), F(s, end + 12)) : null;
        if (source is { IsEmpty: true }) source = null;
        return new HeadLayer(name, color, alpha, dest, source);
    }

    /// <summary>Peint les calques, dans l'ordre, sur une copie de la texture du visage.</summary>
    public static DecodedTexture Composite(DecodedTexture head, IReadOnlyList<HeadLayer> layers)
    {
        if (head.Pixels is not { } basePx || layers.Count == 0) return head;
        int w = head.Width, h = head.Height;
        var px = (byte[])basePx.Clone();
        foreach (var layer in layers)
        {
            var dest = layer.Dest.IsEmpty ? new UvRect(0, 0, 1, 1) : layer.Dest;
            var src = layer.Source ?? new UvRect(0, 0, 1, 1);
            int x0 = (int)MathF.Floor(dest.X * w), x1 = (int)MathF.Ceiling((dest.X + dest.W) * w);
            int y0 = (int)MathF.Floor(dest.Y * h), y1 = (int)MathF.Ceiling((dest.Y + dest.H) * h);
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            {
                float v = ((y + 0.5f) / h - dest.Y) / dest.H;
                if (v < 0 || v > 1) continue;
                int row = (h - 1 - y) * w; // pixels rangés ligne du haut en premier
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    float u = ((x + 0.5f) / w - dest.X) / dest.W;
                    if (u < 0 || u > 1) continue;
                    float su = src.X + u * src.W, sv = src.Y + v * src.H;
                    var (b, g, r, a) = Sample(layer.Color, su, sv);
                    if (layer.Alpha != null) a = Sample(layer.Alpha, su, sv).R;
                    if (a <= 0.002f) continue;
                    int i = (row + x) * 4;
                    px[i] = Blend(px[i], b, a);
                    px[i + 1] = Blend(px[i + 1], g, a);
                    px[i + 2] = Blend(px[i + 2], r, a);
                }
            }
        }
        string name = head.Name + "_" + string.Join("_", layers.Select(l => ShortName(l.Name)).Distinct());
        return DecodedTexture.FromBgra(name, w, h, px);
    }

    static string ShortName(string overlay) => Classify(overlay) is { } c ? c.Name : overlay;

    static byte Blend(byte dst, float src, float a) => (byte)Math.Clamp(MathF.Round(src * 255 * a + dst * (1 - a)), 0, 255);

    // Échantillonnage bilinéaire ; u, v avec l'origine en bas à gauche. Renvoie B, G, R, A entre 0 et 1.
    static (float B, float G, float R, float A) Sample(DecodedTexture t, float u, float v)
    {
        var px = t.Pixels!;
        float fx = Math.Clamp(u * t.Width - 0.5f, 0, t.Width - 1);
        float fy = Math.Clamp((1 - v) * t.Height - 0.5f, 0, t.Height - 1);
        int ix = (int)fx, iy = (int)fy;
        int ix1 = Math.Min(ix + 1, t.Width - 1), iy1 = Math.Min(iy + 1, t.Height - 1);
        float tx = fx - ix, ty = fy - iy;
        Span<float> c = stackalloc float[4];
        for (int k = 0; k < 4; k++)
        {
            float a = px[(iy * t.Width + ix) * 4 + k], b = px[(iy * t.Width + ix1) * 4 + k];
            float d = px[(iy1 * t.Width + ix) * 4 + k], e = px[(iy1 * t.Width + ix1) * 4 + k];
            c[k] = ((a * (1 - tx) + b * tx) * (1 - ty) + (d * (1 - tx) + e * tx) * ty) / 255f;
        }
        return (c[0], c[1], c[2], c[3]);
    }

    static float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);

    static int SkipString(ReadOnlySpan<byte> s, int p)
    {
        int len = BinaryPrimitives.ReadInt32LittleEndian(s[p..]);
        return (p + 4 + len + 3) & ~3;
    }

    /// <summary>Pièce qui porte la texture du visage : la tête elle-même, pas un chapeau, un masque ou des cheveux.</summary>
    public static bool IsFace(OutfitPart part)
    {
        if (part.Piece != "Head" || part.IsSkin || part.Material != MaterialKind.Opaque) return false;
        var words = part.SlotName.ToLowerInvariant().Split('_');
        if (!words.Contains("head") || words.Any(w => w is "hat" or "mask" or "headwear" or "helmet" or "accessory")) return false;
        // Le sous-maillage principal (pas « _1 », souvent une mèche ou un accessoire)
        return !int.TryParse(words[^1], out int i) || i == 0;
    }

    /// <summary>Remplace la texture de couleur des visages par la version peinte.</summary>
    public static List<OutfitPart> Apply(IEnumerable<OutfitPart> parts, IReadOnlyList<HeadLayer> layers)
    {
        if (layers.Count == 0) return parts.ToList();
        return parts.Select(p =>
        {
            if (!IsFace(p)) return p;
            var tex = p.Textures;
            if (tex.BaseColor == null) return p;
            var painted = tex with { BaseColor = Composite(tex.BaseColor, layers) };
            return p with { TextureSource = new Lazy<TextureSet>(painted) };
        }).ToList();
    }
}
