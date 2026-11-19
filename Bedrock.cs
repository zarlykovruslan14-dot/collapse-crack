using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using System.Windows.Forms;
using System.Xml.Linq;

static class BedrockVersions
{
    internal static readonly Version LegacyLimit = new Version(1,21,114);
    static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Collapse Crack");
    static readonly string[] UpdateHosts = {"dl.delivery.mp.microsoft.com","tlu.dl.delivery.mp.microsoft.com"};
    static readonly HttpClient Http = new HttpClient(new HttpClientHandler {AllowAutoRedirect=false})
        {Timeout=TimeSpan.FromMinutes(15)};

    internal sealed class Entry
    {
        public Version Version;
        public Guid UpdateId;
        public int Channel;
        public override string ToString()
        {
            return Version + (Channel == 0 ? "   ·   Релиз" : Channel == 1 ? "   ·   Бета" : "   ·   Preview");
        }
    }

    internal static string DisplayVersion(string value)
    {
        Version version;
        if (Version.TryParse(value, out version) && version.Build >= 200 && version.Revision == 0)
            return new Version(version.Major,version.Minor,version.Build/100,version.Build%100).ToString();
        return value;
    }

    internal static bool IsLegacy(string value)
    {
        Version v;
        return Version.TryParse(value,out v) && new Version(v.Major,v.Minor,Math.Max(0,v.Build)) < LegacyLimit;
    }

    internal static List<Entry> Catalog()
    {
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("versions.csv"))
        using(var reader=new StreamReader(stream))
        {
            var entries = new List<Entry>();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split(',');
                var version = Version.Parse(fields[0]);
                    entries.Add(new Entry { Version=version, UpdateId=Guid.Parse(fields[1]),
                        Channel=Int32.Parse(fields[2]) });
            }
            return entries.OrderByDescending(entry => entry.Version).ToList();
        }
    }

    static XDocument Request(Guid id)
    {
        const string endpoint="https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured";
        XNamespace service="http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";
        XNamespace soap="http://www.w3.org/2003/05/soap-envelope";
        XNamespace address="http://www.w3.org/2005/08/addressing";
        XNamespace security="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace utility="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        XNamespace auth="http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization";
        DateTime now=DateTime.UtcNow;
        var tickets=new XElement(auth+"WindowsUpdateTicketsToken",new XAttribute(utility+"id","ClientMSA"),
            new XElement("TicketType",new XAttribute("Name","AAD"),new XAttribute("Version","1.0"),new XAttribute("Policy","MBI_SSL")));
        return new XDocument(new XElement(soap+"Envelope",
            new XElement(soap+"Header",
                new XElement(address+"Action",new XAttribute(soap+"mustUnderstand","1"),service.NamespaceName+"/GetExtendedUpdateInfo2"),
                new XElement(address+"MessageID","urn:uuid:"+Guid.NewGuid()),
                new XElement(address+"To",new XAttribute(soap+"mustUnderstand","1"),endpoint),
                new XElement(security+"Security",new XAttribute(soap+"mustUnderstand","1"),
                    new XElement(utility+"Timestamp",new XElement(utility+"Created",now.ToString("o")),
                        new XElement(utility+"Expires",now.AddMinutes(5).ToString("o"))),tickets)),
            new XElement(soap+"Body",new XElement(service+"GetExtendedUpdateInfo2",
                new XElement(service+"updateIDs",new XElement(service+"UpdateIdentity",
                    new XElement(service+"UpdateID",id.ToString()),new XElement(service+"RevisionNumber","1"))),
                new XElement(service+"infoTypes",new XElement(service+"XmlUpdateFragmentType","FileUrl")),
                new XElement(service+"deviceAttributes",
                    "E:BranchReadinessLevel=CBB&CurrentBranch=rs4_release&FlightRing=Retail&InstallationType=Client&" +
                    "OSSkuId=48&App=WU&OSArchitecture=AMD64&OSVersion=10.0.19045.0&DeviceFamily=Windows.Desktop")))));
    }

    internal static IEnumerable<Uri> PackageLinks(XDocument response)
    {
        foreach(var location in response.Descendants().Where(x=>x.Name.LocalName=="FileLocation"))
        {
            var url=location.Elements().FirstOrDefault(x=>x.Name.LocalName=="Url");
            Uri uri;
            if(url!=null && Uri.TryCreate(url.Value,UriKind.Absolute,out uri) &&
                (uri.Scheme==Uri.UriSchemeHttps || uri.Scheme==Uri.UriSchemeHttp) && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && UpdateHosts.Contains(uri.Host,StringComparer.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/filestreamingservice/files/",StringComparison.Ordinal))
                yield return uri;
        }
    }

    internal static async Task<string> Download(Entry item, IProgress<string> progress)
    {
        string downloads=Path.Combine(AppFolder,"Downloads");
        Directory.CreateDirectory(downloads);
        string dest=Path.Combine(downloads,"Minecraft-"+item.Version+"-"+item.UpdateId.ToString("N").Substring(0,8)+".appx");

        progress.Report("Получаем ссылку Microsoft для " + item.Version + "…");
        XDocument result;
        using(var body=new StringContent(Request(item.UpdateId).ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8,"application/soap+xml"))
        using(var response=await Http.PostAsync("https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured",body).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            result=XDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        Uri[] links=PackageLinks(result).Distinct().ToArray();
        if(links.Length==0)
            throw new IOException("Microsoft не выдала ссылку для этой версии. Бета и Preview могут требовать доступ через аккаунт.");
        if(File.Exists(dest))
            foreach(Uri link in links)
                try{VerifyDigest(dest,result,link);VerifyPackage(dest,item);return dest;}
                catch(IOException){}
        string partial=dest+"."+Guid.NewGuid().ToString("N")+".part";
        Exception last=null;
        try
        {
            foreach(Uri link in links)
            {
                try
                {
                    using(var response=await Http.GetAsync(link,HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        long? total=response.Content.Headers.ContentLength;
                        if(total.HasValue && total.Value<1000000) continue;
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,131072,true))
                        {
                            var bytes=new byte[131072];
                            long read=0;
                            var timer=Stopwatch.StartNew();
                            int n;
                            while((n=await input.ReadAsync(bytes,0,bytes.Length).ConfigureAwait(false))>0)
                            {
                                await output.WriteAsync(bytes,0,n).ConfigureAwait(false);
                                read+=n;
                                if(timer.ElapsedMilliseconds<250)continue;
                                progress.Report(String.Format(total>0 ? "Скачивание {0}… {2:F0}%" : "Скачивание {0}… {1:F0} МБ",
                                    item.Version,read/1048576.0,total>0 ? read*100.0/total.Value : 0));
                                timer.Restart();
                            }
                            if(total.HasValue && total.Value!=read)throw new IOException("Загрузка оборвалась.");
                        }
                    }
                    VerifyDigest(partial,result,link);
                    VerifyPackage(partial,item);
                    if(File.Exists(dest))File.Replace(partial,dest,null);
                    else File.Move(partial,dest);
                    return dest;
                }
                catch(IOException error){last=error;}
                catch(HttpRequestException error){last=error;}
            }
            throw new IOException("Не удалось получить пакет выбранной версии с CDN Microsoft.",last);
        }
        finally { if(File.Exists(partial))File.Delete(partial); }
    }

    internal static void VerifyDigest(string path,XDocument response,Uri link)
    {
        var location=response.Descendants().FirstOrDefault(e=>e.Name.LocalName=="FileLocation" &&
            e.Elements().Any(child=>child.Name.LocalName=="Url" && new Uri(child.Value)==link));
        var value=location==null?null:location.Elements().FirstOrDefault(e=>e.Name.LocalName=="FileDigest");
        if(value==null)throw new IOException("Microsoft не вернула контрольную сумму пакета.");
        byte[] expected;
        try{expected=Convert.FromBase64String(value.Value);}
        catch(FormatException){throw new IOException("Неверная контрольная сумма в ответе Microsoft.");}
        using(var hash=expected.Length==20 ? (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA1.Create() :
            expected.Length==32 ? System.Security.Cryptography.SHA256.Create() : null)
        {
            if(hash==null)throw new IOException("Неизвестный алгоритм проверки пакета.");
            using(var file=File.OpenRead(path))
                if(!hash.ComputeHash(file).SequenceEqual(expected))throw new IOException("Контрольная сумма пакета не совпала.");
        }
    }

    internal static void VerifyPackage(string path,Entry item)
    {
        using(var archive=ZipFile.OpenRead(path))
        {
            var entry=archive.GetEntry("AppxManifest.xml");
            if(entry==null || entry.Length>1048576 || archive.GetEntry("AppxSignature.p7x")==null)
                throw new IOException("Сервер вернул файл, отличный от подписанного пакета APPX.");
            using(var stream=entry.Open())
            {
                var identity=XElement.Load(stream).Elements().FirstOrDefault(x=>x.Name.LocalName=="Identity");
                string name=identity==null?null:(string)identity.Attribute("Name");
                string version=identity==null?null:(string)identity.Attribute("Version");
                if((name!="Microsoft.MinecraftUWP" && name!="Microsoft.MinecraftWindowsBeta" && name!="Microsoft.MinecraftUWPBeta") ||
                    version==null || (version!=item.Version.ToString() && DisplayVersion(version)!=item.Version.ToString()))
                    throw new IOException("В пакете другая игра или версия.");
            }
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        void ActivateApplication(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appUserModelId,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string arguments,
            uint options, out uint processId);
    }

    internal static bool UsePackageLaunch(string folder)
    {
        if (!File.Exists(Path.Combine(folder,"AppxManifest.xml"))) return false;
        if (!File.Exists(Path.Combine(folder,"MicrosoftGame.config"))) return true;
        string root = Path.GetPathRoot(folder);
        string xbox = Path.Combine(root,"XboxGames") + Path.DirectorySeparatorChar;
        string store = Path.Combine(root,"Program Files","WindowsApps") + Path.DirectorySeparatorChar;
        return folder.StartsWith(xbox,StringComparison.OrdinalIgnoreCase) ||
            folder.StartsWith(store,StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<int> Launch(string folder, IProgress<string> progress)
    {
        string exe = Collapse.Client(folder);
        if (exe == null) throw new IOException("В выбранной папке больше нет Minecraft.");
        if (!UsePackageLaunch(folder))
        {
            progress.Report("Запускаем игру из выбранной папки…");
            string helper = Path.Combine(folder,"gamelaunchhelper.exe");
            helper = File.Exists(helper) ? helper : exe;
            using (var child = Process.Start(new ProcessStartInfo(helper) { WorkingDirectory=folder, UseShellExecute=false }))
            {
                await Task.Delay(1600);
                if (child.HasExited)
                    throw new IOException("Игра завершилась сразу после запуска. Код: 0x" + child.ExitCode.ToString("X8") + ".");
                return child.Id;
            }
        }

        progress.Report("Проверяем и регистрируем выбранную версию в Windows…");
        string appId = await ResolveApplicationId(folder);
        progress.Report("Открываем Minecraft…");
        var activationType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
        var activation = (IApplicationActivationManager)Activator.CreateInstance(activationType);
        uint pid;
        try { activation.ActivateApplication(appId, null, 0, out pid); }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(activation); }
        if (pid == 0) throw new IOException("Windows не вернула процесс Minecraft.");
        return (int)pid;
    }

    internal const string RegisterScript = @"
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$manifest=$env:COLLAPSE_MANIFEST
[xml]$xml=[IO.File]::ReadAllText($manifest)
$identity=$xml.Package.Identity.Name
$appid=($xml.Package.Applications.Application | Where-Object { $_.Executable -match 'Minecraft' } | Select-Object -First 1).Id
if(-not $appid -or $identity -notin @('Microsoft.MinecraftUWP','Microsoft.MinecraftWindowsBeta','Microsoft.MinecraftUWPBeta')) {
    throw 'Пакет Minecraft не распознан.'
}
$folder=[IO.Path]::GetDirectoryName($manifest)
function Find-SelectedPackage {
    Get-AppxPackage -Name $identity | Where-Object {
        $_.InstallLocation -and $_.InstallLocation.TrimEnd('\') -ieq $folder.TrimEnd('\')
    } | Select-Object -First 1
}
$package=Find-SelectedPackage
if(-not $package) {
    try {
        Add-AppxPackage -Register -Path $manifest -ForceUpdateFromAnyVersion
    } catch {
        $previous=Get-AppxPackage -Name $identity | Select-Object -First 1
        if(-not $previous -or -not $previous.IsDevelopmentMode){throw}
        $previousManifest=[IO.Path]::Combine($previous.InstallLocation,'AppxManifest.xml')
        Remove-AppxPackage -Package $previous.PackageFullName -PreserveApplicationData
        try {
            Add-AppxPackage -Register -Path $manifest -ForceUpdateFromAnyVersion
        } catch {
            $deploymentError=$_
            if([IO.File]::Exists($previousManifest)){
                Add-AppxPackage -Register -Path $previousManifest -ForceUpdateFromAnyVersion
            }
            throw $deploymentError
        }
    }
    $package=Find-SelectedPackage
}
if(-not $package){ throw 'Windows не зарегистрировала выбранную папку Minecraft.' }
[Console]::WriteLine('COLLAPSE_APPID=' + $package.PackageFamilyName + '!' + $appid)
";

    static async Task<string> ResolveApplicationId(string folder)
    {
        string powershell = Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        var start = new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(RegisterScript)));
        start.EnvironmentVariables["COLLAPSE_MANIFEST"] = Path.Combine(folder,"AppxManifest.xml");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.StandardOutputEncoding = Encoding.UTF8;
        using (var process = Process.Start(start))
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => process.WaitForExit(90000)).ConfigureAwait(false);
            if (!exited)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                throw new TimeoutException("Windows не завершила регистрацию за 90 секунд.");
            }
            string error = (await errorTask.ConfigureAwait(false)).Trim();
            string output = await outputTask.ConfigureAwait(false);
            const string marker = "COLLAPSE_APPID=";
            string app = output.Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.StartsWith(marker,StringComparison.Ordinal));
            if (process.ExitCode != 0 || app == null)
                throw new IOException(error.Length == 0 ? "Не удалось зарегистрировать выбранную версию." : error);
            return app.Substring(marker.Length).Trim();
        }
    }
}

