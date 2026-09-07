; ============================================================================
;  Chorus Mod for Clone Hero -- installeur Inno Setup (tout embarqué)
; ============================================================================
;  Compile avec Inno Setup 6 (gratuit) : https://jrsoftware.org/isinfo.php
;  Ouvre ce fichier dans l'IDE Inno Setup puis Build > Compile (Ctrl+F9).
;
;  AVANT DE COMPILER, ce dossier doit contenir :
;    ChorusModSetup.iss   (ce fichier)
;    config-template.cfg
;    ChorusMod.dll        (dotnet build -c Release, copiée depuis bin\Release\net6.0\)
;    bepinex\             (BepInEx 6.0.0-be.755 win-x64, déjà dézippé --
;                           winhttp.dll, doorstop_config.ini,
;                           .doorstop_version, changelog.txt à sa racine,
;                           puis BepInEx\core\ et dotnet\)
;
;  RIEN N'EST TÉLÉCHARGÉ PENDANT L'INSTALLATION : tout est embarqué dans le
;  Setup.exe généré. Aucune connexion internet requise côté utilisateur,
;  aucun risque de lien mort.
;
;  CE QUE FAIT L'INSTALLEUR :
;    - Demande le dossier Clone Hero (avec validation)
;    - Propose des options (rescan complet, jaquettes, blocage clavier,
;      taille du panneau)
;    - Installe BepInEx 6.0.0-be.755 + ChorusMod.dll dans le jeu
;    - Génère le .cfg pré-rempli selon les choix
;    - Désinstalleur automatique (fichiers [Files] suivis nativement par
;      Inno, + le .cfg généré retiré via [UninstallDelete])
; ============================================================================

#define MyAppName "Chorus Mod"
#define MyAppVersion "1.0"
#define MyAppPublisher "Lucas"
#define BepInExVersion "6.0.0-be.755"

[Setup]
AppId={{5B6C9F2E-CHORUS-MOD-CLONE-HERO-0001}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Pas de vrai "Program Files" : l'appli s'installe DANS le dossier du jeu,
; choisi par la personne à l'étape suivante. DefaultDirName n'est qu'un
; repli si la détection auto (GuessGameDir, dans [Code]) ne trouve rien --
; volontairement neutre, pas d'hypothèse Steam.
DefaultDirName=C:\Clone Hero
DisableProgramGroupPage=yes
; Dossier EXISTANT du jeu, pas un nouveau dossier à créer.
DirExistsWarning=no
PrivilegesRequired=lowest
OutputBaseFilename=ChorusMod-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName} pour Clone Hero

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Messages]
french.SelectDirDesc=Choisis le dossier qui contient "Clone Hero.exe" (là où tu as installé ou extrait le jeu -- via Steam, ou téléchargé directement sur clonehero.net).
french.SelectDirLabel3=Chorus Mod et BepInEx seront installés dans ce dossier EXISTANT du jeu :
french.SelectDirBrowseLabel=Pour continuer, clique sur Suivant. Si tu veux choisir un autre dossier, clique sur Parcourir.

[Files]
; --- Le plugin ---
Source: "ChorusMod.dll"; DestDir: "{app}\BepInEx\plugins\chorus"; Flags: ignoreversion

; --- Gabarit de config : embarqué dans l'exe, pas copié tel quel --
; lu et complété au runtime dans [Code] (voir CurStepChanged). {src} ne
; fonctionne qu'au moment de la compilation, pas dans le Setup.exe final :
; dontcopy + ExtractTemporaryFile est la façon correcte d'accéder à un
; fichier auxiliaire depuis [Code] une fois l'installeur compilé.
Source: "config-template.cfg"; DestDir: "{tmp}"; Flags: dontcopy

; --- BepInEx : fichiers racine (à côté de Clone Hero.exe) ---
Source: "bepinex\winhttp.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bepinex\doorstop_config.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "bepinex\.doorstop_version"; DestDir: "{app}"; Flags: ignoreversion
Source: "bepinex\changelog.txt"; DestDir: "{app}"; Flags: ignoreversion

; --- BepInEx : moteur (dossiers plats, un seul caractère générique suffit) ---
Source: "bepinex\BepInEx\core\*"; DestDir: "{app}\BepInEx\core"; Flags: ignoreversion recursesubdirs

; --- Runtime .NET embarqué par cette build CoreCLR (~67 Mo) ---
Source: "bepinex\dotnet\*"; DestDir: "{app}\dotnet"; Flags: ignoreversion recursesubdirs

[UninstallDelete]
; Seul fichier généré dynamiquement (pas via [Files], donc pas suivi
; automatiquement par le désinstalleur) : le .cfg selon les choix faits
; à l'installation.
Type: files; Name: "{app}\BepInEx\config\fr.lucas.chorus-mod.cfg"

[Code]
var
  OptionsPage: TWizardPage;
  ChkFullScan, ChkAlbumArt, ChkBlockInput: TCheckBox;
  CmbPanelSize: TComboBox;
  EdtSongsFolder: TEdit;
  BtnBrowseSongs: TButton;

// ---------------------------------------------------------------------
//  Détection auto de quelques emplacements plausibles, juste pour
//  pré-remplir le champ -- la personne garde la main pour corriger.
//  Couvre Steam ET une install "portable" téléchargée sur clonehero.net,
//  qui n'a pas d'emplacement standard.
// ---------------------------------------------------------------------
function GuessGameDir(): String;
var
  Candidates: TArrayOfString;
  I: Integer;
begin
  Result := '';
  SetArrayLength(Candidates, 6);
  Candidates[0] := ExpandConstant('{autopf}\Steam\steamapps\common\Clone Hero');
  Candidates[1] := ExpandConstant('{pf32}\Steam\steamapps\common\Clone Hero');
  Candidates[2] := 'D:\SteamLibrary\steamapps\common\Clone Hero';
  Candidates[3] := ExpandConstant('{userdocs}\Clone Hero');
  Candidates[4] := ExpandConstant('{localappdata}\Clone Hero');
  Candidates[5] := 'C:\Clone Hero';

  for I := 0 to GetArrayLength(Candidates) - 1 do
  begin
    if FileExists(Candidates[I] + '\Clone Hero.exe') then
    begin
      Result := Candidates[I];
      Exit;
    end;
  end;
end;

procedure BrowseSongsClicked(Sender: TObject);
var
  Dir: String;
begin
  Dir := EdtSongsFolder.Text;
  if Dir = '' then
    Dir := WizardForm.DirEdit.Text + '\Songs';

  if BrowseForFolder(
    'Choisis le dossier où installer les morceaux téléchargés :', Dir, True
  ) then
    EdtSongsFolder.Text := Dir;
end;

// ---------------------------------------------------------------------
//  Pages de l'assistant
// ---------------------------------------------------------------------
procedure InitializeWizard;
var
  Guessed: String;
  Y: Integer;
begin
  // Pré-remplit le champ dossier standard d'Inno avec une détection auto ;
  // la personne garde la main pour corriger.
  Guessed := GuessGameDir();
  if Guessed <> '' then
    WizardForm.DirEdit.Text := Guessed;

  // --- Page d'options, insérée juste après la page dossier standard ---
  OptionsPage := CreateCustomPage(
    wpSelectDir,
    'Options de Chorus Mod',
    'Réglages appliqués dès le premier lancement (modifiables ensuite '
      + 'dans le fichier .cfg sans réinstaller).'
  );

  Y := 0;
  ChkFullScan := TCheckBox.Create(OptionsPage);
  ChkFullScan.Parent := OptionsPage.Surface;
  ChkFullScan.Top := Y;
  ChkFullScan.Width := OptionsPage.SurfaceWidth;
  ChkFullScan.Caption := 'Rescan complet après chaque installation';
  ChkFullScan.Checked := True;
  Y := Y + 18;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Left := 20;
    Width := OptionsPage.SurfaceWidth - 20;
    Caption :=
      'Fortement recommandé : décoché, les morceaux téléchargés '
        + 'n''apparaîtront pas automatiquement dans le jeu.';
    Font.Color := clGrayText;
  end;
  Y := Y + 24;

  ChkAlbumArt := TCheckBox.Create(OptionsPage);
  ChkAlbumArt.Parent := OptionsPage.Surface;
  ChkAlbumArt.Top := Y;
  ChkAlbumArt.Width := OptionsPage.SurfaceWidth;
  ChkAlbumArt.Caption := 'Afficher les jaquettes dans les résultats';
  ChkAlbumArt.Checked := True;
  Y := Y + 24;

  ChkBlockInput := TCheckBox.Create(OptionsPage);
  ChkBlockInput.Parent := OptionsPage.Surface;
  ChkBlockInput.Top := Y;
  ChkBlockInput.Width := OptionsPage.SurfaceWidth;
  ChkBlockInput.Caption :=
    'Bloquer les touches du jeu pendant la saisie (evite Espace = Control Remapper)';
  ChkBlockInput.Checked := True;
  Y := Y + 32;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Caption := 'Dossier d''installation des morceaux (Songs) :';
  end;
  Y := Y + 20;

  EdtSongsFolder := TEdit.Create(OptionsPage);
  EdtSongsFolder.Parent := OptionsPage.Surface;
  EdtSongsFolder.Top := Y;
  EdtSongsFolder.Left := 0;
  EdtSongsFolder.Width := OptionsPage.SurfaceWidth - 90;

  BtnBrowseSongs := TButton.Create(OptionsPage);
  BtnBrowseSongs.Parent := OptionsPage.Surface;
  BtnBrowseSongs.Top := Y - 2;
  BtnBrowseSongs.Left := OptionsPage.SurfaceWidth - 80;
  BtnBrowseSongs.Width := 80;
  BtnBrowseSongs.Caption := 'Parcourir';
  BtnBrowseSongs.OnClick := @BrowseSongsClicked;
  Y := Y + 24;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Width := OptionsPage.SurfaceWidth;
    Caption :=
      'Doit être un dossier que Clone Hero scanne (vérifiable dans les '
        + 'réglages du jeu). Laisse vide pour une détection automatique.';
    Font.Color := clGrayText;
  end;
  Y := Y + 30;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Caption := 'Taille du panneau :';
  end;
  Y := Y + 20;

  CmbPanelSize := TComboBox.Create(OptionsPage);
  CmbPanelSize.Parent := OptionsPage.Surface;
  CmbPanelSize.Top := Y;
  CmbPanelSize.Width := 220;
  CmbPanelSize.Style := csDropDownList;
  CmbPanelSize.Items.Add('Normal (1180x780)');
  CmbPanelSize.Items.Add('Grand (1400x900)');
  CmbPanelSize.Items.Add('Compact (900x600)');
  CmbPanelSize.ItemIndex := 0;
end;

// ---------------------------------------------------------------------
//  Suggestion de dossier Songs dès l'arrivée sur la page d'options,
//  une seule fois -- si la personne revient en arrière et modifie le
//  dossier du jeu, on ne veut pas écraser un choix déjà fait à la main.
// ---------------------------------------------------------------------
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = OptionsPage.ID) and (EdtSongsFolder.Text = '') then
    EdtSongsFolder.Text := WizardForm.DirEdit.Text + '\Songs';
end;

// ---------------------------------------------------------------------
//  Validation : le dossier choisi doit vraiment contenir le jeu.
// ---------------------------------------------------------------------
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;

  if CurPageID = wpSelectDir then
  begin
    Dir := WizardForm.DirEdit.Text;
    if not FileExists(Dir + '\Clone Hero.exe') then
    begin
      MsgBox(
        '"Clone Hero.exe" est introuvable dans ce dossier :' + #13#10 + Dir
          + #13#10#13#10 + 'Vérifie le chemin : c''est le dossier où se '
          + 'trouve "Clone Hero.exe", que le jeu vienne de Steam ou d''un '
          + 'téléchargement direct sur clonehero.net.',
        mbError, MB_OK
      );
      Result := False;
    end;
  end;
end;

// ---------------------------------------------------------------------
//  Utilitaires
// ---------------------------------------------------------------------
function BoolToCfg(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

// ---------------------------------------------------------------------
//  Étape principale : appelée après la copie des fichiers déclarés,
//  avant l'écran de fin. Ici, uniquement la génération de la config --
//  BepInEx et le plugin sont déjà en place via [Files].
// ---------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir, ConfigTemplate: String;
  ConfigAnsi: AnsiString;
  ConfigText: String;
  PanelW, PanelH: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Dir := ExpandConstant('{app}');

  WizardForm.StatusLabel.Caption := 'Écriture de la configuration...';
  ForceDirectories(Dir + '\BepInEx\config');

  case CmbPanelSize.ItemIndex of
    1: begin PanelW := '1400'; PanelH := '900'; end;
    2: begin PanelW := '900'; PanelH := '600'; end;
  else
    begin PanelW := '1180'; PanelH := '780'; end;
  end;

  ConfigTemplate := ExpandConstant('{tmp}') + '\config-template.cfg';
  ExtractTemporaryFile('config-template.cfg');
  if LoadStringFromFile(ConfigTemplate, ConfigAnsi) then
  begin
    // LoadStringFromFile ne travaille qu'en AnsiString ; StringChangeEx
    // n'accepte que du String (Unicode). D'où la conversion explicite --
    // sans risque ici puisque toutes les valeurs substituées (true/false,
    // nombres, "F9") sont de l'ASCII pur, seuls les commentaires du
    // template contiennent des accents qui survivent à l'aller-retour.
    ConfigText := String(ConfigAnsi);

    // False (4e argument) : pas d'expansion de variables d'environnement
    // sur ce chemin -- un dossier contenant un '%' littéral (rare mais
    // possible) ne doit pas être mésinterprété.
    StringChangeEx(ConfigText, '{{SONGS_FOLDER}}', EdtSongsFolder.Text, False);
    StringChangeEx(ConfigText, '{{TOGGLE_KEY}}', 'F9', True);
    StringChangeEx(ConfigText, '{{BLOCK_INPUT}}', BoolToCfg(ChkBlockInput.Checked), True);
    StringChangeEx(ConfigText, '{{FULL_SCAN}}', BoolToCfg(ChkFullScan.Checked), True);
    StringChangeEx(ConfigText, '{{PANEL_WIDTH}}', PanelW, True);
    StringChangeEx(ConfigText, '{{PANEL_HEIGHT}}', PanelH, True);
    StringChangeEx(ConfigText, '{{SHOW_ALBUM_ART}}', BoolToCfg(ChkAlbumArt.Checked), True);

    // Le plugin refuse d'installer un morceau si ce dossier n'existe pas
    // encore -- on le crée maintenant plutôt que de laisser échouer le
    // premier téléchargement.
    if EdtSongsFolder.Text <> '' then
      ForceDirectories(EdtSongsFolder.Text);

    SaveStringToFile(
      Dir + '\BepInEx\config\fr.lucas.chorus-mod.cfg', AnsiString(ConfigText), False
    );
  end
  else
    MsgBox(
      'config-template.cfg introuvable à côté de l''installeur -- la '
        + 'config par défaut de BepInEx sera utilisée à la place '
        + '(rien de grave, juste moins pratique : à régler à la main).',
      mbInformation, MB_OK
    );
end;

[Run]
Filename: "{app}\Clone Hero.exe"; \
  Description: "Lancer Clone Hero maintenant (premier démarrage plus long -- BepInEx génère ses fichiers)"; \
  Flags: postinstall nowait skipifsilent unchecked
