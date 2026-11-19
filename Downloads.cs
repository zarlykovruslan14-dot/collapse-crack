using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CollapseLauncher
{
    internal sealed class ReleaseItem
    {
        public string Version;
        public string Url;
        public BedrockVersions.Entry Uwp;
        public bool Gdk {get{return Uwp==null;}}
        public override string ToString(){return Version;}
    }
    internal static class Downloads
    {
        static readonly HttpClient Http=new HttpClient {Timeout=TimeSpan.FromMinutes(45)};
        public static async Task<List<ReleaseItem>> Catalog()
        {
            var result=BedrockVersions.Catalog().Where(v=>v.Channel==0)
                .Select(v=>new ReleaseItem {Version=v.Version.ToString(),Uwp=v}).ToList();
            string cache=Path.Combine(Store.Root,"gdk-catalog.json");
            byte[] data=null;
            try
            {
                using(var client=new HttpClient {Timeout=TimeSpan.FromSeconds(15)})
                    data=await client.GetByteArrayAsync("https://raw.githubusercontent.com/MinecraftBedrockArchiver/GdkLinks/master/urls.min.json");
                Store.Atomic(cache,data);
            }
            catch(HttpRequestException) {if(File.Exists(cache))data=File.ReadAllBytes(cache);}
            catch(TaskCanceledException) {if(File.Exists(cache))data=File.ReadAllBytes(cache);}
            if(data==null)
                using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("gdk.json"))
                using(var buffer=new MemoryStream()){stream.CopyTo(buffer);data=buffer.ToArray();}
            using(var buffer=new MemoryStream(data))
            {
                var parsed=(Dictionary<string,Dictionary<string,string[]>>)new DataContractJsonSerializer(
                    typeof(Dictionary<string,Dictionary<string,string[]>>),
                    new DataContractJsonSerializerSettings{UseSimpleDictionaryFormat=true}).ReadObject(buffer);
                foreach(var pair in parsed["release"])
                {
                    Version version;
                    if(!System.Version.TryParse(pair.Key,out version))continue;
                    var url=pair.Value.FirstOrDefault(ValidGameUrl);
                    if(url!=null)result.Add(new ReleaseItem {Version=pair.Key,Url=url});
                }
            }
            return result.OrderByDescending(v=>System.Version.Parse(v.Version)).ToList();
        }
        static bool ValidGameUrl(string value)
        {
            Uri u;
            return Uri.TryCreate(value,UriKind.Absolute,out u) &&
                (u.Scheme=="http"||u.Scheme=="https") && u.IsDefaultPort && u.UserInfo=="" &&
                (u.Host=="assets1.xboxlive.com"||u.Host=="assets2.xboxlive.com") &&
                u.AbsolutePath.EndsWith(".msixvc",StringComparison.OrdinalIgnoreCase);
        }
        public static async Task<string> Install(ReleaseItem item,IProgress<string> progress)
        {
            string library=Path.Combine(Store.Root,"Versions");
            Directory.CreateDirectory(library);
            string dest=Path.Combine(library,"Minecraft-"+item.Version);
            if(Collapse.Client(dest)!=null)return dest;
            string stage=dest+"-"+Guid.NewGuid().ToString("N")+".partial";
            Directory.CreateDirectory(stage);
            try
            {
                if(item.Uwp!=null)
                {
                    string package=await BedrockVersions.Download(item.Uwp,progress);
                    progress.Report("Распаковка "+item.Version+"…");
                    await Task.Run(()=>{
                        using(var zip=ZipFile.OpenRead(package))
                            foreach(var e in zip.Entries)
                            {
                                if(e.FullName=="AppxSignature.p7x")continue;
                                string path=Store.Child(stage,e.FullName);
                                if(e.FullName.EndsWith("/")){Directory.CreateDirectory(path);continue;}
                                Directory.CreateDirectory(Path.GetDirectoryName(path));
                                e.ExtractToFile(path);
                            }
                    });
                }
                else
                {
                    string package=Path.Combine(Store.Root,"Downloads","Minecraft-"+item.Version+".msixvc");
                    Directory.CreateDirectory(Path.GetDirectoryName(package));
                    if(!File.Exists(package)) await Fetch(item.Url,package,progress);
                    progress.Report("Подготовка пакета службами Xbox…");
                    string source=await StageGdk(package,item.Version);
                    progress.Report("Перенос "+item.Version+" в библиотеку…");
                    await Task.Run(()=>Store.CopyTree(source,stage));
                }
                if(Collapse.Client(stage)==null)throw new IOException("В загруженной сборке не найден Minecraft.Windows.exe.");
                string actual=Collapse.Version(Collapse.Client(stage));
                if(actual!=item.Version)throw new IOException("Загруженная сборка содержит другую версию: "+actual);
                if(Directory.Exists(dest))
                {
                    if(Collapse.Client(dest)!=null)return dest;
                    throw new IOException("Папка версии уже существует. Выбери её через «Добавить папку».");
                }
                Directory.Move(stage,dest);
                return dest;
            }
            finally {if(Directory.Exists(stage))Store.DeleteOwned(stage,library);}
        }
        static async Task Fetch(string url,string target,IProgress<string> progress)
        {
            string part=target+"."+Guid.NewGuid().ToString("N")+".part";
            try
            {
                using(var response=await Http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long total=response.Content.Headers.ContentLength??0,current=0;
                    using(var input=await response.Content.ReadAsStreamAsync())
                    using(var output=new FileStream(part,FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
                    {
                        byte[] buf=new byte[131072];
                        int n;var watch=Stopwatch.StartNew();
                        while((n=await input.ReadAsync(buf,0,buf.Length))>0)
                        {
                            await output.WriteAsync(buf,0,n);current+=n;
                            if(watch.ElapsedMilliseconds>200)
                            {
                                progress.Report(total>0 ? "Загрузка "+(current*100/total)+"%  ·  "+(current/1048576)+" МБ" :
                                    "Загрузка "+current/1048576+" МБ");
                                watch.Restart();
                            }
                        }
                    }
                    if(total>0 && current!=total)throw new IOException("Загрузка оборвалась.");
                    if(current<1000000)throw new IOException("Сервер вернул неполный пакет.");
                }
                if(!File.Exists(target))File.Move(part,target);
            }
            finally {if(File.Exists(part))File.Delete(part);}
        }

        static async Task<string> StageGdk(string file,string version)
        {
            // Stage uses Microsoft's installed content keys. It does not remove existing packages.
            string script=@"
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Add-AppxPackage -Stage -Path $env:COLLAPSE_PACKAGE -ForceUpdateFromAnyVersion
$null=[Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]
$manager=New-Object Windows.Management.Deployment.PackageManager
$packages=$manager.FindPackages('Microsoft.MinecraftUWP_8wekyb3d8bbwe')
$match=$packages | Where-Object { $_.Id.Name -ieq 'Microsoft.MinecraftUWP' -and $_.InstalledLocation.Path } | Where-Object {
    $v=$_.Id.Version
    $display=('{0}.{1}.{2}.{3}' -f $v.Major,$v.Minor,[math]::Floor($v.Build/100),($v.Build%100))
    $display -eq $env:COLLAPSE_VERSION
} | Select-Object -First 1
if(-not $match){throw 'Windows не выдала папку подготовленной версии. Установи Minecraft for Windows через Xbox / Microsoft Store один раз и повтори.'}
[Console]::WriteLine('COLLAPSE_PATH='+$match.InstalledLocation.Path)
";
            var result=await Shell(script,new Dictionary<string,string>{{"COLLAPSE_PACKAGE",file},{"COLLAPSE_VERSION",version}});
            string line=result.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l=>l.StartsWith("COLLAPSE_PATH="));
            if(line==null)throw new IOException("Службы Xbox не вернули расположение пакета.");
            string source=line.Substring("COLLAPSE_PATH=".Length);
            if(Collapse.Client(source)==null)throw new IOException("Установленный пакет Xbox недоступен для чтения.");
            return source;
        }

        internal static async Task<string> Shell(string script,Dictionary<string,string> environment)
        {
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"),
                "-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.UseShellExecute=false;start.CreateNoWindow=true;
            start.RedirectStandardError=true;start.RedirectStandardOutput=true;
            start.StandardErrorEncoding=Encoding.UTF8;start.StandardOutputEncoding=Encoding.UTF8;
            foreach(var pair in environment)start.EnvironmentVariables[pair.Key]=pair.Value;
            using(var child=Process.Start(start))
            {
                var output=child.StandardOutput.ReadToEndAsync();
                var errors=child.StandardError.ReadToEndAsync();
                bool exited=await Task.Run(()=>child.WaitForExit(600000));
                if(!exited){try{child.Kill();}catch(InvalidOperationException){}throw new TimeoutException("Windows не завершила установку пакета.");}
                string result=await output,error=await errors;
                if(child.ExitCode!=0)throw new IOException(error.Trim());
                return result;
            }
        }
    }
}
