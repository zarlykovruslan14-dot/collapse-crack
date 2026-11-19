$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
    $refs=@("$framework/WPF/WindowsBase.dll","$framework/WPF/PresentationCore.dll","$framework/WPF/PresentationFramework.dll","$framework/System.Xaml.dll") | ForEach-Object { "/r:$_" }
    $sources='Launcher.cs','Library.cs','Downloads.cs','Updater.cs','Bedrock.cs','Discovery.cs'
    & "$framework/csc.exe" /nologo /nowarn:0649 /target:winexe /platform:x64 /optimize+ '/out:Collapse Launcher.exe' /win32icon:Collapse.ico /win32manifest:Collapse.manifest /resource:Theme.xaml,Theme.xaml /resource:landscape.png,landscape.png /resource:versions.csv,versions.csv /resource:gdk.json,gdk.json @refs /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Runtime.Serialization.dll /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll @sources
    if($LASTEXITCODE){throw 'Build failed'}
} finally { Pop-Location }
