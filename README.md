# Chorus Mod

Plugin BepInEx pour Clone Hero : recherche de charts sur enchor.us,
téléchargement, installation et rescan automatique de la bibliothèque —
le tout depuis une fenêtre in-game. **Une seule DLL**, pas d'application
externe, pas de dépendance à copier.

Fonctionne sous **Windows et Linux** (build natif ou Proton). Aucun chemin
n'est codé en dur : tout passe par la configuration.

> Pour une installation Windows sans ligne de commande, voir l'installeur
> tout-en-un dans [`installer/`](installer/README.md).

---

## Sommaire

- [Prérequis](#prérequis)
- [Installation depuis zéro — Linux](#installation-depuis-zéro--linux)
- [Installation depuis zéro — Windows](#installation-depuis-zéro--windows)
- [Compiler le plugin](#compiler-le-plugin)
- [Utilisation](#utilisation)
- [Configuration](#configuration)
- [Dépannage](#dépannage)
- [Notes techniques : limites IL2CPP de cette build](#notes-techniques--limites-il2cpp-de-cette-build)

---

## Prérequis

- Clone Hero **v1.1.0.6142** (IL2CPP, Unity 2022.3.62f2) — d'autres
  versions peuvent fonctionner, voir les notes techniques
- BepInEx **6.0.0-be.755**, variante *Unity.IL2CPP*
- SDK **.NET 6** (uniquement pour compiler)

> La version de BepInEx compte. Les builds « bleeding edge » cassent
> régulièrement la compatibilité entre elles : `be.785` par exemple ne
> charge pas les mêmes plugins que `be.755`. Le plugin est épinglé sur
> `be.755` dans le `.csproj`.

---

## Installation depuis zéro — Linux

### 1. Repérer le dossier du jeu

```bash
find ~ -maxdepth 8 -iname "Clone Hero" -type d 2>/dev/null
```

Emplacements courants :

| Contexte | Chemin |
| --- | --- |
| Steam natif | `~/.local/share/Steam/steamapps/common/Clone Hero` |
| Steam Flatpak | `~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Clone Hero` |
| Install manuelle | variable |

Note ce chemin, il servira partout ensuite :

```bash
export CLONEHERO_DIR="$HOME/.local/share/Steam/steamapps/common/Clone Hero"
```

### 2. Identifier la variante du jeu

```bash
ls "$CLONEHERO_DIR"
```

- Présence de `Clone Hero.x86_64` → **build Linux natif**
- Présence de `Clone Hero.exe` → **build Windows sous Proton**

Cette distinction détermine quel BepInEx installer.

### 3a. Build Linux natif → BepInEx Linux

Télécharge `BepInEx-Unity.IL2CPP-linux-x64-6.0.0-be.755+*.zip` depuis
<https://builds.bepinex.dev/projects/bepinex_be>, puis :

```bash
cd "$CLONEHERO_DIR"
unzip ~/Téléchargements/BepInEx-Unity.IL2CPP-linux-x64-6.0.0-be.755*.zip
chmod +x run_bepinex.sh
```

Lancement depuis Steam : *Propriétés* → *Options de lancement* :

```
"/chemin/complet/vers/Clone Hero/run_bepinex.sh" %command%
```

Ou hors Steam :

```bash
./run_bepinex.sh
```

> ⚠️ Le script `run_bepinex.sh` a des soucis connus avec le wrapper de
> lancement de Steam. Si BepInEx ne se charge pas (voir §5), lance le jeu
> directement en ligne de commande pour vérifier, puis rabats-toi sur la
> méthode Proton ci-dessous.

### 3b. Build Windows sous Proton → BepInEx Windows

Télécharge `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+*.zip`, puis :

```bash
cd "$CLONEHERO_DIR"
unzip ~/Téléchargements/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755*.zip
```

Options de lancement Steam :

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

C'est la méthode la plus fiable : elle reproduit exactement
l'environnement Windows, donc les mêmes méthodes IL2CPP disponibles.

### 4. Premier lancement (obligatoire)

Lance le jeu **une fois** et laisse-le arriver au menu. Ce démarrage est
plus long que d'habitude : BepInEx génère les *interop assemblies* à
partir du binaire du jeu.

### 5. Vérifier que BepInEx est bien chargé

```bash
grep -m3 "BepInEx\|Chainloader" "$CLONEHERO_DIR/BepInEx/LogOutput.log"
ls "$CLONEHERO_DIR/BepInEx/interop" | head
```

Tu dois voir la version de BepInEx dans le log, et une centaine de DLLs
dans `interop/` (dont `CloneHero.dll`). Sans ça, inutile d'aller plus
loin : le plugin ne se chargera pas et ne compilera même pas.

### 6. Installer le plugin

```bash
mkdir -p "$CLONEHERO_DIR/BepInEx/plugins/chorus"
cp ChorusMod.dll "$CLONEHERO_DIR/BepInEx/plugins/chorus/"
```

Relance le jeu, puis vérifie :

```bash
grep "Chorus Mod" "$CLONEHERO_DIR/BepInEx/LogOutput.log"
```

Attendu : `Chorus Mod chargé. Dossier Songs : '...'` puis
`Appuie sur F9 en jeu pour ouvrir Chorus Mod.`

### 7. Vérifier le dossier Songs

La détection automatique couvre les emplacements usuels (à côté du jeu,
`~/Documents/Clone Hero/Songs`, `~/.local/share/Clone Hero/Songs`, et les
préfixes Proton). Si le log affiche un chemin vide ou faux, corrige-le :

```bash
nano "$CLONEHERO_DIR/BepInEx/config/fr.lucas.chorus-mod.cfg"
```

```ini
SongsFolder = /home/toi/Musique/CloneHeroSongs
```

Le dossier doit être **un de ceux que Clone Hero scanne** (voir les
réglages du jeu), sinon les charts s'installeront sans jamais apparaître.

---

## Installation depuis zéro — Windows

1. Repère le dossier du jeu (Steam → clic droit → *Gérer* → *Parcourir les
   fichiers locaux*).
2. Télécharge `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+*.zip` sur
   <https://builds.bepinex.dev/projects/bepinex_be> et extrais **tout le
   contenu** à la racine du dossier du jeu (à côté de `Clone Hero.exe`).
3. Si le zip vient d'un navigateur, débloque les fichiers, sinon Windows
   peut refuser de les charger silencieusement :
   ```powershell
   Get-ChildItem -Path "C:\Games\Clone Hero" -Recurse -Filter *.dll | Unblock-File
   ```
4. Lance le jeu une fois (démarrage long : génération des interop).
5. Copie `ChorusMod.dll` dans `<jeu>\BepInEx\plugins\chorus\`.
6. Relance et cherche `Chorus Mod` dans `BepInEx\LogOutput.log`.

---

## Compiler le plugin

Le chemin du jeu n'est **jamais** codé en dur. Trois façons de le fournir,
par ordre de priorité :

**1. Fichier local (recommandé)**

```bash
cp ChorusMod.user.props.example ChorusMod.user.props
# puis édite le chemin dedans
```

Ce fichier est ignoré par git : chacun garde son chemin sans conflit.

**2. Variable d'environnement**

```bash
export CLONEHERO_DIR="$HOME/.local/share/Steam/steamapps/common/Clone Hero"
dotnet build -c Release
```

**3. Ligne de commande**

```bash
dotnet build -c Release -p:CloneHeroDir="/chemin/vers/Clone Hero"
```

Puis :

```bash
dotnet restore
dotnet build -c Release
```

La DLL sort dans `bin/Release/net6.0/ChorusMod.dll`. Aucune dépendance à
copier avec : toutes les références sont en `Private=false` (elles
existent déjà côté jeu) et `System.Text.Json` fait partie du runtime .NET 6
qu'embarque BepInEx.

Si le chemin est faux, la compilation s'arrête sur un message explicite
plutôt que sur une avalanche d'erreurs de types manquants.

---

## Utilisation

| Action | Commande |
| --- | --- |
| Ouvrir / fermer | **F9** (configurable) ou **Échap** |
| Lancer la recherche | **Entrée** ou bouton *Chercher* |
| Faire défiler | molette |
| Coller depuis le presse-papier | bouton *Coller* |

**Filtres.** Instrument, difficulté, et champ ciblé (titre, artiste,
album, genre, charter). Les *Filtres avancés* dépliables ajoutent 11
critères en tri-état : un clic pour **exiger**, deux pour **exclure**,
trois pour revenir à indifférent.

Sans texte de recherche mais avec des filtres avancés actifs, tu peux
parcourir toutes les charts possédant telle caractéristique.

**Installation.** Le bouton *Installer* télécharge, extrait dans le
dossier Songs, puis déclenche le rescan du jeu. Les charts hébergées sur
des dossiers Google Drive ne peuvent pas être récupérées automatiquement :
le bouton devient *Ouvrir* et passe la main au navigateur.

Le rescan nécessite que l'objet `SongScan` existe dans la scène : reste au
menu principal ou à l'écran de sélection pendant les téléchargements.

---

## Configuration

`<jeu>/BepInEx/config/fr.lucas.chorus-mod.cfg`, généré au premier
lancement.

### `[General]`

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `SongsFolder` | auto-détecté | Dossier d'installation. **Doit être scanné par Clone Hero.** |
| `ToggleKey` | `F9` | Touche d'ouverture. |
| `BlockGameInput` | `true` | Empêche les touches tapées de déclencher les raccourcis du jeu. |
| `FullScan` | `false` | Passe à `true` si les nouveaux morceaux n'apparaissent pas après installation. |
| `PanelWidth` | `1180` | Largeur de l'overlay en pixels. |
| `PanelHeight` | `780` | Hauteur de l'overlay. |
| `ShowAlbumArt` | `true` | Jaquettes. À couper si l'affichage d'images ne fonctionne pas sur ta build. |
| `PanelOpacityLayers` | `8` | Opacité du fond quand la teinte n'est pas disponible. |

### `[API]`

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `BaseUrl` | `https://api.enchor.us` | Hôte de l'API. |
| `SearchEndpoint` | `/search` | Recherche libre (POST). |
| `FilesBaseUrl` | `https://files.enchor.us` | Hôte des fichiers (charts et jaquettes). |
| `Instrument` | `guitar` | Instrument sélectionné à l'ouverture. |
| `LogRawResponse` | `true` | Dump le JSON du premier résultat. **À passer à `false`** une fois que tout marche. |

> Ces réglages permettent de suivre un changement d'URL de l'API sans
> recompiler.

---

## Dépannage

**Le plugin ne se charge pas.** Cherche `Chorus Mod` dans
`BepInEx/LogOutput.log`. Absent → BepInEx ne voit pas la DLL : vérifie le
chemin `BepInEx/plugins/chorus/`, la version de BepInEx (`be.755`), et
sous Windows le déblocage des fichiers.

**Pour voir plus de détails**, passe le log en verbeux dans
`BepInEx/config/BepInEx.cfg`, **dans les deux sections** :

```ini
[Logging.Console]
LogLevels = All

[Logging.Disk]
LogLevels = All
```

**Recherche en échec (HTTP 405).** Le `.cfg` contient une ancienne URL.
BepInEx ne remplace jamais une valeur existante quand les défauts
changent : corrige `BaseUrl` / `SearchEndpoint` à la main, ou supprime le
fichier pour le régénérer.

**Les morceaux n'apparaissent pas après installation.** Passe
`FullScan = true`. Si ça persiste, vérifie que `SongsFolder` fait bien
partie des dossiers scannés dans les réglages du jeu.

**Rewired est cassé (« Rewired is not initialized » en boucle).** Redémarre
le jeu. Ce symptôme venait d'anciennes versions qui désactivaient l'Input
Manager ; l'approche actuelle n'y touche plus.

---

## Notes techniques : limites IL2CPP de cette build

Le stripping managé d'Unity a retiré du binaire un certain nombre de
méthodes que le jeu n'utilise pas. Elles existent dans les DLLs de
référence mais lèvent `NotSupportedException: Method unstripping failed`
à l'appel — impossible à contourner par une injection, le code natif
n'existe tout simplement pas.

Cartographie établie par tests sur `v1.1.0.6142` :

| Fonctionne | Ne fonctionne pas |
| --- | --- |
| `GUI.Box`, `GUI.Label`, `GUI.Button` | `GUI.TextField`, `GUILayout.TextField` |
| `GUI.BeginGroup` / `EndGroup` | `GUILayout.Window` (`DoWindow`) |
| `GUI.color` (teinte) | `GUILayout.Space` |
| `Texture2D.whiteTexture` | `GUI.DrawTexture`, `Graphics.DrawTexture` |
| fond de `GUIStyle` (`normal.background`) | `GUI.Label`/`Box` prenant une `Texture` |
| `UnityWebRequestTexture` | ctor `Texture2D(int,int)` |
| `Object.FindObjectOfType` | ctor `RectOffset(int,int,int,int)` |
| | `Resources.FindObjectsOfTypeAll` |

Conséquences dans le code :

- **Champ de saisie maison** : les frappes sont lues via `Event.current`
  et le texte rendu dans un `GUI.Label` (voir `ChorusUI.HandleTextInput`).
- **Pas de fenêtre déplaçable** : panneau à position fixe, centré.
- **Défilement manuel** : offset piloté à la molette + `GUI.BeginGroup`
  pour le clipping.
- **Images via fond de `GUIStyle`** : la texture est assignée en
  `normal.background` puis dessinée par une `GUI.Box` vide. C'est le seul
  chemin de rendu d'image survivant (voir `Theme.DrawImage`, qui teste six
  méthodes et retient celle qui passe).
- **Aucune création de texture** : les jaquettes viennent de
  `UnityWebRequestTexture`, qui les fabrique côté natif.

`Theme.DrawImage` journalise le mode retenu au premier affichage. Sur une
autre version du jeu, ces disponibilités peuvent différer : le code teste
et se replie automatiquement, donc au pire une fonctionnalité cosmétique
disparaît avec un avertissement dans le log, sans planter.

### Blocage des entrées

Trois approches ont été essayées avant celle en place :

1. `SetActive(false)` sur *Rewired Input Manager* → désinitialise Rewired
   **définitivement**. À proscrire.
2. `enabled = false` sur ses composants → même conséquence.
3. `ReInput.controllers.Keyboard.enabled = false` → s'applique bien
   (vérifié par relecture du flag) mais insuffisant : le jeu lit certaines
   touches via l'`Input` legacy d'Unity.

La solution retenue est un **préfixe Harmony** sur `Input.GetKey*`, qui
renvoie « touche non pressée » tant que l'overlay est ouvert. Aucun état
n'est modifié, et le patch se retire proprement.

### API enchor.us

Endpoints vérifiés par capture réseau puis testés :

- `POST /search` — corps
  `{search, page, instrument, difficulty, drumType, drumsReviewed, source}`.
  `difficulty` accepte `null`, `easy`, `medium`, `hard`, `expert`.
- `POST /search/advanced` — champs texte sous forme
  `{value, exact, exclude}` (`name`, `artist`, `album`, `genre`, `year`,
  `charter`), bornes `min*`/`max*`, et 11 booléens tri-état
  (`null` = indifférent, `true` = exiger, `false` = exclure).
  Réponse : `{found, out_of, page, data, search_time_ms}` — `found` donne
  le total, absent de l'endpoint simple.
  **Ne gère pas de recherche libre** : une clé `search` y est ignorée.
- Fichiers : `https://files.enchor.us/{md5}.sng` pour les charts,
  `{albumArtMd5}.jpg` pour les jaquettes.
