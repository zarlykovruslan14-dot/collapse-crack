using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path=System.IO.Path;
using Microsoft.Win32;

[assembly:AssemblyVersion("6.0.1.0")]
[assembly:AssemblyFileVersion("6.0.1.0")]

namespace CollapseLauncher
{
    internal static class Program
    {
        [STAThread] public static void Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.Tls12;
            if(Updater.HandleReplacement(args))return;
            var app=new Application();
            try
            {
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                    app.Resources=(ResourceDictionary)XamlReader.Load(stream);
                Store.Load();
                app.Run(new LauncherWindow());
            }
            catch(Exception e){MessageBox.Show(e.Message,"Collapse Launcher");}
        }
    }

    internal sealed class LauncherWindow : Window
    {
        readonly Grid content=new Grid();
        readonly TextBlock notice=new TextBlock();
        readonly TextBlock searchText=new TextBlock();
        readonly ProgressBar searchBar=new ProgressBar();
        readonly Dictionary<string,Button> navigation=new Dictionary<string,Button>();
        readonly ListBox installed=new ListBox(),available=new ListBox(),active=new ListBox(),skins=new ListBox();
        readonly ComboBox selector=new ComboBox();
        readonly StackPanel catalog=new StackPanel();
        readonly TextBox filter=new TextBox();
        readonly System.Windows.Threading.DispatcherTimer timer=new System.Windows.Threading.DispatcherTimer();
        readonly Image wallpaper=new Image {Stretch=Stretch.UniformToFill,Opacity=.86};
        BitmapSource defaultBackground;
        readonly Button play;
        string pageName;
        bool busy,searching,updating;
        Process game;
        List<ReleaseItem> releases=new List<ReleaseItem>();

        static Brush Ink {get{return Hex("#EFF8FB");}}
        static Brush Muted {get{return Hex("#94ADBD");}}
        static Brush Accent {get{return Hex("#50E2DB");}}
        static Brush Hex(string value){return (Brush)new BrushConverter().ConvertFromString(value);}
        static TextBlock Text(string value,double size=13,Brush color=null,bool bold=false)
        {
            return new TextBlock {Text=value,FontSize=size,Foreground=color??Ink,TextWrapping=TextWrapping.Wrap,
                FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal};
        }
        static Grid Rows(params double[] heights)
        {
            var g=new Grid();
            foreach(double h in heights)g.RowDefinitions.Add(new RowDefinition {Height=h<0?new GridLength(1,GridUnitType.Star):h==0?GridLength.Auto:new GridLength(h)});
            return g;
        }
        static Grid Cols(params double[] widths)
        {
            var g=new Grid();
            foreach(double w in widths)g.ColumnDefinitions.Add(new ColumnDefinition {Width=w<0?new GridLength(1,GridUnitType.Star):w==0?GridLength.Auto:new GridLength(w)});
            return g;
        }
        static void Put(Grid g,UIElement child,int row,int column=0)
        {Grid.SetRow(child,row);Grid.SetColumn(child,column);g.Children.Add(child);}
        static Button Button(string title,Action click,bool primary=false)
        {
            var b=new Button {Content=title};
            if(primary)b.Style=(Style)Application.Current.Resources["Primary"];
            b.Click+=(s,e)=>click();return b;
        }
        static Border Card(UIElement child,int padding=20)
        {
            return new Border {Child=child,Background=Hex("#EA142332"),BorderBrush=Hex("#375064"),BorderThickness=new Thickness(1),
                CornerRadius=new CornerRadius(18),Padding=new Thickness(padding)};
        }

        public LauncherWindow(bool live=true)
        {
            Title="Collapse Launcher";Width=1180;Height=780;MinWidth=1000;MinHeight=690;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;Background=Hex("#0B1119");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this,new System.Windows.Shell.WindowChrome {
                CaptionHeight=0,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false});
            var outer=Rows(47,-1,36);
            Content=new Border {Child=outer,BorderBrush=Hex("#314A60"),BorderThickness=new Thickness(1)};
            var title=Cols(-1,46,46,46);title.Background=Hex("#0E1722");
            var wordmark=Text("COLLAPSE  /  LAUNCHER",11,Muted,true);
            wordmark.VerticalAlignment=VerticalAlignment.Center;wordmark.Margin=new Thickness(23,0,0,0);
            Put(title,wordmark,0);
            title.MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2)ToggleMax();else if(e.ButtonState==MouseButtonState.Pressed)DragMove();};
            Put(title,Button("—",()=>WindowState=WindowState.Minimized),0,1);
            Put(title,Button("□",ToggleMax),0,2);Put(title,Button("×",Close),0,3);Put(outer,title,0);

            var body=Cols(193,-1);Put(outer,body,1);
            var sidebar=Rows(106,-1,103);sidebar.Background=Hex("#0F1B27");Put(body,sidebar,0);
            var brand=Cols(43,-1);brand.Margin=new Thickness(18,25,10,20);
            Put(brand,new Border {Background=Accent,CornerRadius=new CornerRadius(14),Height=42,
                Child=new TextBlock {Text="C",FontSize=25,FontWeight=FontWeights.Bold,Foreground=Hex("#092129"),
                    HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}},0);
            var labels=new StackPanel {Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            labels.Children.Add(Text("Collapse",18,Ink,true));labels.Children.Add(Text("Bedrock Launcher",10,Muted));Put(brand,labels,0,1);Put(sidebar,brand,0);
            var routes=new StackPanel {Margin=new Thickness(12,0,12,0)};
            foreach(string route in new[]{"Играть","Версии","Ресурспаки","Скины","Аккаунт","Настройки"})
            {
                string key=route;var tab=Button(route,()=>ShowPage(key));
                tab.HorizontalContentAlignment=HorizontalAlignment.Left;tab.Margin=new Thickness(0,0,0,8);tab.Padding=new Thickness(15,13,10,13);
                navigation[key]=tab;routes.Children.Add(tab);
            }
            Put(sidebar,routes,1);
            var links=new StackPanel {Margin=new Thickness(12,0,12,12)};
            links.Children.Add(Button("Канал проекта  ↗",()=>Open("https://t.me/mcbeforandroid")));
            var credit=Text("@bestphpp  ·  6.0",11,Muted);credit.Margin=new Thickness(8,12,0,0);links.Children.Add(credit);Put(sidebar,links,2);

            var backdrop=new Grid {Background=new LinearGradientBrush(Color.FromRgb(11,27,40),Color.FromRgb(27,39,57),30),ClipToBounds=true};
            backdrop.Children.Add(wallpaper);
            backdrop.Children.Add(new Border {Background=new SolidColorBrush(Color.FromArgb(88,5,13,23))});
            content.Margin=new Thickness(30,24,30,24);backdrop.Children.Add(content);Put(body,backdrop,0,1);
            var bottom=Cols(-1,0);bottom.Background=Hex("#0E1722");
            notice.Foreground=Muted;notice.FontSize=11;notice.Margin=new Thickness(19,0,12,0);notice.VerticalAlignment=VerticalAlignment.Center;
            notice.TextTrimming=TextTrimming.CharacterEllipsis;
            notice.MouseLeftButtonDown+=(s,e)=>{if(!String.IsNullOrWhiteSpace(notice.ToolTip as string))MessageBox.Show(this,(string)notice.ToolTip,"Collapse");};
            Put(bottom,notice,0);var build=Text("COLLAPSE  6.0",10,Muted,true);build.Margin=new Thickness(12,0,19,0);build.VerticalAlignment=VerticalAlignment.Center;
            Put(bottom,build,0,1);Put(outer,bottom,2);

            play=Button("Играть",Launch,true);play.FontSize=23;play.Padding=new Thickness(30,19,30,19);
            timer.Interval=TimeSpan.FromMilliseconds(500);timer.Tick+=(s,e)=>UpdatePlay();
            Closing+=(s,e)=>{if(busy||updating)e.Cancel=true;};
            Closed+=(s,e)=>{timer.Stop();if(game!=null)game.Dispose();};
            selector.SelectionChanged+=(s,e)=>{var i=selector.SelectedItem as Installation;if(i!=null){Store.State.SelectedFolder=i.Folder;TrySave();UpdatePlay();}};
            installed.SelectionChanged+=(s,e)=>{var i=installed.SelectedItem as Installation;if(i!=null)selector.SelectedItem=i;};
            available.SelectionChanged+=(s,e)=>{if(available.SelectedItem!=null)active.SelectedItem=null;};
            active.SelectionChanged+=(s,e)=>{if(active.SelectedItem!=null)available.SelectedItem=null;};
            filter.TextChanged+=(s,e)=>RenderCatalog();
            RefreshInstallations();ApplyWallpaper();ShowPage("Играть");
            if(live)Loaded+=async(s,e)=>{Scan();await LoadCatalog();await CheckUpdate(false);};
        }
        void ToggleMax(){WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;}
        static void Open(string value){Process.Start(new ProcessStartInfo(value){UseShellExecute=true});}
        void SetNotice(string value,bool error=false){notice.Text=value;notice.ToolTip=value;notice.Foreground=error?Hex("#FFC0AD"):Muted;}
        void Progress(string value){SetNotice(value);}
        bool RequireIdle()
        {
            if(busy||updating){SetNotice("Дождись завершения текущей операции.");return false;}
            if(Process.GetProcessesByName("Minecraft.Windows").Any()||Process.GetProcessesByName("Minecraft").Any())
            {SetNotice("Закрой Minecraft перед изменением файлов игры.",true);return false;}
            return true;
        }
        void TrySave(){try{Store.Save();}catch(Exception e){SetNotice(e.Message,true);}}
        internal void ShowPage(string key)
        {
            if(busy||updating)return;
            pageName=key;content.Children.Clear();
            foreach(var n in navigation){n.Value.Background=Hex(n.Key==key?"#194752":"#101F2D");n.Value.Foreground=n.Key==key?Accent:Muted;n.Value.BorderBrush=Hex(n.Key==key?"#42ADBA":"#233C4F");}
            foreach(var c in new UIElement[]{selector,play,installed,available,active,skins,filter,catalog,searchText,searchBar})Detach(c);
            switch(key){case "Играть":Home();break;case "Версии":Versions();break;case "Ресурспаки":Packs();break;case "Скины":Skins();break;case "Аккаунт":Account();break;default:Preferences();break;}
        }
        static void Detach(UIElement item)
        {
            var logical=LogicalTreeHelper.GetParent(item) as ContentControl;
            if(logical!=null){logical.Content=null;return;}
            var parent=VisualTreeHelper.GetParent(item) as Panel;if(parent!=null)parent.Children.Remove(item);
            var decorator=VisualTreeHelper.GetParent(item) as Decorator;if(decorator!=null)decorator.Child=null;
            var presenter=VisualTreeHelper.GetParent(item) as ContentPresenter;if(presenter!=null)presenter.Content=null;
            var scroll=VisualTreeHelper.GetParent(item) as ScrollViewer;if(scroll!=null)scroll.Content=null;
        }
        Grid Section(string title,string subtitle)
        {
            var g=Rows(0,-1);content.Children.Add(g);var headings=new StackPanel {Margin=new Thickness(0,0,0,21)};
            headings.Children.Add(Text(title,29,Ink,true));var sub=Text(subtitle,12,Muted);sub.Margin=new Thickness(1,7,0,0);headings.Children.Add(sub);Put(g,headings,0);return g;
        }
        UIElement SearchStatus()
        {
            var stack=new StackPanel {Margin=new Thickness(0,10,0,10)};
            searchText.FontSize=12;searchText.Foreground=Muted;searchText.Margin=new Thickness(0,0,0,8);
            stack.Children.Add(searchText);stack.Children.Add(searchBar);SearchMessage();return stack;
        }
        void SearchMessage()
        {
            searchText.Text=searching?"Ищем версии на устройстве…  "+Store.State.Installations.Count+" найдено":"На устройстве · "+Store.State.Installations.Count+" версий";
            searchBar.IsIndeterminate=searching;searchBar.Visibility=searching?Visibility.Visible:Visibility.Collapsed;
        }
        void Home()
        {
            var g=Rows(0,-1,0);content.Children.Add(g);var heading=Cols(-1,0);
            Put(heading,Button("Скачать версию  ↗",()=>ShowPage("Версии")),0,1);Put(g,heading,0);
            var center=Rows(-1,0,-1);Put(g,center,1);var card=Rows(0,0,0,0);card.MaxWidth=580;
            var title=Text("Minecraft Bedrock",34,Ink,true);title.Margin=new Thickness(0,0,0,9);Put(card,title,0);
            var sub=Text("Версия для запуска",13,Muted);sub.Margin=new Thickness(0,0,0,17);Put(card,sub,1);
            selector.Margin=new Thickness(0,0,0,15);Put(card,selector,2);Put(card,play,3);Put(center,Card(card,28),1);
            var bottom=Rows(0,0);Put(bottom,SearchStatus(),0);var row=Cols(-1,12,-1,12,-1);
            Put(row,InfoTile("Ресурспаки",Store.State.Packs.Count(p=>p.Active)+" активных",()=>ShowPage("Ресурспаки")),0);
            Put(row,InfoTile("Скин",Store.State.SelectedSkin==null?"Стандартный":"Выбран",()=>ShowPage("Скины")),0,2);
            Put(row,InfoTile("VSync",Store.State.DisableVsync?"Выключен":"Включён",()=>ShowPage("Настройки")),0,4);Put(bottom,row,1);Put(g,bottom,2);UpdatePlay();
        }
        UIElement InfoTile(string title,string value,Action click)
        {
            var labels=new StackPanel();labels.Children.Add(Text(title,11,Muted));labels.Children.Add(Text(value,14,Ink,true));
            var b=Button("",click);b.Content=labels;b.HorizontalContentAlignment=HorizontalAlignment.Left;return b;
        }
        static Version ParseVersion(string value){Version v;return Version.TryParse(value,out v)?v:new Version(0,0);}
        void RefreshInstallations()
        {
            string selected=Store.State.SelectedFolder;selector.ItemsSource=null;installed.ItemsSource=null;
            var list=Store.State.Installations.OrderByDescending(i=>ParseVersion(i.Version)).ToList();selector.ItemsSource=list;
            selector.SelectedItem=list.FirstOrDefault(i=>i.Folder==selected)??list.FirstOrDefault();installed.ItemsSource=list;installed.SelectedItem=selector.SelectedItem;SearchMessage();UpdatePlay();
        }
        async void Scan()
        {
            if(searching)return;searching=true;SearchMessage();
            try
            {
                await Task.Run(()=>Collapse.Scan((folder,version)=>Dispatcher.BeginInvoke(new Action(()=>{
                    if(Store.State.Installations.Any(i=>String.Equals(i.Folder,folder,StringComparison.OrdinalIgnoreCase)))return;
                    Store.State.Installations.Add(new Installation {Folder=folder,Version=version});RefreshInstallations();
                }))));
                Store.State.Installations.RemoveAll(i=>Collapse.Client(i.Folder)==null);TrySave();RefreshInstallations();
            }
            catch(Exception e){SetNotice("Поиск остановлен: "+e.Message,true);}
            finally{searching=false;SearchMessage();}
        }
        void AddFolder()
        {
            using(var picker=new System.Windows.Forms.FolderBrowserDialog {Description="Папка Minecraft",ShowNewFolderButton=false})
            {
                if(picker.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                string exe=Collapse.Client(picker.SelectedPath)??Collapse.Client(Path.Combine(picker.SelectedPath,"Content"));
                if(exe==null){SetNotice("В папке не найден Minecraft.Windows.exe.",true);return;}RegisterInstall(Path.GetDirectoryName(exe));
            }
        }
        void RegisterInstall(string folder)
        {
            if(!Store.State.Installations.Any(i=>String.Equals(i.Folder,folder,StringComparison.OrdinalIgnoreCase)))
                Store.State.Installations.Add(new Installation {Folder=folder,Version=Collapse.Version(Collapse.Client(folder))});
            Store.State.SelectedFolder=folder;TrySave();RefreshInstallations();
        }
        void UpdatePlay()
        {
            if(game!=null)try{if(game.HasExited){game.Dispose();game=null;timer.Stop();}}catch(InvalidOperationException){game=null;timer.Stop();}
            if(play==null)return;
            play.Content=busy?"Запускается…":game!=null?"Запущен":"Играть";play.IsEnabled=!busy&&!updating&&game==null&&selector.SelectedItem!=null;
        }
        async void Launch()
        {
            if(!RequireIdle())return;var i=selector.SelectedItem as Installation;if(i==null)return;
            busy=true;UpdatePlay();
            try
            {
                SetNotice("Применяем настройки профиля…");await Task.Run(()=>Store.Exclusive(()=>Library.Sync()));
                int pid=await BedrockVersions.Launch(i.Folder,new Progress<string>(Progress));game=Process.GetProcessById(pid);timer.Start();SetNotice("Запущен Bedrock "+i.Version);
            }
            catch(Exception e){SetNotice("Не удалось запустить: "+e.Message,true);}
            finally{busy=false;UpdatePlay();}
        }
        void Versions()
        {
            var section=Section("Версии","Установленные сборки и каталог Minecraft Bedrock.");var columns=Cols(255,20,-1);Put(section,columns,1);
            var left=Rows(0,0,-1,0);Put(left,Text("На устройстве",14,Ink,true),0);Put(left,SearchStatus(),1);Put(left,installed,2);
            var add=Button("Добавить папку",AddFolder);add.Margin=new Thickness(0,12,0,0);Put(left,add,3);Put(columns,left,0);
            var right=Rows(0,-1);filter.ToolTip="Поиск версии";filter.Margin=new Thickness(0,0,0,12);Put(right,filter,0);
            Put(right,new ScrollViewer {Content=catalog,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},1);Put(columns,right,0,2);RenderCatalog();
        }
        async Task LoadCatalog()
        {
            try{releases=await Downloads.Catalog();RenderCatalog();SetNotice("Каталог обновлён · "+releases.Count+" версий");}
            catch(Exception e){SetNotice("Каталог недоступен: "+e.Message,true);}
        }
        void RenderCatalog()
        {
            catalog.Children.Clear();if(releases.Count==0){catalog.Children.Add(Text("Загружаем каталог…",13,Muted));return;}
            foreach(var release in releases.Where(r=>r.Version.Contains(filter.Text.Trim())).Take(100))
            {
                var row=Cols(76,15,-1,0);var words=new StackPanel();words.Children.Add(Text("Bedrock "+release.Version,19,Ink,true));
                var local=Store.State.Installations.FirstOrDefault(i=>i.Version==release.Version);
                var sub=Text(local==null?"Релиз · "+(release.Gdk?"GDK":"UWP"):"В библиотеке",11,local==null?Muted:Accent);sub.Margin=new Thickness(0,7,0,0);words.Children.Add(sub);Put(row,words,0,2);
                Put(row,new Border {Height=65,CornerRadius=new CornerRadius(11),Background=new ImageBrush(defaultBackground){Stretch=Stretch.UniformToFill}},0,0);
                var b=Button(local==null?"Скачать":"Играть",()=>{if(local==null)Install(release);else{selector.SelectedItem=selector.Items.Cast<Installation>().FirstOrDefault(i=>i.Folder==local.Folder);ShowPage("Играть");Launch();}},local==null);
                b.VerticalAlignment=VerticalAlignment.Center;b.Margin=new Thickness(15,0,0,0);Put(row,b,0,3);
                var card=Card(row,18);card.Margin=new Thickness(0,0,8,11);catalog.Children.Add(card);
            }
        }
        async void Install(ReleaseItem release)
        {
            if(!RequireIdle())return;busy=true;UpdatePlay();
            try{string folder=await Downloads.Install(release,new Progress<string>(Progress));RegisterInstall(folder);RenderCatalog();SetNotice("Bedrock "+release.Version+" готова к запуску");}
            catch(Exception e){SetNotice("Не удалось установить: "+e.Message,true);}
            finally{busy=false;UpdatePlay();}
        }
        void Packs()
        {
            var section=Section("Ресурспаки","Один активный список применяется к профилям Bedrock перед запуском.");
            var layout=Rows(-1,0);Put(section,layout,1);var columns=Cols(-1,24,-1);
            available.ItemTemplate=PackTemplate(true);active.ItemTemplate=PackTemplate(false);
            var left=Rows(0,-1);Put(left,Text("Библиотека",15,Ink,true),0);available.Margin=new Thickness(0,13,0,0);Put(left,available,1);Put(columns,left,0);
            var right=Rows(0,-1);Put(right,Text("Активные",15,Accent,true),0);active.Margin=new Thickness(0,13,0,0);Put(right,active,1);Put(columns,right,0,2);Put(layout,columns,0);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,17,0,0)};
            actions.Children.Add(Button("Добавить .mcpack",ImportPack,true));
            foreach(var pair in new[]{Tuple.Create("Удалить",(Action)RemovePack),Tuple.Create("Выше ↑",(Action)(()=>MovePack(-1))),Tuple.Create("Ниже ↓",(Action)(()=>MovePack(1)))})
            {var b=Button(pair.Item1,pair.Item2);b.Margin=new Thickness(9,0,0,0);actions.Children.Add(b);}
            Put(layout,actions,1);RefreshPacks();
        }
        DataTemplate PackTemplate(bool enabled)
        {
            var row=new FrameworkElementFactory(typeof(DockPanel));
            var action=new FrameworkElementFactory(typeof(Button));
            action.SetValue(DockPanel.DockProperty,Dock.Right);
            action.SetValue(ContentControl.ContentProperty,enabled?"Активировать":"Деактивировать");
            action.SetValue(Control.FontSizeProperty,11.0);
            action.SetValue(Control.PaddingProperty,new Thickness(11,8,11,8));
            action.SetValue(FrameworkElement.MarginProperty,new Thickness(12,0,0,0));
            action.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            action.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,new RoutedEventHandler((sender,e)=>{
                e.Handled=true;TogglePack(((FrameworkElement)sender).DataContext as Pack,enabled);
            }));
            row.AppendChild(action);
            var name=new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("."));
            name.SetBinding(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("."));
            name.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            name.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            row.AppendChild(name);
            return new DataTemplate(typeof(Pack)){VisualTree=row};
        }
        void RefreshPacks(){available.ItemsSource=null;active.ItemsSource=null;available.ItemsSource=Store.State.Packs.Where(p=>!p.Active).ToList();active.ItemsSource=Store.State.Packs.Where(p=>p.Active).ToList();}
        void ImportPack()
        {
            if(!RequireIdle())return;var picker=new OpenFileDialog {Filter="Наборы ресурсов (*.mcpack;*.zip)|*.mcpack;*.zip",Multiselect=true};
            if(picker.ShowDialog(this)!=true)return;
            try{Store.Exclusive(()=>{foreach(string file in picker.FileNames)Library.ImportPack(file);});RefreshPacks();SetNotice("Ресурспаки добавлены.");}
            catch(Exception e){RefreshPacks();SetNotice(e.Message,true);}
        }
        void TogglePack(Pack pack,bool enabled)
        {
            if(!RequireIdle()||pack==null)return;
            try{Store.Exclusive(()=>{pack.Active=enabled;try{Library.Sync();Store.Save();}catch{pack.Active=!enabled;throw;}});RefreshPacks();SetNotice(enabled?"Ресурспак включён.":"Ресурспак выключен.");}
            catch(Exception e){RefreshPacks();SetNotice(e.Message,true);}
        }
        void RemovePack()
        {
            if(!RequireIdle())return;var pack=(available.SelectedItem??active.SelectedItem) as Pack;if(pack==null)return;
            try
            {
                Store.Exclusive(()=>{bool prior=pack.Active;pack.Active=false;try{Library.Sync();}catch{pack.Active=prior;throw;}
                    Store.State.Packs.Remove(pack);try{Store.Save();}catch{Store.State.Packs.Add(pack);throw;}
                    Library.RemoveCopies(pack);Store.DeleteOwned(pack.Folder,Path.Combine(Store.Root,"Packs"));});RefreshPacks();SetNotice("Ресурспак удалён из библиотеки.");
            }
            catch(Exception e){RefreshPacks();SetNotice(e.Message,true);}
        }
        void MovePack(int direction)
        {
            if(!RequireIdle())return;var pack=active.SelectedItem as Pack;if(pack==null)return;
            var all=Store.State.Packs;int index=all.IndexOf(pack),next=index+direction;if(next<0||next>=all.Count)return;
            var old=all.ToList();try{all.RemoveAt(index);all.Insert(next,pack);Library.Sync();Store.Save();RefreshPacks();active.SelectedItem=pack;}
            catch(Exception e){Store.State.Packs=old;RefreshPacks();SetNotice(e.Message,true);}
        }
        void Skins()
        {
            var section=Section("Скины","PNG-скины в общей библиотеке для найденных профилей Bedrock.");var columns=Cols(245,22,-1);Put(section,columns,1);
            var preview=Rows(-1,0);var current=Store.State.Skins.FirstOrDefault(s=>s.Id==Store.State.SelectedSkin);
            Put(preview,current==null?(UIElement)Text("Добавь свой PNG-скин",16,Muted,true):SkinPreview(current.FilePath),0);
            var name=Text(current==null?"Стандартный скин":current.Name,18,Ink,true);name.HorizontalAlignment=HorizontalAlignment.Center;Put(preview,name,1);Put(columns,Card(preview),0);
            var right=Rows(-1,0,0);Put(columns,right,0,2);skins.ItemsSource=null;skins.ItemsSource=Store.State.Skins;skins.SelectedItem=current;Put(right,skins,0);
            var controls=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,13,0,0)};
            controls.Children.Add(Button("Добавить PNG",ImportSkin,true));var use=Button("Применить",UseSkin);use.Margin=new Thickness(8,0,0,0);controls.Children.Add(use);
            var del=Button("Удалить",DeleteSkin);del.Margin=use.Margin;controls.Children.Add(del);Put(right,controls,1);
            var tip=Text("Первый выбор проверь в гардеробе Minecraft. Набор Collapse появляется в «Классических скинах».",11,Muted);tip.Margin=new Thickness(0,15,0,0);Put(right,tip,2);
        }
        UIElement SkinPreview(string file)
        {
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(file);bitmap.EndInit();bitmap.Freeze();
            int factor=bitmap.PixelWidth/64;var canvas=new Canvas {Width=128,Height=256,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            Action<int,int,int,int,int,int,int,int> part=(x,y,w,h,dx,dy,dw,dh)=>{
                var image=new Image {Source=new CroppedBitmap(bitmap,new Int32Rect(x*factor,y*factor,w*factor,h*factor)),Width=dw,Height=dh};
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);Canvas.SetLeft(image,dx);Canvas.SetTop(image,dy);canvas.Children.Add(image);};
            part(8,8,8,8,32,0,64,64);part(20,20,8,12,32,64,64,96);part(44,20,4,12,0,64,32,96);part(44,20,4,12,96,64,32,96);part(4,20,4,12,32,160,32,96);
            if(bitmap.PixelHeight>=64*factor)part(20,52,4,12,64,160,32,96);else part(4,20,4,12,64,160,32,96);return canvas;
        }
        void ImportSkin()
        {
            if(!RequireIdle())return;var picker=new OpenFileDialog {Filter="PNG-скины (*.png)|*.png"};if(picker.ShowDialog(this)!=true)return;
            var model=MessageBox.Show(this,"Использовать тонкие руки Slim? Для Classic выбери «Нет».","Модель скина",MessageBoxButton.YesNoCancel);
            if(model==MessageBoxResult.Cancel)return;
            try{Store.Exclusive(()=>Library.ImportSkin(picker.FileName,model==MessageBoxResult.Yes));ShowPage("Скины");}catch(Exception e){SetNotice(e.Message,true);}
        }
        void UseSkin()
        {
            if(!RequireIdle())return;var skin=skins.SelectedItem as Skin;if(skin==null)return;string prior=Store.State.SelectedSkin;
            try{Store.State.SelectedSkin=skin.Id;int count=0;Store.Exclusive(()=>{count=Library.Sync();Store.Save();});ShowPage("Скины");SetNotice("Скин установлен в профили: "+count+". Проверь выбор в гардеробе.");}
            catch(Exception e){Store.State.SelectedSkin=prior;SetNotice(e.Message,true);}
        }
        void DeleteSkin()
        {
            if(!RequireIdle())return;var skin=skins.SelectedItem as Skin;if(skin==null)return;
            if(Store.State.SelectedSkin==skin.Id){SetNotice("Для удаления текущего скина сначала выбери другой.",true);return;}
            try{Store.State.Skins.Remove(skin);Store.Save();File.Delete(skin.FilePath);ShowPage("Скины");}catch(Exception e){SetNotice(e.Message,true);}
        }
        void Account()
        {
            var section=Section("Аккаунт Xbox","Вход и выбор игрового профиля выполняет Minecraft.");var stack=new StackPanel();
            stack.Children.Add(Text("Аккаунт для игры",22,Ink,true));
            var copy=Text("Переключение игровых сессий из Collapse пока не реализовано. Microsoft хранит их внутри Minecraft и служб Xbox. Для смены аккаунта используй настройки профиля в игре.",14,Muted);
            copy.Margin=new Thickness(0,13,0,21);stack.Children.Add(copy);
            var launch=Button("Открыть выбранную версию",()=>{ShowPage("Играть");Launch();},true);launch.HorizontalAlignment=HorizontalAlignment.Left;stack.Children.Add(launch);
            var link=Button("Мой профиль Xbox  ↗",()=>Open("https://account.xbox.com/Profile"));link.Margin=new Thickness(0,11,0,0);link.HorizontalAlignment=HorizontalAlignment.Left;stack.Children.Add(link);
            var card=Card(stack,25);card.VerticalAlignment=VerticalAlignment.Top;Put(section,card,1);
        }
        void Preferences()
        {
            var section=Section("Настройки","Производительность, фон и обновления.");var stack=new StackPanel();
            var fps=new CheckBox {Content="Отключить VSync и ограничение FPS",IsChecked=Store.State.DisableVsync};
            fps.Click+=(s,e)=>{if(!RequireIdle()){fps.IsChecked=Store.State.DisableVsync;return;}bool old=Store.State.DisableVsync;
                try{Store.State.DisableVsync=fps.IsChecked==true;Library.Sync();Store.Save();SetNotice("Настройка FPS сохранена.");}catch(Exception error){Store.State.DisableVsync=old;fps.IsChecked=old;SetNotice(error.Message,true);}};
            stack.Children.Add(fps);stack.Children.Add(Text("Применяется к настройкам найденных профилей и повторно перед запуском.",12,Muted));
            var backgrounds=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,23,0,20)};backgrounds.Children.Add(Button("Выбрать фон…",ChooseWallpaper));
            var reset=Button("Сбросить фон",()=>{Store.State.Background=null;TrySave();ApplyWallpaper();});reset.Margin=new Thickness(9,0,0,0);backgrounds.Children.Add(reset);stack.Children.Add(backgrounds);
            var auto=new CheckBox {Content="Автоматически скачивать и устанавливать обновления",IsChecked=Store.State.AutoUpdate};auto.Click+=(s,e)=>{Store.State.AutoUpdate=auto.IsChecked==true;TrySave();};stack.Children.Add(auto);
            stack.Children.Add(Text("Обновление проверяется при запуске. Загрузка, проверка и перезапуск выполняются внутри лаунчера.",12,Muted));
            var update=Button("Проверить обновления",async()=>await CheckUpdate(true),true);update.Margin=new Thickness(0,18,0,0);update.HorizontalAlignment=HorizontalAlignment.Left;stack.Children.Add(update);
            var folder=Button("Открыть библиотеку  ↗",()=>Open(Store.Root));folder.Margin=new Thickness(0,15,0,0);folder.HorizontalAlignment=HorizontalAlignment.Left;stack.Children.Add(folder);
            var card=Card(stack,24);card.VerticalAlignment=VerticalAlignment.Top;Put(section,card,1);
        }
        void ChooseWallpaper()
        {
            var picker=new OpenFileDialog {Filter="Изображения (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"};if(picker.ShowDialog(this)!=true)return;
            try{var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(picker.FileName);image.EndInit();
                string dest=Path.Combine(Store.Root,"wallpaper"+Path.GetExtension(picker.FileName).ToLowerInvariant());if(!String.Equals(dest,picker.FileName,StringComparison.OrdinalIgnoreCase))File.Copy(picker.FileName,dest,true);
                Store.State.Background=dest;TrySave();ApplyWallpaper();}catch(Exception e){SetNotice("Не удалось загрузить фон: "+e.Message,true);}
        }
        void ApplyWallpaper()
        {
            if(defaultBackground==null)
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("landscape.png"))
                {
                    var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
                    image.StreamSource=stream;image.EndInit();image.Freeze();defaultBackground=image;
                }
            wallpaper.Source=defaultBackground;if(String.IsNullOrWhiteSpace(Store.State.Background)||!File.Exists(Store.State.Background))return;
            try{var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=1800;image.UriSource=new Uri(Store.State.Background);image.EndInit();image.Freeze();wallpaper.Source=image;}
            catch(Exception){Store.State.Background=null;}
        }
        async Task CheckUpdate(bool manual)
        {
            if(updating||busy)return;
            try
            {
                var release=await Updater.Check();if(release==null){if(manual)SetNotice("Установлена последняя версия.");return;}
                if(!manual&&!Store.State.AutoUpdate){SetNotice("Доступна новая версия · открой настройки для обновления.");return;}
                if(game!=null||busy||updating){SetNotice("Обновление доступно после закрытия игры.");return;}
                updating=true;UpdatePlay();string path=await Updater.Download(release,new Progress<string>(Progress));
                Updater.StartReplacement(path);updating=false;Close();
            }
            catch(Exception e){if(manual||updating)SetNotice("Обновление не завершено: "+e.Message,true);}
            finally{updating=false;UpdatePlay();}
        }
    }
}
