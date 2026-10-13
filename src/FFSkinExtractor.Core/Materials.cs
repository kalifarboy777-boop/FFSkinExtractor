namespace FFSkinExtractor.Core;

/// <summary>Rendu du matériau d'une pièce, d'après le shader UMA du jeu.</summary>
public enum MaterialKind { Opaque, Cutout, Transparent }

/// <summary>Contenu de la troisième texture d'un overlay.</summary>
public enum MaskKind
{
    None,
    /// <summary>« _S » des overlays « _N » : intensité spéculaire en niveaux de gris.</summary>
    Specular,
    /// <summary>« _M » des overlays « _PBR » : R = métal, G = lissé, B = occlusion.</summary>
    Metallic,
}

/// <summary>
/// Textures d'un overlay UMA. Les overlays « _N » listent couleur (_D), normales (_N) et spéculaire (_S) ;
/// les overlays « _PBR » listent couleur (_C), normales (_N) et masque métal/lissé/occlusion (_M).
/// </summary>
public sealed record TextureSet(DecodedTexture? BaseColor, DecodedTexture? Normal = null, DecodedTexture? Mask = null,
    MaskKind MaskKind = MaskKind.None)
{
    public static readonly TextureSet Empty = new((DecodedTexture?)null);

    public bool IsPbr => MaskKind == MaskKind.Metallic;

    /// <summary>Range les textures d'un overlay d'après leur suffixe (_D/_C, _N, _S, _M).</summary>
    public static TextureSet FromOverlay(IReadOnlyList<DecodedTexture?> textures)
    {
        if (textures.Count == 0) return Empty;
        DecodedTexture? normal = null, mask = null;
        var kind = MaskKind.None;
        foreach (var t in textures.Skip(1))
        {
            if (t == null) continue;
            switch (Suffix(t.Name))
            {
                case "n": normal ??= t; break;
                case "s" when mask == null: mask = t; kind = MaskKind.Specular; break;
                case "m": mask = t; kind = MaskKind.Metallic; break;
            }
        }
        return new TextureSet(textures[0], normal, mask, kind);
    }

    static string Suffix(string name)
    {
        int i = name.LastIndexOf('_');
        return i < 0 ? "" : name[(i + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// Texture glTF « metallicRoughness » (G = rugosité, B = métal) et texture d'occlusion (R),
    /// calculées depuis le masque du jeu. Null si l'overlay n'a pas de masque exploitable.
    /// </summary>
    public (DecodedTexture MetallicRoughness, DecodedTexture? Occlusion)? GltfMaps()
    {
        if (Mask?.Pixels is not { } px) return null;
        int n = Mask.Width * Mask.Height;
        var mr = new byte[n * 4];
        byte[]? ao = MaskKind == MaskKind.Metallic ? new byte[n * 4] : null;
        for (int i = 0; i < n; i++)
        {
            byte b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
            byte metal, rough;
            if (MaskKind == MaskKind.Metallic)
            {
                metal = r;
                rough = (byte)(255 - g);
                ao![i * 4] = ao[i * 4 + 1] = ao[i * 4 + 2] = b;
                ao[i * 4 + 3] = 255;
            }
            else
            {
                // Spéculaire faible (peau, tissu) = mat ; spéculaire fort (cuir, plastique) = plus lisse
                metal = 0;
                rough = (byte)Math.Clamp(255 - g * 3 / 2, 64, 242);
            }
            // BGRA : B = métal, G = rugosité
            mr[i * 4] = metal; mr[i * 4 + 1] = rough; mr[i * 4 + 2] = 0; mr[i * 4 + 3] = 255;
        }
        var mrTex = DecodedTexture.FromBgra(Mask.Name + "_metalRough", Mask.Width, Mask.Height, mr);
        var aoTex = ao == null ? null : DecodedTexture.FromBgra(Mask.Name + "_occlusion", Mask.Width, Mask.Height, ao);
        return (mrTex, aoTex);
    }
}
