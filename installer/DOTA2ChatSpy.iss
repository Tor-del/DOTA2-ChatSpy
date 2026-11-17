#ifndef AppVersion
  #define AppVersion "1.2.2"
#endif

[Setup]
AppId={{5646A5C4-A92C-446A-9BDB-45F0CF84706A}
AppName=DOTA2 Chat Spy
AppVersion={#AppVersion}
AppPublisher=DOTA2 Chat Spy
DefaultDirName={localappdata}\Programs\DOTA2 Chat Spy
DefaultGroupName=DOTA2 Chat Spy
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\ChatSpy.ico
UninstallDisplayIcon={app}\DOTA2ChatSpy.exe
OutputDir=..\dist
OutputBaseFilename=DOTA2-Chat-Spy-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\dist\chat-spy-publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{autoprograms}\Dota Chat Explorer.lnk"
Type: files; Name: "{app}\Dota2ChatExtractor.exe"
Type: files; Name: "{app}\Dota2ChatExtractor.dll"
Type: files; Name: "{app}\Dota2ChatExtractor.deps.json"
Type: files; Name: "{app}\Dota2ChatExtractor.runtimeconfig.json"

[Icons]
Name: "{autoprograms}\DOTA2 Chat Spy"; Filename: "{app}\DOTA2ChatSpy.exe"

[Run]
Filename: "{app}\DOTA2ChatSpy.exe"; Description: "{cm:LaunchProgram,DOTA2 Chat Spy}"; Flags: nowait postinstall skipifsilent
