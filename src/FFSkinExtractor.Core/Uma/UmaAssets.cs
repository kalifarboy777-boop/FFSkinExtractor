using System.Buffers.Binary;
using System.Text;

namespace FFSkinExtractor.Core.Uma;

/// <summary>Contenu utile d'un SlotDataAsset (lu sans typetree).</summary>
public sealed record SlotInfo(string ObjectName, string SlotName, PPtr Material, PPtr Mesh, int SubmeshIndex);

/// <summary>Lecture des MonoBehaviour UMA dont le typetree a été retiré du jeu.</summary>
public static class UmaAssets
{
    // SlotDataAsset : m_Name, slotName, PPtr material, float, 8 octets, PPtr meshData, int submeshIndex
    public static SlotInfo ParseSlot(byte[] raw)
    {
        var s = raw.AsSpan();
        int p = 28;
        string objectName = ReadString(s, ref p);
        string slotName = ReadString(s, ref p);
        var material = new PPtr(I32(s, p), I64(s, p + 4));
        p += 12 + 4 + 8;
        var mesh = new PPtr(I32(s, p), I64(s, p + 4));
        int sub = I32(s, p + 12);
        return new SlotInfo(objectName, slotName, material, mesh, sub);
    }

    /// <summary>
    /// Textures d'un OverlayDataAsset. Il se termine par textureList (int count + count × PPtr)
    /// puis le PPtr du matériau ; la première texture est la couleur de base.
    /// <paramref name="isTexture"/> confirme qu'une référence pointe bien vers une Texture2D.
    /// </summary>
    public static List<PPtr> OverlayTextures(byte[] raw, Func<PPtr, bool> isTexture)
    {
        var s = raw.AsSpan();
        int end = raw.Length - 12;
        for (int count = 1; count <= 8; count++)
        {
            int at = end - 12 * count - 4;
            if (at < 32) break;
            if (I32(s, at) != count) continue;
            var list = new List<PPtr>(count);
            for (int i = 0; i < count; i++) list.Add(new PPtr(I32(s, at + 4 + 12 * i), I64(s, at + 8 + 12 * i)));
            if (isTexture(list[0])) return list;
        }
        return [];
    }

    /// <summary>
    /// Recette UMA du personnage de base (« FemaleBaseRecipe_max_N ») : liste (slot, overlay).
    /// Un slot sans overlay propre partage celui d'un slot précédent.
    /// </summary>
    public static List<(string Slot, string? Overlay)> ParseBaseRecipe(byte[] raw)
    {
        var s = raw.AsSpan();
        var result = new List<(string, string?)>();
        // Repère : chaque entrée commence par le nom du slot suivi de l'entier 100
        for (int p = 32; p + 8 < s.Length;)
        {
            int len = I32(s, p);
            if (len <= 0 || len > 128 || p + 4 + len + 12 > s.Length) { p += 4; continue; }
            int after = (p + 4 + len + 3) & ~3;
            string str = Encoding.UTF8.GetString(s.Slice(p + 4, len));
            if (I32(s, after) != 100 || str.Contains("Overlay")) { p += 4; continue; }

            int shared = I32(s, after + 4);
            int q = after + 8;
            string? overlay = null;
            // L'overlay suit à quelques entiers près (le nombre de champs varie selon l'entrée)
            for (int k = after + 8; k <= after + 20 && k + 4 <= s.Length; k += 4)
            {
                int olen = I32(s, k);
                if (olen <= 0 || olen >= 128 || k + 4 + olen > s.Length) continue;
                string o = Encoding.UTF8.GetString(s.Slice(k + 4, olen));
                if (o.Contains("Overlay")) { overlay = o; q = (k + 4 + olen + 3) & ~3; }
                break;
            }
            if (overlay == null && shared >= 0 && shared < result.Count) overlay = result[shared].Item2;
            result.Add((str, overlay));
            p = q;
        }
        return result;
    }

    static string ReadString(ReadOnlySpan<byte> s, ref int p)
    {
        int len = I32(s, p);
        string str = Encoding.UTF8.GetString(s.Slice(p + 4, len));
        p = (p + 4 + len + 3) & ~3;
        return str;
    }

    static int I32(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadInt32LittleEndian(s[p..]);
    static long I64(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadInt64LittleEndian(s[p..]);
}
