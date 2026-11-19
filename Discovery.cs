using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Xml.Linq;

static class Collapse
{
    static readonly string[] Clients = {"Minecraft.Windows.exe","Minecraft.exe","Minecraft.WindowsPreview.exe"};
    static readonly HashSet<string> Skip = new HashSet<string>(new[]{"Windows","node_modules",".git","resource_packs","behavior_packs","minecraftWorlds","GPUCache","Cache","Code Cache","data"},StringComparer.OrdinalIgnoreCase);
    static string[] Safe(Func<string[]> read)
    {
        try { return read(); }
        catch (IOException) { return new string[0]; }
        catch (UnauthorizedAccessException) { return new string[0]; }
    }

    internal static string Client(string folder)
    {
        return Safe(() => Directory.GetFiles(folder,"*Minecraft*.exe"))
            .FirstOrDefault(file => Clients.Contains(Path.GetFileName(file),StringComparer.OrdinalIgnoreCase));
    }

    internal static string Version(string exe)
    {
        string folder = Path.GetDirectoryName(exe);
        foreach (string parent in new[] {folder, Path.GetDirectoryName(folder)})
        {
            if (parent == null) continue;
            try
            {
                var identity = XElement.Load(Path.Combine(parent,"AppxManifest.xml")).Elements().FirstOrDefault(x => x.Name.LocalName == "Identity");
                string version = identity == null ? null : (string)identity.Attribute("Version");
                if (!String.IsNullOrWhiteSpace(version)) return BedrockVersions.DisplayVersion(version);
            }
            catch (IOException) {} catch (UnauthorizedAccessException) {} catch (System.Xml.XmlException) {}
        }
        try
        {
            string version = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (!String.IsNullOrWhiteSpace(version)) return BedrockVersions.DisplayVersion(version);
        }
        catch (IOException) {} catch (UnauthorizedAccessException) {} catch (System.ComponentModel.Win32Exception) {}
        var match = Regex.Match(folder + "\\", @"(?:^|[\\_\- ])(\d+\.\d+\.\d+(?:\.\d+)?)(?=[\\_\- ]|$)");
        return match.Success ? match.Groups[1].Value : "Версия не определена";
    }

    internal static void Scan(Action<string,string> report)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string downloads = Path.Combine(user,"Downloads");
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
        {
            var configured = key == null ? null : key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            if (!String.IsNullOrWhiteSpace(configured)) downloads = Environment.ExpandEnvironmentVariables(configured);
        }
        var roots = new List<string> {desktop,downloads,Path.Combine(user,"Downloads"),
            Path.Combine(Path.GetPathRoot(user),"XboxGames"),Path.Combine(user,"Documents"),user};
        roots.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
            .Select(d => d.RootDirectory.FullName));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (String.IsNullOrWhiteSpace(root)) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string folder = pending.Pop();
                if (!seen.Add(folder)) continue;
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 && folder != root) continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                string exe = Client(folder);
                if (exe != null) report(folder,Version(exe));
                foreach (string child in Safe(() => Directory.GetDirectories(folder))
                    .OrderBy(p => p.IndexOf("minecraft",StringComparison.OrdinalIgnoreCase) >= 0))
                    if (!Skip.Contains(Path.GetFileName(child))) pending.Push(child);
            }
        }
    }

}
