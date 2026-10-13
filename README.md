# FF Skin Studio (FFSkinExtractor)

Parcourt les tenues 3D de Free Fire, les essaie sur le personnage en 3D et les exporte en `.glb`
(Blender, Unity, Unreal, three.js…), avec squelette, poids et matériaux PBR.

Les tenues de Free Fire sont des personnages UMA (Unity Multipurpose Avatar) dont les maillages sont rangés dans
un format compressé propre au jeu, qu'AssetStudio ne sait pas lire. Ce projet décode ce format
(voir [docs/FORMAT.md](docs/FORMAT.md)).

## Fonctionnalités

- **Appli avec aperçu 3D**, comme REGO Skin Studio : on choisit un personnage (Rafael, Kelly, Alok éveillé…),
  puis un bundle de la boutique ou des ID d'objets par emplacement (cheveux, masque, maquillage, haut, bas, chaussures).
  Export d'un look ou de tous les bundles d'un coup.
- **Catalogue complet** : tous les objets d'ItemID2 (bundles, personnages, vêtements, maquillages), page par page
  comme sur le site, avec ceux dont le modèle 3D est dans tes fichiers marqués « 3D ».
- **Maquillage et racines de cheveux** : peints sur la texture du visage, comme dans le jeu (ID 214…).
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
   voulues dans le jeu pour qu'il les télécharge : un bundle de 0 octet n'est pas encore téléchargé. Après le
   téléchargement complet des ressources du jeu, les bundles numérotés (`optionalab_123`) contiennent la plupart
   des tenues de la boutique : la première analyse lit plusieurs centaines de bundles (en parallèle), les suivantes
   reprennent l'inventaire gardé dans `%APPDATA%\FFSkinStudio\scan-cache.json`.
2. Le `.xapk` du jeu (même version que les données). Il fournit le corps, le visage et la peau ; sans lui les tenues
   s'exportent seules, avec une couleur de peau unie.

## FF Skin Studio (interface)

Lance `FFSkinStudio.exe`. Une page s'ouvre dans le navigateur (`http://127.0.0.1:5178`). Au premier lancement,
indique le dossier des données dans **Réglages** : l'analyse prend quelques secondes.

- À gauche, **Personnage** : le personnage de base ou un personnage du jeu, choisi tel quel (visage, coiffure,
  tenue d'origine). **Bundle** : un bundle de la boutique (liste ItemID2, type BUNDLE) remplit tous ses emplacements ;
  les bundles absents de tes fichiers sont listés à part. **Skin builder** : un ID d'objet par emplacement
  (ex. `203000528`, ou `214047002` pour un maquillage), ou une recherche par nom ; chaque ID remplace la pièce du personnage.
- En haut, **Catalogue complet** : les ~12 700 objets d'ItemID2, filtrables par type (bundles, personnages, têtes,
  maquillages, hauts, bas, chaussures) et par nom, ID ou rareté. Un clic sur un objet « 3D » l'essaie ;
  un objet « Absent » n'a pas encore été téléchargé par le jeu.
- Au centre : fais tourner avec la souris, molette pour zoomer ; boutons pour recadrer, rotation automatique,
  fil de fer, squelette, fond clair et capture PNG.
- À droite : pièces portées, options (corps de base, squelette, PBR, textures PNG), téléchargement du `.glb`,
  export dans le dossier `Documents\FFSkinStudio`, et export de tous les bundles portés par le personnage choisi.

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

- Seuls les objets téléchargés par le jeu ont un modèle 3D. Pour en avoir plus, ouvre-les dans la boutique ou le
  coffre-fort, ou télécharge toutes les ressources dans les paramètres du jeu, puis recopie le dossier et relance l'analyse.
- Les ID sont trouvés par le nom : une quinzaine de tenues n'en ont pas, quelques correspondances faibles sont écartées.
- Les animations du jeu et les effets animés (VFX) ne sont pas exportés.
- La correspondance rugosité/métal des shaders du jeu est approchée.

## Crédits

- ID, noms et icônes d'objets : [ItemID2](https://github.com/0xme/ItemID2) et ff-resources, par Crystal Person.
- [three.js](https://threejs.org) (MIT), [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET),
  [AssetRipper.TextureDecoder](https://github.com/AssetRipper/TextureDecoder), [SharpGLTF](https://github.com/vpenades/SharpGLTF).

Outil non officiel, sans lien avec Garena. Les modèles et textures appartiennent à Garena : usage personnel uniquement.
