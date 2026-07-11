; IsoForge - script do instalador (Inno Setup 6)
; Compilar com: ISCC.exe installer\IsoForge.iss
; Espera o exe publicado em: bin\Release\net8.0-windows\win-x64\publish\IsoForge.exe

#define AppName "IsoForge"
; AppVersion pode ser sobrescrito pela linha de comando (CI): ISCC /DAppVersion=1.2.3
#ifndef AppVersion
  #define AppVersion "1.4.12"
#endif
#define AppPublisher "IsoForge"
#define AppExe "IsoForge.exe"

[Setup]
AppId={{A7E3F2C1-9B4D-4E7A-8C21-2F6D5B0A1E44}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=IsoForge-Setup
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Instala em Arquivos de Programas -> requer elevação
PrivilegesRequired=admin
; Fecha o IsoForge em execução (ex.: durante o auto-update) para poder substituir o exe.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "..\bin\Release\net8.0-windows\win-x64\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Dirs]
; Pasta compartilhada dos instaladores, com escrita para todos os usuários
Name: "{commonappdata}\IsoForge\Installers"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\icon.ico"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Run]
; Instalação interativa: oferece abrir o app ao final (o app baixa os instaladores sozinho ao abrir).
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName}"; Flags: nowait postinstall skipifsilent
; Auto-update silencioso: reabre o app direto, sem mensagem.
Filename: "{app}\{#AppExe}"; Flags: nowait postinstall; Check: WizardSilent
