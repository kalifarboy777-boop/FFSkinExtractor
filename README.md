# FF Skin Studio (FFSkinExtractor)

Parcourt les tenues 3D de Free Fire, les essaie sur le personnage en 3D et les exporte en `.glb`
(Blender, Unity, Unreal, three.js…), avec squelette, poids et matériaux PBR.

Les tenues de Free Fire sont des personnages UMA (Unity Multipurpose Avatar) dont les maillages sont rangés dans
un format compressé propre au jeu, qu'AssetStudio ne sait pas lire. Ce projet décode ce format
(voir [docs/FORMAT.md](docs/FORMAT.md)).

## Fonctionnalités

- **Appli avec aperçu 3D** : catalogue des tenues avec icônes, noms officiels et ID d'objets, choix pièce par pièce
  (haut, bas, chaussures, tête, cheveux, accessoire), recherche par nom ou par ID, export d'une tenue ou de toutes.
- **Squelette et poids** : armature complète (52 os femme, 55 homme) et os de tissu ou de cheveux propres à
  chaque vêtement. Le modèle se pose et s'anime directement dans Blender.
- **Corps de base** : peau, visage, cheveux et tenue de départ du jeu, remplacés par les pièces choisies.
- **Matériaux PBR** : couleur, normales, rugosité, métal et occlusion depuis les textures `_N`, `_S` et `_PBR`,
  transparence (lunettes, voiles) et découpe (cheveux).
- **Coloris** : les variantes d'une même tenue (ex. Cobra rouge, bleu, violet, jaune) apparaissent séparément.
- **Deux qualités** : modèles du lobby (HD) ou modèles utilisés en partie.
- **Liaison aux ID** d'après la base [ItemID2](https://github.com/0xme/ItemID2).

## Récupérer les données du jeu

Mets dans un même dossier (par exemple `Documents\FFdata`) :

1. Le dossier `Android/data/com.dts.freefireth/files` copié depuis le téléphone. Ouvre d'abord les tenues
   voulues dans le jeu pour qu'il les télécharge : un bundle de 0 octet n'est pas encore téléchargé.
2. Le `.xapk` du jeu (même version que les données). Il fournit le corps, le visage et la peau ; sans lui les tenues
   s'exportent seules, avec une couleur de peau unie.

## FF Skin Studio (interface)

Lance `FFSkinStudio.exe`. Une page s'ouvre dans le navigateur (`http://127.0.0.1:5178`). Au premier lancement,
indique le dossier des données dans **Réglages** : l'analyse prend quelques secondes.

- À gauche : clique sur une tenue pour l'essayer, ou utilise les onglets Haut, Bas, Chaussures… pour composer.
- Au centre : fais tourner avec la souris, molette pour zoomer ; boutons pour recadrer, rotation automatique,
  fil de fer, squelette, fond clair et capture PNG.
- À droite : pièces portées, options (corps de base, squelette, PBR, textures PNG), téléchargement du `.glb`,
  export dans le dossier `Documents\FFSkinStudio`, et export de toutes les tenues en une fois.

Options : `FFSkinStudio.exe [dossier des données] [--port 5178] [--no-browser]`. Les réglages sont gardés dans
`%APPDATA%\FFSkinStudio`, avec une copie locale de la base ItemID2 (rafraîchie chaque semaine).

## Ligne de commande (`ffextract`)

```
ffextract list   <dossier>                         liste les tenues trouvées
ffextract export <dossier|bundle> [--outfit NOM] [--hd] [--out DOSSIER] [--apk JEU.xapk] [--no-base]
ffextract base   <JEU.xapk> [--out DOSSIER] [--lowpoly]   personnage de base seul
ffextract dump   <bundle> <dossier>                objets UMA bruts (analyse)
ffextract skeleton <fichier avatar> <Female|Male> <sortie.json>
```

`--hd` exporte les modèles du lobby, `--apk` ajoute le corps de base et la peau.

## Compiler

Prérequis : [.NET 8 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/FFSkinExtractor.App -- "C:\Users\moi\Documents\FFdata"
dotnet publish src/FFSkinExtractor.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
dotnet publish src/FFSkinExtractor.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
```

## Structure

| Dossier | Rôle |
|---|---|
| `src/FFSkinExtractor.Core` | Bundles (AssetsTools.NET), données de l'APK, décodeur UMA, squelette, textures, export GLB (SharpGLTF), catalogue d'objets |
| `src/FFSkinExtractor.App` | FF Skin Studio : serveur local ASP.NET Core et interface three.js intégrée à l'exécutable |
| `src/FFSkinExtractor.Cli` | Outil en ligne de commande `ffextract` |
| `docs/FORMAT.md` | Formats du jeu décodés |

## Limites connues

- Les ID sont trouvés par le nom : une quinzaine de tenues n'en ont pas, quelques correspondances faibles sont écartées.
- Le visage homme par défaut est peint dans la texture du corps : il est moins détaillé que le visage femme.
- Les animations du jeu et les effets animés (VFX) ne sont pas exportés.
- La correspondance rugosité/métal des shaders du jeu est approchée.

## Crédits

- ID, noms et icônes d'objets : [ItemID2](https://github.com/0xme/ItemID2) et ff-resources, par Crystal Person.
- [three.js](https://threejs.org) (MIT), [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET),
  [AssetRipper.TextureDecoder](https://github.com/AssetRipper/TextureDecoder), [SharpGLTF](https://github.com/vpenades/SharpGLTF).

Outil non officiel, sans lien avec Garena. Les modèles et textures appartiennent à Garena : usage personnel uniquement.
