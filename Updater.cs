using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace CollapseLauncher
{
    internal static class Updater
    {
        public const string Repository="zarlykovruslan14-dot/collapse-crack";
        public static readonly Version Current=new Version(6,0,0,0);
        [DataContract] internal sealed class Release
        {
            [DataMember(Name="tag_name")] public string Tag;
            [DataMember(Name="assets")] public Asset[] Assets;
        }
        [DataContract] internal sealed class Asset
        {
            [DataMember(Name="browser_download_url")] public string Url;
            [DataMember(Name="name")] public string Name;
            [DataMember(Name="size")] public long Size;
            [DataMember(Name="digest")] public string Digest;
        }
        public static async Task<Release> Check()
        {
            using(var client=new HttpClient{Timeout=TimeSpan.FromSeconds(12)})
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CollapseLauncher/6.0.0");
                using(var response=await client.GetAsync("https://api.github.com/repos/"+Repository+"/releases/latest"))
                {
                    response.EnsureSuccessStatusCode();
                    using(var stream=await response.Content.ReadAsStreamAsync())
                    {
                        var release=(Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(stream);
                        Version v;
                        if(!Version.TryParse((release.Tag??"").TrimStart('v','V'),out v))return null;
                        v=new Version(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
                        return v>Current ? release : null;
                    }
                }
            }
        }
        public static async Task<string> Download(Release release,IProgress<string> progress)
        {
            var asset=(release.Assets??new Asset[0]).Where(a=>a.Name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a=>a.Name.StartsWith("Collapse.Launcher",StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            Uri uri;
            if(asset==null || !Uri.TryCreate(asset.Url,UriKind.Absolute,out uri) || uri.Scheme!="https" || uri.Host!="github.com" ||
                uri.UserInfo!="" || !uri.IsDefaultPort || !uri.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.OrdinalIgnoreCase) ||
                asset.Digest==null || !System.Text.RegularExpressions.Regex.IsMatch(asset.Digest,@"Asha256:[a-fA-F0-9]{64}z"))
                throw new IOException("В релизе нет EXE с контрольной суммой SHA-256.");
            string folder=Path.Combine(Store.Root,"Updates",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string target=Path.Combine(folder,"next.exe");
            using(var client=new HttpClient{Timeout=TimeSpan.FromMinutes(15)})
            using(var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using(var input=await response.Content.ReadAsStreamAsync())
                using(var output=File.Create(target))
                {
                    byte[] buffer=new byte[131072];long total=0;int n;
                    while((n=await input.ReadAsync(buffer,0,buffer.Length))>0)
                    {
                        await output.WriteAsync(buffer,0,n);total+=n;
                        progress.Report("Обновление "+release.Tag+" · "+(asset.Size>0?total*100/asset.Size:0)+"%");
                    }
                    if(total!=asset.Size)throw new IOException("Обновление скачано не полностью.");
                }
            }
            if(!Hash(target).Equals(asset.Digest.Substring(7),StringComparison.OrdinalIgnoreCase))
                throw new IOException("Контрольная сумма обновления не совпала.");
            var version=AssemblyName.GetAssemblyName(target).Version;
            if(version<=Current)throw new IOException("Загруженный EXE содержит предыдущую версию.");
            return target;
        }
        static string Hash(string path)
        {
            using(var sha=SHA256.Create())
            using(var data=File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");
        }
        public static void StartReplacement(string next)
        {
            string current=Assembly.GetExecutingAssembly().Location;
            string helper=Path.Combine(Path.GetDirectoryName(next),"updater.exe");
            File.Copy(current,helper,true);
            using(var process=Process.GetCurrentProcess())
            {
                var start=new ProcessStartInfo(helper,"--replace") {UseShellExecute=false,CreateNoWindow=true};
                start.EnvironmentVariables["COLLAPSE_UPDATE_TARGET"]=current;
                start.EnvironmentVariables["COLLAPSE_UPDATE_SOURCE"]=next;
                start.EnvironmentVariables["COLLAPSE_UPDATE_PID"]=process.Id.ToString();
                start.EnvironmentVariables["COLLAPSE_UPDATE_START"]=process.StartTime.ToUniversalTime().Ticks.ToString();
                start.EnvironmentVariables["COLLAPSE_UPDATE_HASH"]=Hash(next);
                Process.Start(start);
            }
        }
        public static bool HandleReplacement(string[] args)
        {
            if(args.Length!=1 || args[0]!="--replace")return false;
            string target=Environment.GetEnvironmentVariable("COLLAPSE_UPDATE_TARGET");
            string source=Environment.GetEnvironmentVariable("COLLAPSE_UPDATE_SOURCE");
            string prior=null;bool replaced=false;
            try
            {
                string own=Assembly.GetExecutingAssembly().Location;
                if(!String.Equals(Path.GetDirectoryName(own),Path.GetDirectoryName(source),StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(own).StartsWith(Path.Combine(Store.Root,"Updates")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                    !target.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) ||
                    !Hash(source).Equals(Environment.GetEnvironmentVariable("COLLAPSE_UPDATE_HASH"),StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Неверные параметры обновления.");
                int pid=Int32.Parse(Environment.GetEnvironmentVariable("COLLAPSE_UPDATE_PID"));
                try
                {
                    using(var original=Process.GetProcessById(pid))
                    {
                        long ticks=Int64.Parse(Environment.GetEnvironmentVariable("COLLAPSE_UPDATE_START"));
                        if(original.StartTime.ToUniversalTime().Ticks!=ticks ||
                            !String.Equals(original.MainModule.FileName,target,StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Изменился процесс лаунчера.");
                        if(!original.WaitForExit(60000))throw new IOException("Лаунчер занят. Повтори обновление позже.");
                    }
                }
                catch(ArgumentException){}
                string staged=Path.Combine(Path.GetDirectoryName(target),".collapse-update-"+Guid.NewGuid().ToString("N")+".exe");
                prior=target+".previous-"+Guid.NewGuid().ToString("N");
                File.Copy(source,staged);
                File.Replace(staged,target,prior);
                replaced=true;
                var launch=new ProcessStartInfo(target){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(target)};
                foreach(string key in new[]{"COLLAPSE_UPDATE_TARGET","COLLAPSE_UPDATE_SOURCE","COLLAPSE_UPDATE_PID","COLLAPSE_UPDATE_START","COLLAPSE_UPDATE_HASH"})
                    launch.EnvironmentVariables.Remove(key);
                using(var child=Process.Start(launch))
                    if(child.WaitForExit(2500))throw new IOException("Новая версия завершилась при запуске.");
                File.Delete(prior);
            }
            catch(Exception e)
            {
                if(replaced && prior!=null && File.Exists(prior))
                    try{File.Replace(prior,target,null);}catch(IOException){}
                System.Windows.MessageBox.Show("Не удалось завершить обновление.\n"+e.Message,"Collapse");
            }
            return true;
        }
    }
}
