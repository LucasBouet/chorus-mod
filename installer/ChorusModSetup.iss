; ============================================================================
;  Chorus Mod for Clone Hero -- Inno Setup installer (fully self-contained)
; ============================================================================
;  Build with Inno Setup 6 (free): https://jrsoftware.org/isinfo.php
;  Open this file in the Inno Setup IDE, then Build > Compile (Ctrl+F9).
;
;  BEFORE COMPILING, this folder must contain:
;    ChorusModSetup.iss   (this file)
;    config-template.cfg
;    ChorusMod.dll        (dotnet build -c Release, copied from bin\Release\net6.0\)
;    bepinex\             (BepInEx 6.0.0-be.755 win-x64, already unzipped --
;                           winhttp.dll, doorstop_config.ini,
;                           .doorstop_version, changelog.txt at its root,
;                           then BepInEx\core\ and dotnet\)
;
;  NOTHING IS DOWNLOADED DURING INSTALLATION: everything is bundled in the
;  generated Setup.exe. No internet connection required on the user's side,
;  no risk of dead links.
;
;  WHAT THE INSTALLER DOES:
;    - Asks for the Clone Hero folder (with validation)
;    - Offers options (full rescan, album art, keyboard blocking,
;      panel size)
;    - Installs BepInEx 6.0.0-be.755 + ChorusMod.dll into the game
;    - Generates the pre-filled .cfg based on the choices made
;    - Automatic uninstaller ([Files] entries tracked natively by
;      Inno, + the generated .cfg removed via [UninstallDelete])
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
; No real "Program Files": the app installs INSIDE the game folder,
; chosen by the person in the next step. DefaultDirName is only a
; fallback if auto-detection (GuessGameDir, in [Code]) finds nothing --
; deliberately neutral, no Steam assumption.
DefaultDirName=C:\Clone Hero
DisableProgramGroupPage=yes
; EXISTING game folder, not a new folder to create.
DirExistsWarning=no
PrivilegesRequired=lowest
OutputBaseFilename=ChorusMod-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName} for Clone Hero

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; --- The plugin ---
Source: "ChorusMod.dll"; DestDir: "{app}\BepInEx\plugins\chorus"; Flags: ignoreversion

; --- Config template: bundled in the exe, not copied as-is --
; read and filled in at runtime in [Code] (see CurStepChanged). {src} only
; works at compile time, not in the final Setup.exe: dontcopy +
; ExtractTemporaryFile is the correct way to access an auxiliary file from
; [Code] once the installer is compiled.
Source: "config-template.cfg"; DestDir: "{tmp}"; Flags: dontcopy

; --- BepInEx: root files (next to Clone Hero.exe) ---
Source: "bepinex\winhttp.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bepinex\doorstop_config.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "bepinex\.doorstop_version"; DestDir: "{app}"; Flags: ignoreversion
Source: "bepinex\changelog.txt"; DestDir: "{app}"; Flags: ignoreversion

; --- BepInEx: engine (flat folders, a single wildcard is enough) ---
Source: "bepinex\BepInEx\core\*"; DestDir: "{app}\BepInEx\core"; Flags: ignoreversion recursesubdirs

; --- .NET runtime bundled by this CoreCLR build (~67 MB) ---
Source: "bepinex\dotnet\*"; DestDir: "{app}\dotnet"; Flags: ignoreversion recursesubdirs

[UninstallDelete]
; The only dynamically generated file (not via [Files], so not tracked
; automatically by the uninstaller): the .cfg reflecting the choices made
; at install time.
Type: files; Name: "{app}\BepInEx\config\fr.lucas.chorus-mod.cfg"

[Code]
var
  OptionsPage: TWizardPage;
  ChkFullScan, ChkAlbumArt, ChkBlockInput: TCheckBox;
  CmbPanelSize: TComboBox;
  EdtSongsFolder: TEdit;
  BtnBrowseSongs: TButton;

// ---------------------------------------------------------------------
//  Auto-detects a few plausible locations, just to pre-fill the field --
//  the person stays in control to correct it. Covers Steam AND a
//  "portable" install downloaded from clonehero.net, which has no
//  standard location.
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
    'Choose the folder where downloaded songs will be installed:', Dir, True
  ) then
    EdtSongsFolder.Text := Dir;
end;

// ---------------------------------------------------------------------
//  Wizard pages
// ---------------------------------------------------------------------
procedure InitializeWizard;
var
  Guessed: String;
  Y: Integer;
begin
  // Pre-fills Inno's standard folder field with an auto-detection;
  // the person stays in control to correct it.
  Guessed := GuessGameDir();
  if Guessed <> '' then
    WizardForm.DirEdit.Text := Guessed;

  // --- Options page, inserted right after the standard folder page ---
  OptionsPage := CreateCustomPage(
    wpSelectDir,
    'Chorus Mod Options',
    'Settings applied from the first launch onward (changeable later '
      + 'in the .cfg file without reinstalling).'
  );

  Y := 0;
  ChkFullScan := TCheckBox.Create(OptionsPage);
  ChkFullScan.Parent := OptionsPage.Surface;
  ChkFullScan.Top := Y;
  ChkFullScan.Width := OptionsPage.SurfaceWidth;
  ChkFullScan.Caption := 'Full rescan after each install';
  ChkFullScan.Checked := True;
  Y := Y + 18;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Left := 20;
    Width := OptionsPage.SurfaceWidth - 20;
    Caption :=
      'Strongly recommended: if unchecked, downloaded songs won''t '
        + 'automatically show up in the game.';
    Font.Color := clGrayText;
  end;
  Y := Y + 24;

  ChkAlbumArt := TCheckBox.Create(OptionsPage);
  ChkAlbumArt.Parent := OptionsPage.Surface;
  ChkAlbumArt.Top := Y;
  ChkAlbumArt.Width := OptionsPage.SurfaceWidth;
  ChkAlbumArt.Caption := 'Show album art in results';
  ChkAlbumArt.Checked := True;
  Y := Y + 24;

  ChkBlockInput := TCheckBox.Create(OptionsPage);
  ChkBlockInput.Parent := OptionsPage.Surface;
  ChkBlockInput.Top := Y;
  ChkBlockInput.Width := OptionsPage.SurfaceWidth;
  ChkBlockInput.Caption :=
    'Block game keys while typing (avoids Space = Control Remapper)';
  ChkBlockInput.Checked := True;
  Y := Y + 32;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Caption := 'Songs install folder:';
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
  BtnBrowseSongs.Caption := 'Browse';
  BtnBrowseSongs.OnClick := @BrowseSongsClicked;
  Y := Y + 24;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Width := OptionsPage.SurfaceWidth;
    Caption :=
      'Must be a folder that Clone Hero scans (check the game''s '
        + 'settings). Leave empty for auto-detection.';
    Font.Color := clGrayText;
  end;
  Y := Y + 30;

  with TNewStaticText.Create(OptionsPage) do
  begin
    Parent := OptionsPage.Surface;
    Top := Y;
    Caption := 'Panel size:';
  end;
  Y := Y + 20;

  CmbPanelSize := TComboBox.Create(OptionsPage);
  CmbPanelSize.Parent := OptionsPage.Surface;
  CmbPanelSize.Top := Y;
  CmbPanelSize.Width := 220;
  CmbPanelSize.Style := csDropDownList;
  CmbPanelSize.Items.Add('Normal (1180x780)');
  CmbPanelSize.Items.Add('Large (1400x900)');
  CmbPanelSize.Items.Add('Compact (900x600)');
  CmbPanelSize.ItemIndex := 0;
end;

// ---------------------------------------------------------------------
//  Suggests a Songs folder as soon as the options page is reached, only
//  once -- if the person goes back and changes the game folder, we don't
//  want to overwrite a choice they already made by hand.
// ---------------------------------------------------------------------
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = OptionsPage.ID) and (EdtSongsFolder.Text = '') then
    EdtSongsFolder.Text := WizardForm.DirEdit.Text + '\Songs';
end;

// ---------------------------------------------------------------------
//  Validation: the chosen folder must actually contain the game.
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
        '"Clone Hero.exe" was not found in this folder:' + #13#10 + Dir
          + #13#10#13#10 + 'Check the path: it should be the folder '
          + 'containing "Clone Hero.exe", whether the game came from '
          + 'Steam or a direct download from clonehero.net.',
        mbError, MB_OK
      );
      Result := False;
    end;
  end;
end;

// ---------------------------------------------------------------------
//  Utilities
// ---------------------------------------------------------------------
function BoolToCfg(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

// ---------------------------------------------------------------------
//  Main step: called after the declared files are copied, before the
//  finish screen. Only config generation happens here -- BepInEx and the
//  plugin are already in place via [Files].
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

  WizardForm.StatusLabel.Caption := 'Writing configuration...';
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
    // LoadStringFromFile only works with AnsiString; StringChangeEx only
    // accepts String (Unicode). Hence the explicit conversion -- safe
    // here since every substituted value (true/false, numbers, "F9") is
    // pure ASCII; only the template's comments contain characters that
    // survive the round-trip.
    ConfigText := String(ConfigAnsi);

    // False (4th argument): no environment variable expansion on this
    // path -- a folder containing a literal '%' (rare but possible)
    // must not be misinterpreted.
    StringChangeEx(ConfigText, '{{SONGS_FOLDER}}', EdtSongsFolder.Text, False);
    StringChangeEx(ConfigText, '{{TOGGLE_KEY}}', 'F9', True);
    StringChangeEx(ConfigText, '{{BLOCK_INPUT}}', BoolToCfg(ChkBlockInput.Checked), True);
    StringChangeEx(ConfigText, '{{FULL_SCAN}}', BoolToCfg(ChkFullScan.Checked), True);
    StringChangeEx(ConfigText, '{{PANEL_WIDTH}}', PanelW, True);
    StringChangeEx(ConfigText, '{{PANEL_HEIGHT}}', PanelH, True);
    StringChangeEx(ConfigText, '{{SHOW_ALBUM_ART}}', BoolToCfg(ChkAlbumArt.Checked), True);

    // The plugin refuses to install a song if this folder doesn't exist
    // yet -- create it now rather than letting the first download fail.
    if EdtSongsFolder.Text <> '' then
      ForceDirectories(EdtSongsFolder.Text);

    SaveStringToFile(
      Dir + '\BepInEx\config\fr.lucas.chorus-mod.cfg', AnsiString(ConfigText), False
    );
  end
  else
    MsgBox(
      'config-template.cfg was not found next to the installer -- '
        + 'BepInEx''s default config will be used instead '
        + '(nothing serious, just less convenient: adjust it by hand).',
      mbInformation, MB_OK
    );
end;

[Run]
Filename: "{app}\Clone Hero.exe"; \
  Description: "Launch Clone Hero now (first startup takes longer -- BepInEx generates its files)"; \
  Flags: postinstall nowait skipifsilent unchecked
