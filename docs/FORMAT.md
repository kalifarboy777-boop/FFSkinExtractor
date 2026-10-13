# Formats des tenues de Free Fire (v1.132, Unity 2018.4)

Notes de rétro-ingénierie des données d'avatar, vérifiées sur une centaine de tenues homme et femme.
Toutes les valeurs sont little-endian sauf mention contraire. Les chaînes sont `i32 longueur` + octets UTF-8,
alignées sur 4 octets.

## Où sont les données

| Emplacement | Contenu |
|---|---|
| `Android/data/com.dts.freefireth/files/contentcache/Optional/android/optionalavatarres/gameassetbundles/optionalab_avatar_*`, `optionalab_legendcloth_*`, `optionalab_tailor_*` | Bundles UnityFS des tenues (maillages, overlays, textures). Un fichier de 0 octet n'a pas encore été téléchargé. |
| `…/Optional/android/gameassetbundles/optionalab_<n>` et `optionalab_<n>_<m>` (bundles numérotés) | La plupart des tenues de la boutique, une fois que le jeu a téléchargé toutes ses ressources : environ 530 bundles et 2 000 tenues en v1.132, dont quelques-uns en Unity 2022.3. Une même tenue y est souvent répartie sur plusieurs bundles (le haut dans l'un, le bas dans un autre) : l'appli réunit les pièces par nom de tenue. Les sacs à dos et parachutes y sont rangés aussi (ignorés). |
| `asset_pack_install_time.apk` (dans le `.xapk`) → `assets/bin/Data/` | Données de base : environ 44 000 fichiers sérialisés Unity, presque tous à un seul objet. Corps, visage et cheveux par défaut, recettes UMA, avatars (squelettes), textures de peau. |

Les fichiers de `bin/Data` sont au format sérialisé v22 : la taille du fichier est un `i64` big-endian à l'offset
`0x18` de l'en-tête. Les références entre fichiers (PPtr `fileId ≠ 0`) passent par la liste des dépendances
(`externals`) du fichier qui contient la référence.

Les tables de configuration du jeu (liste des objets, prix…) sont chiffrées : l'outil ne les lit pas.

## Objets UMA

Les MonoBehaviour UMA ont perdu leur typetree : on lit les octets bruts. Après l'en-tête MonoBehaviour
(`m_GameObject`, `m_Enabled`, `m_Script`, 28 octets), `m_Name` commence à l'offset 28.

### SlotDataAsset

`m_Name`, `slotName`, PPtr `material` (UMAMaterial), `f32`, 8 octets, PPtr `meshData` (UMAMeshAsset), `i32 subMeshIndex`.

Le nom du matériau donne le rendu :

| Matériau | Rendu glTF |
|---|---|
| `DTSAvatarSkin…` sur un maillage à plusieurs sous-maillages (slots `_N`) | morceau de peau du corps |
| `…CutOff…`, `…AvatarHair…` | alpha `MASK` 0,5 |
| `…Glass…` | alpha `BLEND`, opacité 0,4 |
| autres | opaque |

Les slots en jeu (sans `_N`) utilisent tous `DTSAvatarCommon` : la peau y est reconnue par la place du
sous-maillage dans la texture (zone de peau du corps de base).

### OverlayDataAsset

Se termine par `textureList` (`i32 count` + `count` × PPtr) puis le PPtr du matériau.

| Overlay | Textures |
|---|---|
| `…_Overlay` | couleur `_D` |
| `…_Overlay_N` | couleur `_D`, normales `_N`, spéculaire `_S` (niveaux de gris) |
| `…_Overlay_PBR` | couleur `_C`, normales `_N`, masque `_M` (R = métal, G = lissé, B = occlusion) |

Les normales sont des cartes tangentes classiques (RGB, Y vers le haut). À l'export : `_S` donne la rugosité
(`1 − 1,5 × S`, bornée), `_M` donne métal, rugosité (`1 − G`) et occlusion.

Les coloris d'une même tenue (`Cos_Cobra_Red`, `_Blue`, `_Purple`, `_Yellow`) partagent les slots et les
maillages du premier : seuls les overlays existent pour les autres. Les slots `_PBR` reprennent le maillage `_N`.

### Recette du personnage de base

`FemaleBaseRecipe_max_N` / `MaleBaseRecipe_max_N` (sans `_N` pour la version en jeu) listent des couples
(slot, overlay) : `slotName`, `i32 100`, un index d'overlay partagé, quelques entiers, puis le nom de l'overlay
(il manque pour les morceaux de peau et le visage, qui prennent la peau choisie par le joueur).
Slots de base : `Chest` (haut), `Leg` (bas), `Feet` (chaussures), `Head`, `Hair` ; le slot `Additive` est ignoré.

Peaux par défaut : `female_body_Overlay_N` (`female_Newbody_d/_n/_s`), `female_head001_Overlay_N`,
`male_body_Overlay_N` (`male_body_D/_N/_S`, qui porte aussi le visage homme).

## UMAMeshAsset

### En-tête

1. `m_Name`, puis le nom du slot, puis 2 `i32`.
2. `bindPoses` : `i32 count` + count × 64 octets (matrice 4×4, rangée par lignes).
3. 12 tableaux Unity standards, en général vides, de taille d'élément `32, 12, 12, 16, 4, 8, 8, 8, 8, 8, 8` puis
   les sous-maillages (`int[]` par élément). Les cheveux de base ont des tangentes. Quelques maillages récents
   (« Cos_Fauxfurhat », « Cos_Colorfest ») remplissent ces tableaux, avec parfois une autre taille d'élément
   (12 au lieu de 4) : la taille retenue est celle après laquelle on retombe sur un tableau vide ou de même longueur.
   Leurs UV sont alors dans le premier tableau de n × 8 octets (2 × `f32`) et le tableau compressé d'UV est vide.
4. `umaBones` : `i32 count`, puis par os position (3 f32), rotation (4 f32), échelle (3 f32), nom, `u32 hash`,
   `u32 hash du parent`.
5. `umaBoneCount`, hash de la racine (`0x332677a9` = CRC32 de `bone_Root`), `boneNameHashes` (`i32 count` + `u32`).
6. `subMeshCount`, `n` (nombre de sommets), la chaîne `bone_Root`, un `i32`.

### Données compressées

Tableaux préfixés par leur longueur, alignés sur 4 :

1. UV : n × (`u16`, `u16`) / 65535.
2. Poids 12 octets : 4 × `u8` index d'os, 4 × `u16` poids (somme 10 000), ou vide.
3. Jusqu'à quatre tableaux optionnels de n éléments, en général de 4 octets (parfois 8). Certains maillages
   en ont moins : la liste s'arrête dès que la longueur des positions (6n) apparaît.
4. Positions : n × 3 × **`u16` big-endian**. Repère Unity = (x, z, −y) des données ; plages ±1 en X et ±2 en Y/Z
   (unités du jeu, ×2 à l'export pour retrouver la taille du squelette).
5. Normales : n × 3 octets (recalculées à l'export, plus fiables).
6. Tableau inutilisé.
7. Poids compacts : n × 6 octets, 24 bits **big-endian** = 4 index d'os sur 6 bits, puis 3 poids sur 255,
   le 4ᵉ valant `255 − somme`. Ou vide.
8. Sous-maillages : `i32 count`, puis pour chacun `i32` taille en octets et des indices **`u16` big-endian**.

### Os supplémentaires

En fin d'objet : des `i32` nuls, `k` + k matrices, `k` + k hash, puis les `umaBones`. Ce sont les os de tissu, de
cheveux ou d'accessoire propres au vêtement. Les index de poids `46` et plus les désignent, après les 46 os de la
table standard. Si le maillage a sa propre liste `boneNameHashes`, c'est elle qui fait foi.

Certains maillages n'ont aucun poids : ils sont attachés en rigide à l'os le plus proche.

## Squelette

Le hash d'un os est `Animator.StringToHash(nom)`, c'est-à-dire le CRC32 de son nom.

Les squelettes viennent des assets `Avatar` de l'APK : nom, `avatarSize`, nœuds (parent, axes), identifiants,
pose du squelette (N × 40 octets), **pose par défaut** N × (translation 3 f32, rotation 4 f32, échelle 3 f32),
puis les hash des noms. La pose de liaison des vêtements est cette pose par défaut. On garde le sous-arbre de
`bone_Root` dans l'ordre de parcours en profondeur (52 os pour la femme, 55 pour l'homme), translations × 2.
Ils sont fournis dans `src/FFSkinExtractor.Core/Rig/Female.json` et `Male.json`.

Table standard des 46 index de poids (femme) : 0 Spine1, 1 LeftLegUpper, 2 RightArm, 3 L_F11, 4 LeftHand,
5 L_F21, 6 L_F12, 7 L_F01, 8 L_F22, 9 Left_Weapon, 10 L_F02, 11 LeftForeArm, 12–20 côté droit,
21 LeftClav, 22 Spine, 23 RightClav, 24 Neck, 25 LeftArm, 26–27 ForeTwist, 28–30 attaches dos, 31 Hips,
32 RightLegUpper, 33 LeftLeg, 34 RightLeg, 35 Female_Dummy001, 36 LeftAnkle, 37 LeftToe, 38 RightAnkle,
39 RightToe. La table homme est dans `Rig/BoneTables.cs`.

## Passage en glTF

Unity est main gauche, glTF main droite : sommet `(−x, y, z)`, triangles `(i, i+2, i+1)`, translation d'os
`(−tx, ty, tz)`, rotation `(qx, −qy, −qz, qw)`.

## Liaison aux ID d'objets

Sans les tables chiffrées, les tenues sont reliées aux objets de la base [ItemID2](https://github.com/0xme/ItemID2)
par leurs noms : l'icône d'un objet reprend le nom interne (`Icon_avatar_female_sc_top_nanoscientist` pour
`Sc_Nanoscientist`) et le début de l'ID donne la pièce : 101/102 personnage, 203 haut, 204 bas, 205 chaussures,
211 tête (cheveux, masque), 214 maquillage. Environ 85 % des tenues trouvent ainsi leurs ID.


## Maquillage et racines de cheveux (overlays sans maillage)

Les overlays `{Genre}_Headadditive_{Tenue}[_k]_Overlay` (maquillage, objets 214…) et `{Genre}_Hairroot_{Tenue}_Overlay`
(racines et dégradés de cheveux) n'ont pas de slot à eux : le jeu les fusionne dans la texture du visage.
Après `m_Name` et `overlayName` viennent un entier puis le **rectangle de destination** (x, y, largeur, hauteur,
de 0 à 1, origine en bas à gauche) ; un rectangle nul couvre toute la texture. Les overlays `OverlayDataAssetWithAtlas`
ajoutent à la fin 16 octets : le **rectangle source** dans leur texture (plusieurs morceaux, comme les deux yeux et une
joue, partagent une texture). Les textures `_rgb` + `_sa` séparent la couleur et l'opacité (niveaux de gris).

## Repère des maillages

La plupart des maillages sont rangés Z vers le haut. Certains (Racergirl, Scorpio, cheveux de Seaman…) sont rangés
Y vers le haut ou autour de la tête. Les `bindPoses` du maillage (os propres en début d'objet, os supplémentaires
en fin d'objet ; matrices Unity rangées e00, e01, e02, e03, e10…) combinées au squelette de base donnent le passage
du repère du maillage à celui du personnage (`bindPose × monde de l'os`, translations à l'échelle d'export).
