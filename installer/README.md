# Installeur Chorus Mod

Un `Setup.exe` Windows classique, **tout embarqué** : sélection du dossier
Clone Hero, page d'options (rescan, jaquettes, blocage clavier, taille du
panneau), installation de BepInEx + du plugin, désinstalleur automatique.

**Aucune connexion internet requise côté utilisateur** : BepInEx est
embarqué directement dans le Setup.exe, pas téléchargé à l'installation.
Zéro risque de lien mort.

## Ce dont tu as besoin pour CONSTRUIRE l'installeur (une fois)

- Le SDK .NET 6 (pour compiler `ChorusMod.dll`)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (gratuit)
- BepInEx 6.0.0-be.755 (win-x64), déjà dézippé

## Ce dont la PERSONNE QUI INSTALLE a besoin

Rien. Ni SDK .NET, ni BepInEx, ni connexion internet, ni connaissance
technique.

---

## Construire l'installeur

### 1. Compile le plugin comme d'habitude

Depuis la racine du dépôt :

```bash
dotnet build -c Release
```

### 2. Rassemble les trois éléments à côté du script installeur

```
installer\
  ChorusModSetup.iss
  config-template.cfg
  ChorusMod.dll          <- copiée depuis ..\bin\Release\net6.0\
  bepinex\                <- BepInEx dézippé (voir ci-dessous)
    winhttp.dll
    doorstop_config.ini
    .doorstop_version
    changelog.txt
    BepInEx\core\...
    dotnet\...
```

Pour le dossier `bepinex\` : télécharge
`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+*.zip` depuis
<https://builds.bepinex.dev/projects/bepinex_be> et dézippe-le tel quel
dans `installer\bepinex\`.

> `ChorusMod.dll`, `bepinex\` et `Output\` ne sont pas suivis par git
> (voir `.gitignore`) : ce sont des artefacts de build, pas du code
> source. Chacun les régénère localement.

> Ce dossier contient le runtime .NET embarqué par BepInEx (dossier
> `dotnet\`, ~67 Mo) — c'est normal et volontaire, c'est ce qui permet à
> l'installeur final de ne rien avoir à télécharger.

### 3. Compile l'installeur

Ouvre `ChorusModSetup.iss` dans l'IDE Inno Setup, puis **Build → Compile**
(`Ctrl+F9`). Le résultat sort dans `Output\ChorusMod-Setup.exe`
(attends-toi à quelque chose autour de 25-35 Mo, le runtime .NET embarqué
compresse bien).

C'est ce fichier, et lui seul, que tu distribues ou gardes pour une
réinstallation sur un nouveau PC.

---

## Utilisation (côté personne qui installe)

1. Double-clic sur `ChorusMod-Setup.exe`
2. Choisir le dossier Clone Hero (pré-rempli si détecté automatiquement,
   sinon Parcourir — celui qui contient `Clone Hero.exe`)
3. Cocher les options voulues
4. Suivant → Installer (rapide, tout est déjà dans le Setup.exe)
5. Optionnel : lancer le jeu directement depuis la dernière page

Premier lancement du jeu plus long que d'habitude (BepInEx génère ses
fichiers d'interop). Ensuite, **F9** en jeu ouvre Chorus Mod.

## Désinstallation

Panneau de configuration Windows → *Applications* → *Chorus Mod* →
Désinstaller. Les fichiers de BepInEx et du plugin sont suivis nativement
par Inno (déclarés en `[Files]`) donc retirés automatiquement ; le fichier
de config généré à l'installation (`fr.lucas.chorus-mod.cfg`, dont le
contenu dépend des choix faits) est retiré via une entrée
`[UninstallDelete]` dédiée puisqu'il n'existe pas au moment de la
compilation. Le dossier `Songs` n'est jamais touché.

## Mettre à jour la version de BepInEx embarquée

Remplace le contenu de `bepinex\` par la nouvelle version dézippée, ajuste
`#define BepInExVersion` en haut du `.iss` (purement informatif, affiché
nulle part sauf dans les commentaires), et recompile. Comme tout est
embarqué, aucune URL à maintenir à jour.

⚠️ Les builds BepInEx bleeding edge cassent régulièrement la compatibilité
entre elles (vécu pendant le développement — voir les notes techniques du
README du plugin). Ne mets pas à jour sans retester le plugin en entier.

## Limites connues

- **Windows uniquement.** Inno Setup ne produit pas d'installeur Linux.
  Pour Linux, la procédure manuelle du README principal du plugin reste
  la référence.
