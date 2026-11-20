using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Drawing;

namespace CollapseLauncher
{
    [DataContract] public sealed class Settings
    {
        [DataMember] public List<Pack> Packs = new List<Pack>();
        [DataMember] public List<Skin> Skins = new List<Skin>();
        [DataMember] public List<Installation> Installations = new List<Installation>();
        [DataMember] public string SelectedFolder;
        [DataMember] public string SelectedSkin;
        [DataMember] public bool DisableVsync = true;
        [DataMember] public string Background;
        [DataMember] public bool AutoUpdate = true;
    }

    [DataContract] public sealed class Pack
    {
        [DataMember] public string Id;
        [DataMember] public int[] Version;
        [DataMember] public string Name;
        [DataMember] public bool Active;
        [DataMember] public string[] Dependencies;
        public string Folder { get { return Path.Combine(Store.Root,"Packs",Id); } }
        public override string ToString() { return Name; }
    }
    [DataContract] public sealed class Skin
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public bool Slim;
        public string FilePath { get { return Path.Combine(Store.Root,"Skins",Id+".png"); } }
        public override string ToString() { return Name + (Slim ? " · Slim" : " · Classic"); }
    }
    [DataContract] public sealed class Installation
    {
        [DataMember] public string Folder;
        [DataMember] public string Version;
        public override string ToString() { return "Bedrock "+Version+"   ·   "+Path.GetFileName(Folder); }
    }
    [DataContract] public sealed class ActivePack
    {
        [DataMember(Name="pack_id")] public string Id;
        [DataMember(Name="version")] public int[] Version;
        [DataMember(Name="subpack",EmitDefaultValue=false)] public string Subpack;
    }

    internal static class Store
    {
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Collapse Launcher");
        public static readonly string StateFile = Path.Combine(Root,"settings.json");
        static readonly Mutex Gate=new Mutex(false,@"Local\CollapseLauncherLibrary");
        public static Settings State;

        public static T Read<T>(string file)
        {
            using(var input=File.OpenRead(file))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input);
        }
        public static byte[] Json<T>(T value)
        {
            using(var data=new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(data,value);
                return data.ToArray();
            }
        }
        public static void Atomic(string file,byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllBytes(temp,data);
                if(File.Exists(file))File.Replace(temp,file,null);
                else File.Move(temp,file);
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        public static void Load()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root,"Packs"));
            Directory.CreateDirectory(Path.Combine(Root,"Skins"));
            State=File.Exists(StateFile) ? Read<Settings>(StateFile) : new Settings();
            if(State.Packs==null)State.Packs=new List<Pack>();
            if(State.Skins==null)State.Skins=new List<Skin>();
            if(State.Installations==null)State.Installations=new List<Installation>();
        }
        public static void Save() { Atomic(StateFile,Json(State)); }
        public static void Exclusive(Action action)
        {
            bool acquired=false;
            try
            {
                try { acquired=Gate.WaitOne(0); } catch(AbandonedMutexException) { acquired=true; }
                if(!acquired)throw new IOException("Библиотека открыта в другом окне Collapse.");
                action();
            }
            finally { if(acquired)Gate.ReleaseMutex(); }
        }
        public static string Child(string root,string relative)
        {
            if(Path.IsPathRooted(relative) || relative.IndexOf(':')>=0)
                throw new InvalidDataException("Недопустимый путь в архиве.");
            string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив содержит путь вне папки.");
            return full;
        }
        public static void CopyTree(string source,string dest)
        {
            Directory.CreateDirectory(dest);
            foreach(string file in Directory.GetFiles(source))
                File.Copy(file,Path.Combine(dest,Path.GetFileName(file)),true);
            foreach(string folder in Directory.GetDirectories(source))
            {
                if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("В библиотеке обнаружена ссылка.");
                CopyTree(folder,Path.Combine(dest,Path.GetFileName(folder)));
            }
        }
        public static void DeleteOwned(string path,string parent)
        {
            string full=Path.GetFullPath(path);
            if(!full.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Папка находится вне библиотеки.");
            if(!Directory.Exists(full))return;
            if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка является ссылкой.");
            Directory.Delete(full,true);
        }
    }

    [DataContract] internal sealed class PackManifest
    {
        [DataMember(Name="header")] public PackHeader Header;
        [DataMember(Name="modules")] public PackModule[] Modules;
        [DataMember(Name="dependencies")] public PackDependency[] Dependencies;
    }
    [DataContract] internal sealed class PackHeader
    {
        [DataMember(Name="name")] public string Name;
        [DataMember(Name="uuid")] public string Id;
        [DataMember(Name="version")] public int[] Version;
    }
    [DataContract] internal sealed class PackModule
    {
        [DataMember(Name="type")] public string Type;
    }
    [DataContract] internal sealed class PackDependency
    {
        [DataMember(Name="uuid")] public string Id;
    }

    internal static class Library
    {
        public static Pack ImportPack(string archive)
        {
            string stage=Path.Combine(Store.Root,"Import-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                using(var zip=ZipFile.OpenRead(archive))
                {
                    if(zip.Entries.Count>30000 || zip.Entries.Sum(e=>e.Length)>2147483648L)
                        throw new InvalidDataException("Набор ресурсов слишком большой.");
                    var manifests=zip.Entries.Where(e=>e.FullName.EndsWith("manifest.json",StringComparison.OrdinalIgnoreCase) &&
                        (e.FullName=="manifest.json" || e.FullName.Count(c=>c=='/')==1)).ToArray();
                    if(manifests.Length!=1)throw new InvalidDataException("Нужен один ресурспак с manifest.json.");
                    string prefix=manifests[0].FullName.Substring(0,manifests[0].FullName.Length-"manifest.json".Length);
                    foreach(var entry in zip.Entries.Where(e=>e.FullName.StartsWith(prefix,StringComparison.Ordinal)))
                    {
                        string relative=entry.FullName.Substring(prefix.Length);
                        if(relative.Length==0)continue;
                        string path=Store.Child(stage,relative);
                        if(entry.FullName.EndsWith("/")) {Directory.CreateDirectory(path);continue;}
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        entry.ExtractToFile(path);
                    }
                }
                var manifest=Store.Read<PackManifest>(Path.Combine(stage,"manifest.json"));
                Guid id;
                if(manifest.Header==null || !Guid.TryParse(manifest.Header.Id,out id) ||
                    manifest.Header.Version==null || manifest.Header.Version.Length!=3 || manifest.Header.Version.Any(v=>v<0) ||
                    manifest.Modules==null || !manifest.Modules.Any(m=>m.Type=="resources") ||
                    manifest.Modules.Any(m=>m.Type!="resources"))
                    throw new InvalidDataException("Этот файл не является самостоятельным набором ресурсов Bedrock.");
                string canonical=id.ToString();
                if(Store.State.Packs.Any(p=>p.Id==canonical))throw new IOException("Этот ресурспак уже есть в библиотеке.");
                string title=manifest.Header.Name;
                if(String.IsNullOrWhiteSpace(title))title=Path.GetFileNameWithoutExtension(archive);
                if(title=="pack.name" || title=="pack_name")
                {
                    string lang=Path.Combine(stage,"texts","ru_RU.lang");
                    if(!File.Exists(lang))lang=Path.Combine(stage,"texts","en_US.lang");
                    if(File.Exists(lang))
                    {
                        string translated=File.ReadLines(lang).FirstOrDefault(l=>l.StartsWith(title+"=",StringComparison.Ordinal));
                        if(translated!=null)title=translated.Substring(title.Length+1);
                    }
                }
                var pack=new Pack {Id=canonical,Name=title,Version=manifest.Header.Version,
                    Dependencies=(manifest.Dependencies??new PackDependency[0]).Where(d=>!String.IsNullOrWhiteSpace(d.Id)).Select(d=>d.Id).ToArray()};
                Directory.Move(stage,pack.Folder);
                Store.State.Packs.Add(pack);
                try { Store.Save(); }
                catch { Store.State.Packs.Remove(pack);Store.DeleteOwned(pack.Folder,Path.Combine(Store.Root,"Packs"));throw; }
                return pack;
            }
            finally { if(Directory.Exists(stage))Store.DeleteOwned(stage,Store.Root); }
        }

        public static Skin ImportSkin(string file,bool slim)
        {
            using(var image=Image.FromFile(file))
                if((image.Width!=64 || (image.Height!=64 && image.Height!=32)) &&
                   (image.Width!=128 || image.Height!=128))
                    throw new InvalidDataException("Нужен PNG-скин 64×64, 64×32 или 128×128.");
            var skin=new Skin {Id=Guid.NewGuid().ToString(),Name=Path.GetFileNameWithoutExtension(file),Slim=slim};
            using(var image=Image.FromFile(file)) image.Save(skin.FilePath,System.Drawing.Imaging.ImageFormat.Png);
            Store.State.Skins.Add(skin);
            try {Store.Save();}
            catch {Store.State.Skins.Remove(skin);File.Delete(skin.FilePath);throw;}
            return skin;
        }

        public static IEnumerable<string> DataRoots()
        {
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string packages=Path.Combine(local,"Packages");
            if(Directory.Exists(packages))
                foreach(string dir in Directory.GetDirectories(packages,"*Minecraft*"))
                {
                    string data=Path.Combine(dir,"LocalState","games","com.mojang");
                    if(Directory.Exists(data))roots.Add(data);
                }
            string users=Path.Combine(roaming,"Minecraft Bedrock","Users");
            if(Directory.Exists(users))
                foreach(string user in Directory.GetDirectories(users))
                {
                    string data=Path.Combine(user,"games","com.mojang");
                    if(Directory.Exists(data))roots.Add(data);
                }
            string shared=Path.Combine(roaming,"Minecraft Bedrock","Users","Shared","games","com.mojang");
            if(Directory.Exists(Path.Combine(roaming,"Minecraft Bedrock")))roots.Add(shared);
            foreach(var install in Store.State.Installations)
            {
                string portable=Path.Combine(install.Folder,"games","com.mojang");
                if(Directory.Exists(portable))roots.Add(portable);
            }
            return roots;
        }
        public static string SetOption(string text,string key,string value)
        {
            string newline=text.Contains("\r\n") ? "\r\n" : "\n";
            var lines=text.Replace("\r\n","\n").Split('\n').ToList();
            bool found=false;
            for(int i=0;i<lines.Count;i++)
                if(lines[i].StartsWith(key+":",StringComparison.Ordinal))
                {lines[i]=key+":"+value;found=true;}
            if(!found) {if(lines.Count>0 && lines[lines.Count-1]=="")lines.RemoveAt(lines.Count-1);lines.Add(key+":"+value);}
            return String.Join(newline,lines);
        }

        public static int Sync()
        {
            foreach(var pack in Store.State.Packs.Where(p=>p.Active))
                foreach(string required in pack.Dependencies??new string[0])
                    if(!Store.State.Packs.Any(p=>p.Active&&String.Equals(p.Id,required,StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Для набора «"+pack.Name+"» включи зависимый ресурспак "+required+".");
            var roots=DataRoots().ToList();
            var prior=new Dictionary<string,byte[]>();
            foreach(string data in roots)
            {
                foreach(string path in new[]{Path.Combine(data,"global_resource_packs.json"),Path.Combine(data,"minecraftpe","options.txt")})
                    prior[path]=File.Exists(path)?File.ReadAllBytes(path):null;
            }
            try{foreach(string data in roots)SyncTo(data);return roots.Count;}
            catch(Exception error)
            {
                var failed=new List<string>();
                foreach(var original in prior)
                    try
                    {
                        if(original.Value!=null)Store.Atomic(original.Key,original.Value);
                        else if(File.Exists(original.Key))File.Delete(original.Key);
                    }
                    catch(IOException){failed.Add(original.Key);}
                    catch(UnauthorizedAccessException){failed.Add(original.Key);}
                if(failed.Count>0)throw new IOException("Не удалось восстановить настройки профилей: "+String.Join(", ",failed),error);
                throw;
            }
        }
        internal static void SyncTo(string data)
        {
            var packIds=new HashSet<string>(Store.State.Packs.Select(p=>p.Id),StringComparer.OrdinalIgnoreCase);
            string global=Path.Combine(data,"global_resource_packs.json");
            var active=File.Exists(global) ? Store.Read<List<ActivePack>>(global) : new List<ActivePack>();
            if(active==null)active=new List<ActivePack>();
            active=active.Where(p=>!packIds.Contains(p.Id)).ToList();
            foreach(var pack in Store.State.Packs.Where(p=>p.Active))
            {
                string installed=Path.Combine(data,"resource_packs","collapse_"+pack.Id);
                Store.CopyTree(pack.Folder,installed);
            }
            active.InsertRange(0,Store.State.Packs.Where(p=>p.Active).Select(p=>new ActivePack {Id=p.Id,Version=p.Version}));
            Store.Atomic(global,Store.Json(active));

            var skin=Store.State.Skins.FirstOrDefault(s=>s.Id==Store.State.SelectedSkin);
            if(skin!=null)
            {
                string folder=Path.Combine(data,"skin_packs","collapse_"+skin.Id);
                Directory.CreateDirectory(folder);
                File.Copy(skin.FilePath,Path.Combine(folder,"skin.png"),true);
                var header=new SkinManifest {Header=new SkinPackHeader {Id=skin.Id,Name="Collapse · "+skin.Name},
                    Modules=new[]{new SkinModule {Id=StableModule(skin.Id)}}};
                Store.Atomic(Path.Combine(folder,"manifest.json"),Store.Json(header));
                var skins=new SkinPack {Serialize="collapse_"+skin.Id.Replace("-",""),
                    Skins=new[]{new SkinDefinition {Geometry=skin.Slim ? "geometry.humanoid.customSlim" : "geometry.humanoid.custom"}}};
                Store.Atomic(Path.Combine(folder,"skins.json"),Store.Json(skins));
                Directory.CreateDirectory(Path.Combine(folder,"texts"));
                Store.Atomic(Path.Combine(folder,"texts","en_US.lang"),Encoding.UTF8.GetBytes(
                    "skinpack."+skins.Serialize+"="+skin.Name+"\nskin."+skins.Serialize+".selected="+skin.Name+"\n"));
            }
            string options=Path.Combine(data,"minecraftpe","options.txt");
            if(File.Exists(options))
            {
                string text=File.ReadAllText(options);
                text=SetOption(text,"gfx_vsync",Store.State.DisableVsync?"0":"1");
                if(Store.State.DisableVsync)text=SetOption(text,"gfx_max_framerate","0");
                if(skin!=null)
                {
                    text=SetOption(text,"game_skintypefull",skin.Id+"_selected");
                    text=SetOption(text,"game_lastcustomskinnew",skin.Id+"_selected");
                }
                else
                {
                    // Remove only this launcher's selection; leave other game options intact.
                    var ours=new HashSet<string>(Store.State.Skins.Select(s=>s.Id+"_selected"),StringComparer.OrdinalIgnoreCase);
                    string newline=text.Contains("\r\n")?"\r\n":"\n";
                    text=String.Join(newline,text.Replace("\r\n","\n").Split('\n').Where(line=>{
                        int colon=line.IndexOf(':');if(colon<0)return true;
                        string key=line.Substring(0,colon);
                        return (key!="game_skintypefull"&&key!="game_lastcustomskinnew")||!ours.Contains(line.Substring(colon+1));
                    }));
                }
                Store.Atomic(options,Encoding.UTF8.GetBytes(text));
            }
        }
        public static void RemoveCopies(Pack pack)
        {
            foreach(string data in DataRoots())
                Store.DeleteOwned(Path.Combine(data,"resource_packs","collapse_"+pack.Id),Path.Combine(data,"resource_packs"));
        }
        static string StableModule(string id)
        {
            byte[] bytes=new Guid(id).ToByteArray();bytes[15]^=0x71;return new Guid(bytes).ToString();
        }
    }
    [DataContract] internal sealed class SkinManifest
    {
        [DataMember(Name="format_version")] public int Format=1;
        [DataMember(Name="header")] public SkinPackHeader Header;
        [DataMember(Name="modules")] public SkinModule[] Modules;
    }
    [DataContract] internal sealed class SkinPackHeader
    {
        [DataMember(Name="name")] public string Name;
        [DataMember(Name="uuid")] public string Id;
        [DataMember(Name="version")] public int[] Version=new[]{1,0,0};
    }
    [DataContract] internal sealed class SkinModule
    {
        [DataMember(Name="type")] public string Type="skin_pack";
        [DataMember(Name="uuid")] public string Id;
        [DataMember(Name="version")] public int[] Version=new[]{1,0,0};
    }
    [DataContract] internal sealed class SkinPack
    {
        [DataMember(Name="serialize_name")] public string Serialize;
        [DataMember(Name="localization_name")] public string Localization="collapse";
        [DataMember(Name="skins")] public SkinDefinition[] Skins;
    }
    [DataContract] internal sealed class SkinDefinition
    {
        [DataMember(Name="localization_name")] public string Name="selected";
        [DataMember(Name="geometry")] public string Geometry;
        [DataMember(Name="texture")] public string Texture="skin.png";
        [DataMember(Name="type")] public string Type="free";
    }
}
