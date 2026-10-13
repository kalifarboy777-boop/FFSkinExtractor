# Format des maillages UMA de Free Fire (v1.132, Unity 2018.4.11f1)

Notes de rétro-ingénierie (13/10/2026), vérifiées sur la tenue « Hitman » (`optionalab_avatar_23`).

- Les bundles de `contentcache/Optional/android/**/gameassetbundles/` sont des UnityFS standard, sans chiffrement. Ceux de l'APK sont en plus compressés en gzip.
- Les vêtements sont des MonoBehaviour UMA : `UMAMeshAsset` (maillage), `SlotDataAsset`, `OverlayDataAsset` (texture), `UMAMaterial`. Le typetree est retiré, donc il faut décoder les octets bruts.
- `UMAMeshAsset`, après `m_Name` et le nom d'asset : `u32 hash` (bone_Root), `u32 0`, `u32 k`, `u32 n` (nombre de sommets), `string rootBoneName`, `u32 1`, puis des tableaux préfixés par leur longueur `u32`, alignés sur 4 :
  1. UV : n × (u16 LE, u16 LE) / 65535
  2. poids d'os : n × 12 octets (4 × u8 index d'os, 4 × u16 poids, total 10000), ou longueur 0 pour un maillage à un seul os
  3. vide
  4. n × 4 octets (normales ou couleurs, à confirmer)
  5. vide, 6. vide
  7. positions : n × 3 × **u16 big-endian**, pos = (q − 32768) / 32768 × 4 (en mètres, Z vers le haut), puis **X × 0,5** (l’axe X a une plage deux fois plus petite ; sans ça le modèle est trop large)
  8. 3n octets (normales ou tangentes compressées, à confirmer)
  9. 6n octets ou vide (à confirmer)
  10. `u32` nombre de sous-maillages, puis pour chacun `u32` taille en octets et des indices de triangles **u16 big-endian**
  11. fin : matrice de bind pose de bone_Root (16 float)
- Les index d'os renvoient au squelette de base `BaseBoneMale`/`BaseBoneFemale` (dans `avatar/assetindexer` de l'APK).
