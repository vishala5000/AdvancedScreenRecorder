[Setup]
AppName=V Rec
AppVersion=1.0
DefaultDirName={pf}\V Rec
DefaultGroupName=V Rec
OutputDir=Output
OutputBaseFilename=VRec_Setup
Compression=lzma
SolidCompression=yes

[Files]
Source: "bin\Release\net48\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\V Rec"; Filename: "{app}\V Rec.exe"
Name: "{commondesktop}\V Rec"; Filename: "{app}\V Rec.exe"

[Run]
Filename: "{app}\V Rec.exe"; Description: "Launch V Rec"; Flags: nowait postinstall skipifsilent
