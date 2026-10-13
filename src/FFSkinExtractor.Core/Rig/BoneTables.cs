namespace FFSkinExtractor.Core.Rig;

/// <summary>
/// Liste d'os standard de Free Fire. La plupart des vêtements n'ont pas de liste d'os propre :
/// leurs index de poids 0 à 45 renvoient à cette liste (différente chez l'homme et la femme),
/// puis 46, 47… aux os supplémentaires déclarés dans le maillage.
/// </summary>
/// <remarks>
/// Ces listes ne figurent pas dans les données du jeu. Elles ont été reconstituées en analysant
/// les 944 maillages de la version 1.132 : position des sommets par rapport aux articulations,
/// et os qui partagent les mêmes sommets (une phalange touche sa main, un tibia sa cuisse…).
/// Voir docs/FORMAT.md. Les entrées vides ne sont utilisées par aucun vêtement connu.
/// </remarks>
public static class BoneTables
{
    public const int StandardCount = 46;

    static readonly string[] Female =
    [
        /* 0 */ "bone_Spine1", "bone_LeftLegUpper", "bone_RightArm",
        /* 3 */ "bone_Left_Finger11", "bone_LeftHand", "bone_Left_Finger21", "bone_Left_Finger12", "bone_Left_Finger01",
        /* 8 */ "bone_Left_Finger22", "bone_Left_Weapon", "bone_Left_Finger02", "bone_LeftForeArm",
        /* 12 */ "bone_Right_Finger11", "bone_RightHand", "bone_Right_Finger21", "bone_Right_Finger12", "bone_Right_Finger01",
        /* 17 */ "bone_Right_Finger22", "bone_Right_Weapon", "bone_Right_Finger02", "bone_RightForeArm",
        /* 21 */ "bone_LeftClav", "bone_Spine", "bone_RightClav", "bone_Neck", "bone_LeftArm",
        /* 26 */ "Bip01 R ForeTwist", "Bip01 L ForeTwist",
        /* 28 */ "bone_Left_Spine_Backpack", "bone_Right_Spine_Weapon", "bone_Left_Spine_Weapon",
        /* 31 */ "bone_Hips", "bone_RightLegUpper", "bone_LeftLeg", "bone_RightLeg", "Female_Dummy001",
        /* 36 */ "bone_LeftAnkle", "bone_LeftToe", "bone_RightAnkle", "bone_RightToe",
        /* 40 */ "", "", "", "", "", "",
    ];

    static readonly string[] Male =
    [
        /* 0 */ "bone_Spine1", "bone_LeftClav", "bone_Spine", "bone_RightClav",
        /* 4 */ "bone_RightHand", "bone_Right_Finger21", "bone_Right_Finger22", "bone_Right_Finger31", "bone_Right_Finger32",
        /* 9 */ "bone_Right_Finger01", "bone_RightForeArm", "bone_Right_Finger41", "bone_Right_Finger11", "bone_Right_Finger42",
        /* 14 */ "bone_Right_Finger02", "bone_Right_Finger12",
        /* 16 */ "bone_LeftHand", "bone_Left_Finger11", "bone_Left_Finger21", "bone_Left_Finger22", "bone_Left_Finger01",
        /* 21 */ "bone_Left_Finger31", "bone_Left_Finger32", "bone_Left_Finger41", "bone_Left_Finger12", "bone_Left_Finger42",
        /* 26 */ "bone_Left_Weapon", "bone_LeftForeArm", "bone_Left_Finger02", "bone_Head", "bone_RightArm", "bone_LeftArm",
        /* 32 */ "Male_Dummy001", "bone_Left_Spine_Weapon", "bone_Right_Spine_Weapon", "bone_Hips",
        /* 36 */ "bone_LeftLegUpper", "bone_RightLegUpper", "bone_LeftLeg", "bone_RightLeg",
        /* 40 */ "bone_Left_Spine_Backpack", "bone_Right_Weapon",
        /* 42 */ "bone_LeftAnkle", "bone_LeftToe", "bone_RightAnkle", "bone_RightToe",
    ];

    static readonly Dictionary<string, uint[]> Hashes = new()
    {
        ["Female"] = Female.Select(n => n.Length == 0 ? 0u : BoneHash.Of(n)).ToArray(),
        ["Male"] = Male.Select(n => n.Length == 0 ? 0u : BoneHash.Of(n)).ToArray(),
    };

    /// <summary>Hashes des 46 os standard (0 pour une entrée inconnue).</summary>
    public static IReadOnlyList<uint> Standard(string gender) =>
        Hashes[gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "Female" : "Male"];

    /// <summary>Noms d'os de Free Fire, pour retrouver les noms absents de la table de chaînes d'un Avatar.</summary>
    public static IEnumerable<string> KnownBoneNames => Female.Concat(Male).Where(n => n.Length > 0)
        .Concat(["bone_Root", "Bip01", "Bip01 Footsteps", "bone_Head", "bone_Hips_Dummy", "Cheat_L", "Cheat_R",
                 "Bip01 L ForeTwist1", "Bip01 R ForeTwist1"])
        .Concat(Enumerable.Range(1, 4).SelectMany(i => new[] { $"Female_Bone00{i}", $"Male_Bone00{i}" }))
        .Distinct();
}
