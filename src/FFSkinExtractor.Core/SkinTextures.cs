namespace FFSkinExtractor.Core;

/// <summary>
/// Textures de peau du corps de base (homme et femme). Les tenues ne contiennent que les vêtements :
/// les morceaux de peau visibles (cou, mains, taille…) utilisent ces textures, rangées dans l'APK du jeu.
/// </summary>
public sealed class SkinTextures
{
    public TextureSet? Male { get; private init; }
    public TextureSet? Female { get; private init; }
    public TextureSet? MaleHead { get; private init; }
    public TextureSet? FemaleHead { get; private init; }

    public TextureSet? For(string gender) => IsFemale(gender) ? Female : Male;

    /// <summary>Visage par défaut du personnage de base.</summary>
    public TextureSet? HeadFor(string gender) => (IsFemale(gender) ? FemaleHead : MaleHead) ?? For(gender);

    static bool IsFemale(string gender) => gender.Equals("Female", StringComparison.OrdinalIgnoreCase);

    public static SkinTextures From(GameData data, bool highDetail = true)
    {
        string n = highDetail ? "_N" : "";
        return new()
        {
            Female = data.Overlay("female_body_Overlay" + n) ?? data.Overlay("female_body_Overlay"),
            Male = data.Overlay("male_body_Overlay" + n) ?? data.Overlay("male_body_Overlay"),
            FemaleHead = data.Overlay("female_head001_Overlay" + n) ?? data.Overlay("female_head001_Overlay"),
            MaleHead = data.Overlay("male_head001_Overlay" + n),
        };
    }

    /// <summary>Lit les textures de peau depuis un .xapk, un .apk, ou un dossier d'APK décompressé.</summary>
    public static SkinTextures Load(string path)
    {
        using var data = GameData.Open(path);
        return From(data);
    }
}
