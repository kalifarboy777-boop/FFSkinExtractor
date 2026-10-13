# FFSkinExtractor

Extrait les tenues 3D de Free Fire à partir des fichiers du jeu et les exporte en `.glb` (ouvrable dans Blender).

Les tenues de Free Fire sont des personnages UMA (Unity Multipurpose Avatar). Leurs maillages sont rangés dans des
`UMAMeshAsset` au format compressé propre au jeu, qu'AssetStudio ne sait pas lire. Ce projet décode ce format
(voir [docs/FORMAT.md](docs/FORMAT.md)).

## Ce que ça fait aujourd'hui

- Ouvre les bundles du jeu (UnityFS, et les bundles gzip de l'APK).
- Regroupe les slots UMA en tenues (ex. `Male_*_SC_Hitman` → tenue `SC_Hitman`).
- Décode les maillages (positions, UV, sous-maillages) et les textures (ASTC, ETC, RGBA).
- Exporte chaque tenue en `.glb`, en pose de référence, sans squelette.

Pas encore fait : le lien ID d'item → bundle, le squelette et les poids, les animations, les variantes `_PBR`,
les textures rangées dans un autre bundle, et l'interface graphique avec aperçu.

## Utilisation

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download).

Copier depuis le téléphone le dossier `Android/data/com.dts.freefireth/files`, puis :

```
dotnet run --project src/FFSkinExtractor.Cli -- list   "C:\...\com.dts.freefireth\files"
dotnet run --project src/FFSkinExtractor.Cli -- export "C:\...\com.dts.freefireth\files" --out exports
dotnet run --project src/FFSkinExtractor.Cli -- export <bundle> --outfit Hitman --out exports
```

`--hd` exporte les variantes `_N`, plus détaillées. Un bundle de 0 octet n'a pas encore été téléchargé par le jeu.

## Structure

- `src/FFSkinExtractor.Core` : lecture des bundles (AssetsTools.NET), décodeur UMA, textures, export GLB (SharpGLTF).
- `src/FFSkinExtractor.Cli` : outil en ligne de commande `ffextract`.

Les modèles appartiennent à Garena : usage personnel uniquement.
