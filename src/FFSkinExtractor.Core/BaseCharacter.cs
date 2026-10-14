using AssetsTools.NET.Extra;
using FFSkinExtractor.Core.Uma;

namespace FFSkinExtractor.Core;

/// <summary>
/// Personnage de base d'après la recette du jeu (« FemaleBaseRecipe_max_N ») : visage, cheveux
/// par défaut, corps et tenue de départ. Les pièces choisies remplacent ensuite les pièces de base
/// de même type (un haut remplace le buste, un bas les jambes…).
/// </summary>
public static class BaseCharacter
{
    /// <summary>Pièces du personnage de base.</summary>
    public static List<OutfitPart> Build(GameData data, string gender, bool highDetail = true)
    {
        string g = gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "Female" : "Male";
        var recipeObj = data.Find($"{g}BaseRecipe_max{(highDetail ? "_N" : "")}", AssetClassID.MonoBehaviour)
            ?? throw new InvalidDataException($"recette du personnage de base {g} introuvable dans l'APK");
        var recipe = UmaAssets.ParseBaseRecipe(data.Raw(recipeObj));

        // Les slots de base, indexés par leur nom de slot (champ slotName)
        var slots = new Dictionary<string, (DataObject Obj, SlotInfo Info)>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in data.FindAll(n => n.EndsWith("_Slot", StringComparison.Ordinal) && n.StartsWith("Base_", StringComparison.OrdinalIgnoreCase)))
        {
            if (o.Type != AssetClassID.MonoBehaviour) continue;
            try { var info = UmaAssets.ParseSlot(data.Raw(o)); slots.TryAdd(info.SlotName, (o, info)); }
            catch (Exception) { }
        }

        var skin = SkinTextures.From(data, highDetail);
        var meshes = new Dictionary<DataObject, UmaMesh>();
        var parts = new List<OutfitPart>();
        foreach (var (slotName, overlayName) in recipe)
        {
            if (!slots.TryGetValue(slotName, out var slot)) continue;
            string piece = PieceOfBaseSlot(slotName);
            if (piece == "") continue;
            var meshObj = data.Resolve(slot.Obj.File, slot.Info.Mesh.FileId, slot.Info.Mesh.PathId);
            if (meshObj == null) continue;
            if (!meshes.TryGetValue(meshObj.Value, out var mesh))
                meshes[meshObj.Value] = mesh = UmaMeshDecoder.Decode(meshObj.Value.Name, data.Raw(meshObj.Value));

            // La recette ne donne pas d'overlay aux morceaux de peau ni au visage : le jeu leur applique
            // la peau choisie par le joueur (ici la peau par défaut)
            bool isSkin = overlayName == null && piece != "Head";
            var textures = overlayName != null ? data.Overlay(overlayName) ?? TextureSet.Empty
                : piece == "Head" ? skin.HeadFor(g) ?? TextureSet.Empty
                : skin.For(g) ?? TextureSet.Empty;
            var material = data.Resolve(slot.Obj.File, slot.Info.Material.FileId, slot.Info.Material.PathId)?.Name ?? "";
            var kind = material.Contains("Hair") || material.Contains("CutOff") ? MaterialKind.Cutout : MaterialKind.Opaque;
            parts.Add(new OutfitPart(slot.Info.ObjectName, mesh, slot.Info.SubmeshIndex, textures, isSkin, kind, piece));
        }
        return parts;
    }

    static string PieceOfBaseSlot(string slot)
    {
        string s = slot.ToLowerInvariant();
        if (s.Contains("additive")) return "";
        if (s.Contains("chest")) return "Top";
        if (s.Contains("_leg")) return "Bottom";
        if (s.Contains("feet")) return "Shoe";
        if (s.Contains("hair")) return "Hair";
        if (s.Contains("head")) return "Head";
        return "Other";
    }

    /// <summary>
    /// Assemble le personnage : pièces de base, remplacées par les pièces choisies du même type.
    /// Les accessoires s'ajoutent sans rien remplacer.
    /// </summary>
    public static List<OutfitPart> Compose(IReadOnlyList<OutfitPart> baseParts, IEnumerable<OutfitPart> chosen)
    {
        var chosenList = chosen.ToList();
        var replaced = chosenList.Select(p => p.Piece).ToHashSet();
        var chosenParts = chosenList.Where(p => !p.IsSkin).ToList();
        return baseParts.Where(p => !replaced.Contains(p.Piece) && !Covered(p, chosenParts)).Concat(chosenList).ToList();
    }

    const float CoverMargin = 0.03f;
    // Écart entre la semelle des chaussures de base et le bas d'une pièce qui descend jusqu'au sol (pattes, pieds)
    const float SoleGap = 0.06f;

    /// <summary>
    /// Pièce de base cachée sous une pièce choisie, comme dans le jeu : cheveux sous un masque intégral
    /// (90 % de leurs sommets dans la boîte du masque), chaussures sous une combinaison qui descend jusqu'aux
    /// pieds (« 24K Woof » et ses pattes).
    /// </summary>
    static bool Covered(OutfitPart basePart, List<OutfitPart> chosen)
    {
        if (basePart.IsSkin || basePart.Piece is not ("Hair" or "Shoe")) return false;
        var points = Vertices(basePart).ToList();
        if (points.Count == 0) return false;
        if (basePart.Piece == "Shoe")
        {
            float sole = points.Min(v => v.Y);
            return chosen.Any(c => c.Piece is "Top" or "Bottom" && Vertices(c).Select(v => v.Y).DefaultIfEmpty(float.MaxValue).Min() <= sole + SoleGap);
        }
        foreach (var c in chosen.Where(c => c.Piece is "Head" or "Accessory"))
        {
            var (min, max) = Bounds(c);
            int inside = points.Count(v => v.X >= min.X - CoverMargin && v.X <= max.X + CoverMargin
                && v.Y >= min.Y - CoverMargin && v.Y <= max.Y + CoverMargin && v.Z >= min.Z - CoverMargin && v.Z <= max.Z + CoverMargin);
            if (inside >= 0.9 * points.Count) return true;
        }
        return false;
    }

    static IEnumerable<System.Numerics.Vector3> Vertices(OutfitPart p) =>
        p.SubmeshIndex < p.Mesh.Submeshes.Count
            ? p.Mesh.Submeshes[p.SubmeshIndex].Distinct().Where(i => i < p.Mesh.Positions.Length).Select(i => p.Mesh.Positions[i])
            : [];

    static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) Bounds(OutfitPart p)
    {
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var v in Vertices(p)) { min = System.Numerics.Vector3.Min(min, v); max = System.Numerics.Vector3.Max(max, v); }
        return (min, max);
    }
}
