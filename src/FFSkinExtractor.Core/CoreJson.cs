using System.Text.Json;
using System.Text.Json.Serialization;
using FFSkinExtractor.Core.Items;
using FFSkinExtractor.Core.Rig;

namespace FFSkinExtractor.Core;

/// <summary>Sérialisation JSON générée à la compilation (compatible avec les exécutables allégés).</summary>
[JsonSerializable(typeof(List<Skeleton.JsonBone>))]
[JsonSerializable(typeof(List<ItemCatalog.RawItem>))]
internal partial class CoreJson : JsonSerializerContext
{
    public static CoreJson Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}
