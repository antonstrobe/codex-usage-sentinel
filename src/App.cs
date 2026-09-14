using System;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexUsageSentinel {
    public static class Startup {
        const string KeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName="CodexUsageSentinel";
        public static string Command {get{return "\""+Application.ExecutablePath+"\" --tray";}}
        public static string Folder {get{return Environment.GetFolderPath(Environment.SpecialFolder.Startup);}}
        public static void OpenFolder() {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+Folder+"\"") {UseShellExecute=true});
        }
        public static bool Enabled {
            get {using(var key=Registry.CurrentUser.OpenSubKey(KeyPath)) return key!=null && string.Equals(key.GetValue(ValueName) as string,Command,StringComparison.OrdinalIgnoreCase);}
        }
        public static void Toggle() {
            bool enabled=Enabled;
            using(var key=Registry.CurrentUser.CreateSubKey(KeyPath)) {
                if(enabled)key.DeleteValue(ValueName,false);else key.SetValue(ValueName,Command,RegistryValueKind.String);
            }
        }
        public static bool InStartupFolder {
            get {return string.Equals(Path.GetDirectoryName(Application.ExecutablePath),Environment.GetFolderPath(Environment.SpecialFolder.Startup),StringComparison.OrdinalIgnoreCase);}
        }
    }
    public static class Theme {
        public static Color Bg=Color.FromArgb(15,19,28),Panel=Color.FromArgb(23,30,43),Text=Color.FromArgb(236,241,248),Muted=Color.FromArgb(153,169,192),Green=Color.FromArgb(89,219,173),Red=Color.FromArgb(255,119,137);
        public static Color ButtonBg=Color.FromArgb(37,48,67),ActionBlue=Color.FromArgb(119,176,255);
        public static Label Label(string text,float size,Color color,FontStyle style=FontStyle.Regular) {
            return new Label {Text=text,ForeColor=color,Font=new Font("Segoe UI",size,style),AutoSize=true,Margin=new Padding(0,0,0,8),BackColor=Color.Transparent};
        }
        public static Button Button(string text,Action action,bool primary=false) {
            var b=new Button {Text=text,AutoSize=true,Height=39,MinimumSize=new Size(0,39),FlatStyle=FlatStyle.Flat,BackColor=primary ? ActionBlue : ButtonBg,
                ForeColor=primary ? Bg : Text,Font=new Font("Segoe UI",10,FontStyle.Regular),Padding=new Padding(10,3,10,3),Margin=new Padding(0,0,8,8),Cursor=Cursors.Hand};
            b.FlatAppearance.BorderSize=0;b.Click+=(s,e)=>action();return b;
        }
        public static void Describe(Button button,ToolTip tips,string description) {
            button.AccessibleName=button.Text;button.AccessibleDescription=description;
            if(tips.GetToolTip(button)!=description)tips.SetToolTip(button,description);
        }
        public static void ToggleState(Button button,ToolTip tips,bool active,string label,string description) {
            button.Text=(active ? "✓ " : "○ ")+label;
            button.UseVisualStyleBackColor=false;
            button.BackColor=active ? Green : ButtonBg;button.ForeColor=active ? Bg : Text;
            button.FlatAppearance.MouseOverBackColor=active ? Color.FromArgb(117,232,193) : Color.FromArgb(50,65,88);
            button.FlatAppearance.MouseDownBackColor=active ? Color.FromArgb(64,190,145) : Color.FromArgb(62,78,104);
            Describe(button,tips,description);
        }
        public static ToolTip Tooltips() {
            var tips=new ToolTip {InitialDelay=450,ReshowDelay=150,AutoPopDelay=10000,ShowAlways=true,OwnerDraw=true,BackColor=Panel,ForeColor=Text};
            tips.Popup+=(s,e)=>{
                var size=TextRenderer.MeasureText(tips.GetToolTip(e.AssociatedControl),SystemFonts.MessageBoxFont,new Size(420,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
                e.ToolTipSize=new Size(size.Width+20,size.Height+16);
            };
            tips.Draw+=(s,e)=>{
                e.DrawBackground();
                using(var border=new Pen(Color.FromArgb(69,86,110)))e.Graphics.DrawRectangle(border,0,0,e.Bounds.Width-1,e.Bounds.Height-1);
                TextRenderer.DrawText(e.Graphics,e.ToolTipText,SystemFonts.MessageBoxFont,new Rectangle(10,8,e.Bounds.Width-20,e.Bounds.Height-16),Text,TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
            };
            return tips;
        }
        public static Icon Icon() {
            using(var bmp=new Bitmap(32,32))using(var g=Graphics.FromImage(bmp)) {
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(Color.Transparent);
                using(var brush=new SolidBrush(Panel))g.FillEllipse(brush,1,1,30,30);
                using(var pen=new Pen(Green,3))g.DrawArc(pen,7,7,18,18,130,285);
                using(var pen=new Pen(Text,2)) {g.DrawLine(pen,16,10,16,17);g.DrawLine(pen,16,17,21,19);}
                IntPtr h=bmp.GetHicon();try{return (Icon)System.Drawing.Icon.FromHandle(h).Clone();}finally{DestroyIcon(h);}
            }
        }
        [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr handle);
        [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        public static void DarkTitle(Form form) {try{int dark=1;DwmSetWindowAttribute(form.Handle,20,ref dark,4);}catch{}}
    }
    public sealed class SetupForm : Form {
        TextBox token,id,username;
        Label status;
        Button connect;
        Monitor monitor;
        CancellationTokenSource cancel=new CancellationTokenSource();
        public SetupForm(Monitor monitor) {
            this.monitor=monitor;Text="Личный Telegram · Codex Usage Sentinel";BackColor=Theme.Bg;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);
            ClientSize=new Size(610,550);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;
            HandleCreated+=(s,e)=>Theme.DarkTitle(this);
            var flow=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24)};Controls.Add(flow);
            flow.Controls.Add(Theme.Label("Подключить Telegram",20,Theme.Text,FontStyle.Bold));
            flow.Controls.Add(new Label {Text="Бот определяется по токену. Уведомления — только в ваш личный чат.",AutoSize=false,Size=new Size(560,36),ForeColor=Theme.Muted});
            flow.Controls.Add(Theme.Label("Токен бота",10,Theme.Text));
            token=new TextBox {Width=535,UseSystemPasswordChar=true,BackColor=Theme.Panel,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle,Font=new Font("Segoe UI",12),Margin=new Padding(0,0,0,15)};flow.Controls.Add(token);
            token.AccessibleName="Токен Telegram-бота";token.AccessibleDescription="Оставьте пустым, чтобы использовать сохранённый токен. Новый токен хранится зашифрованным Windows.";
            flow.Controls.Add(Theme.Label("Ваш Telegram username · без @; можно пропустить при известном ID",10,Theme.Text));
            username=new TextBox {Width=535,Text=monitor.Settings.Username,BackColor=Theme.Panel,ForeColor=Theme.Text,Font=new Font("Segoe UI",12),Margin=new Padding(0,0,0,14),AccessibleName="Ваш Telegram username"};flow.Controls.Add(username);
            flow.Controls.Add(Theme.Label("Личный Chat ID · сохранённый ID подставляется автоматически",10,Theme.Text));
            id=new TextBox {Width=535,Text=monitor.Settings.ChatId>0 ? monitor.Settings.ChatId.ToString() : "",BackColor=Theme.Panel,ForeColor=Theme.Text,Font=new Font("Segoe UI",12),Margin=new Padding(0,0,0,14)};flow.Controls.Add(id);
            id.AccessibleName="Числовой ID личного чата";
            username.TextChanged+=(s,e)=>{if(id.Text==monitor.Settings.ChatId.ToString())id.Clear();};
            var hint=new Label {Text="Пустое поле ID: используем сохранённый, а если его нет — ищем личный чат. Telegram разрешает писать после Start или сообщения этому боту.\nТокен хранится зашифрованным средствами Windows для вашей учётной записи.",Size=new Size(535,74),ForeColor=Theme.Muted};flow.Controls.Add(hint);
            connect=Theme.Button("Проверить и сохранить",()=>Connect(),true);flow.Controls.Add(connect);
            status=new Label {Size=new Size(535,74),ForeColor=Theme.Muted,Text=!string.IsNullOrEmpty(monitor.Settings.TokenProtected) ? "Токен уже сохранён. Оставьте поле пустым, чтобы использовать его." : "Проверю бота и личного получателя перед сохранением."};flow.Controls.Add(status);
            FormClosed+=(s,e)=>{cancel.Cancel();token.Clear();};
        }
        async void Connect() {
            connect.Enabled=false;status.Text="Проверяю Telegram…";
            try {
                var settings=await monitor.Bot.Connect(monitor.Settings,token.Text,id.Text,username.Text,cancel.Token);
                monitor.SetSettings(settings);token.Clear();DialogResult=DialogResult.OK;Close();
            } catch(OperationCanceledException) {}
            catch(Exception ex) {if(!IsDisposed){status.Text=ex is TelegramFailure || ex is InvalidOperationException ? ex.Message : "Не удалось сохранить настройки. Проверьте доступ к папке приложения.";status.ForeColor=Theme.Red;}}
            finally {if(!IsDisposed)connect.Enabled=true;}
        }
    }
    public sealed class MainForm : Form {
        readonly Monitor monitor;
        readonly bool renderOnly;
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer clock=new System.Windows.Forms.Timer {Interval=1000};
        readonly System.Windows.Forms.Timer activation=new System.Windows.Forms.Timer {Interval=400};
        readonly EventWaitHandle showEvent;
        readonly ToolTip tips=Theme.Tooltips();
        readonly ToolStripMenuItem trayPause;
        Label remaining,subline,windows,health,telegram,detail,policy;
        Button startup,pause,test,telegramSetup;
        bool quitting,testing;
        public MainForm(Monitor monitor,bool trayStart,bool renderOnly,EventWaitHandle showEvent) {
            this.monitor=monitor;this.renderOnly=renderOnly;this.showEvent=showEvent;
            Text="Codex Usage Sentinel · Build "+Program.Build;Icon=Theme.Icon();BackColor=Theme.Bg;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(780,730);MinimumSize=new Size(740,740);StartPosition=FormStartPosition.CenterScreen;
            HandleCreated+=(s,e)=>Theme.DarkTitle(this);
            var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(24)};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,72));root.RowStyles.Add(new RowStyle(SizeType.Absolute,224));root.RowStyles.Add(new RowStyle(SizeType.Absolute,85));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,97));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,96));Controls.Add(root);
            var header=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            header.Controls.Add(Theme.Label("CODEX  /  USAGE SENTINEL",19,Theme.Text,FontStyle.Bold));
            header.Controls.Add(Theme.Label("Лимиты под наблюдением · личные уведомления в Telegram",10,Theme.Muted));root.Controls.Add(header,0,0);
            var card=new Panel {Dock=DockStyle.Fill,BackColor=Theme.Panel,Padding=new Padding(20),Margin=new Padding(0,0,0,14)};
            remaining=Theme.Label("—",46,Theme.Green,FontStyle.Bold);remaining.Location=new Point(20,12);card.Controls.Add(remaining);
            subline=Theme.Label("Остаток основного лимита",11,Theme.Muted);subline.Location=new Point(25,93);card.Controls.Add(subline);
            windows=new Label {AutoSize=false,Location=new Point(25,129),Size=new Size(674,64),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,Font=new Font("Segoe UI",10),ForeColor=Theme.Text};card.Controls.Add(windows);root.Controls.Add(card,0,1);
            health=new Label {Dock=DockStyle.Fill,ForeColor=Theme.Muted,Padding=new Padding(0,0,0,3)};root.Controls.Add(health,0,2);
            var alarmPanel=new Panel {Dock=DockStyle.Fill,BackColor=Theme.Panel,Padding=new Padding(14)};
            var alarms=Theme.Button("Будильники…",()=>OpenAlarms());alarms.AutoSize=false;alarms.Width=158;alarms.Dock=DockStyle.Right;alarmPanel.Controls.Add(alarms);
            policy=new Label {Dock=DockStyle.Fill,ForeColor=Theme.Text};alarmPanel.Controls.Add(policy);policy.BringToFront();root.Controls.Add(alarmPanel,0,3);
            Theme.Describe(alarms,tips,"Открыть будильники: добавить, изменить или удалить событие по проценту; выбрать количество сообщений и интервал между ними.");
            var bottom=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(0,12,0,0)};
            telegram=new Label {Size=new Size(714,45),ForeColor=Theme.Text};bottom.Controls.Add(telegram);
            detail=new Label {Size=new Size(714,57),ForeColor=Theme.Muted,Font=new Font("Segoe UI",9)};bottom.Controls.Add(detail);root.Controls.Add(bottom,0,4);
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};
            var check=Theme.Button("Проверить сейчас",()=>monitor.CheckNow(),true);buttons.Controls.Add(check);
            Theme.Describe(check,tips,"Проверить лимиты Codex сейчас. Это разовое действие; автоматическая проверка продолжается каждую минуту.");
            telegramSetup=Theme.Button("Telegram…",()=>Setup());buttons.Controls.Add(telegramSetup);
            test=Theme.Button("Тест ×10",()=>Test());buttons.Controls.Add(test);
            var testMenu=new ContextMenuStrip {BackColor=Theme.Panel,ForeColor=Theme.Text};
            testMenu.Items.Add("Отправить 1 тестовое сообщение",null,(s,e)=>Test(1));test.ContextMenuStrip=testMenu;
            var codex=Theme.Button("Codex CLI…",()=>ChooseCodex());buttons.Controls.Add(codex);
            Theme.Describe(codex,tips,"Выбрать установленный codex.exe, через который программа читает лимиты.");
            var hide=Theme.Button("В трей",()=>Hide());buttons.Controls.Add(hide);buttons.SetFlowBreak(hide,true);
            Theme.Describe(hide,tips,"Свернуть окно в трей. Программа продолжит проверять лимиты и отправлять уведомления.");
            startup=Theme.Button("Автозапуск",()=>ToggleStartup());buttons.Controls.Add(startup);
            pause=Theme.Button("Пауза на 30 мин",()=>{try{monitor.Pause();RefreshStatus();}catch{ShowError("Не удалось сохранить паузу.");}});buttons.Controls.Add(pause);
            var startupFolder=Theme.Button("Папка автозагрузки",()=>OpenStartupFolder());buttons.Controls.Add(startupFolder);
            Theme.Describe(startupFolder,tips,"Открыть папку автозагрузки Windows. Можно положить туда EXE или ярлык, а затем удалить его из этой папки. Это разовое действие.");
            root.Controls.Add(buttons,0,5);
            tray=new NotifyIcon {Icon=Icon,Text="Codex Usage Sentinel",Visible=!renderOnly};
            var menu=new ContextMenuStrip {BackColor=Theme.Panel,ForeColor=Theme.Text};
            menu.Items.Add("Открыть",null,(s,e)=>Reveal());menu.Items.Add("Проверить лимиты",null,(s,e)=>monitor.CheckNow());
            menu.Items.Add("Настроить Telegram…",null,(s,e)=>{Reveal();Setup();});
            menu.Items.Add("Будильники…",null,(s,e)=>{Reveal();OpenAlarms();});
            menu.Items.Add("Папка автозагрузки",null,(s,e)=>OpenStartupFolder());
            menu.Items.Add("Отправить 1 тестовое сообщение",null,(s,e)=>Test(1));
            trayPause=new ToolStripMenuItem("Пауза уведомлений выключена");
            trayPause.Click+=(s,e)=>{try{monitor.Pause();RefreshStatus();}catch{ShowError("Не удалось сохранить паузу.");}};menu.Items.Add(trayPause);
            menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Выйти",null,(s,e)=>Quit());tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Reveal();
            monitor.Changed+=()=>{if(IsHandleCreated && !IsDisposed)try{BeginInvoke((Action)RefreshStatus);}catch{}};
            clock.Tick+=(s,e)=>RefreshStatus();activation.Tick+=(s,e)=>{if(showEvent!=null && showEvent.WaitOne(0))Reveal();};
            FormClosing+=(s,e)=>{
                if(!quitting && e.CloseReason==CloseReason.UserClosing && !renderOnly){e.Cancel=true;Hide();return;}
                monitor.Dispose();tray.Visible=false;clock.Stop();activation.Stop();
            };
            FormClosed+=(s,e)=>{tray.Dispose();clock.Dispose();activation.Dispose();tips.Dispose();};RefreshStatus();
        }
        void ShowError(string message) {MessageBox.Show(this,message,"Codex Usage Sentinel",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        public void StartMonitoring() {if(renderOnly)return;var handle=Handle;monitor.Start();clock.Start();activation.Start();}
        void OpenStartupFolder() {try{Startup.OpenFolder();}catch{ShowError("Не удалось открыть папку. Нажмите Win+R, введите shell:startup и нажмите Enter.");}}
        void Reveal() {Show();WindowState=FormWindowState.Normal;Activate();}
        void Quit() {quitting=true;Close();}
        void Setup() {using(var f=new RelaySetupForm(monitor))f.ShowDialog(this);RefreshStatus();}
        void OpenAlarms() {using(var f=new AlarmsForm(monitor))f.ShowDialog(this);RefreshStatus();}
        async void Test(int total=10) {
            if(testing)return;
            testing=true;test.Enabled=false;RefreshStatus();
            try{for(int number=1;number<=total;number++)await monitor.TestMessage(number,total);}
            catch(OperationCanceledException){}
            catch(Exception ex){if(!IsDisposed)ShowError(ex is TelegramFailure || ex is DeliveryUncertain || ex is InvalidOperationException ? ex.Message : "Не удалось отправить тестовое сообщение.");}
            finally{testing=false;if(!IsDisposed){test.Enabled=monitor.Settings.Ready;RefreshStatus();}}
        }
        void ToggleStartup() {if(Startup.InStartupFolder){ShowError("EXE уже находится в папке автозагрузки. Для отключения переместите его в другую папку; там будет доступна кнопка включения и выключения автозапуска.");return;}try{Startup.Toggle();RefreshStatus();}catch{ShowError("Не удалось изменить автозапуск. Можно положить EXE или ярлык в папку shell:startup.");}}
        void ChooseCodex() {
            using(var dialog=new OpenFileDialog {Title="Выберите codex.exe",Filter="Codex CLI|codex.exe"}) {
                if(dialog.ShowDialog(this)==DialogResult.OK) {monitor.Settings.CodexPath=dialog.FileName;try{monitor.SetSettings(monitor.Settings);monitor.CheckNow();}catch{ShowError("Не удалось сохранить путь Codex.");}}
            }
        }
        public void RefreshStatus() {
            var usage=monitor.Latest;
            bool fresh=monitor.Fresh && usage!=null && DateTime.UtcNow-usage.CheckedUtc<=TimeSpan.FromSeconds(90);
            remaining.Text=fresh ? usage.Remaining.ToString("0.#",CultureInfo.InvariantCulture)+"%" : "—";
            var alarmRules=monitor.AlarmRules;var enabledRules=alarmRules.Where(r=>r.Enabled).OrderByDescending(r=>r.Percent).ToList();
            remaining.ForeColor=fresh && enabledRules.Any(r=>usage.Remaining<=r.Percent) ? Theme.Red : Theme.Green;
            policy.Text="Активных будильников: "+enabledRules.Count+" из "+alarmRules.Count+"\n"+
                (enabledRules.Count==0 ? "Уведомления по процентам выключены" : string.Join("   ·   ",enabledRules.Take(3).Select(r=>"≤"+r.Percent+"%: "+(r.Continuous ? "∞" : r.MessageCount.ToString())))+(enabledRules.Count>3 ? "   …" : ""))+"\nПроверка лимитов каждые 60 секунд";
            subline.Text=fresh ? "Осталось · минимум среди основных окон Codex" : "Нет свежих данных о лимитах";
            windows.Text=usage==null ? "Ожидаю ответ Codex…" : string.Join("\n",usage.Core.Select(w=>w.Label+": "+w.Remaining.ToString("0.#")+"% · "+w.ResetText));
            if(!fresh && usage!=null)windows.Text="Последние известные значения:\n"+windows.Text;
            string checkedAt=usage==null ? "ещё не проверено" : usage.CheckedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss");
            string resets=usage!=null && usage.Resets.HasValue ? usage.Resets.Value.ToString() : "нет данных";
            health.Text=monitor.ReadStatus+"\nПоследний успех: "+checkedAt+"   ·   Доступные сбросы: "+resets+
                (monitor.NextCheckUtc>DateTime.UtcNow ? "\nСледующая проверка через "+Math.Ceiling((monitor.NextCheckUtc-DateTime.UtcNow).TotalSeconds)+" сек" : "");
            health.ForeColor=fresh ? Theme.Muted : Theme.Red;
            telegram.Text=monitor.Settings.Ready ? "Telegram  @"+monitor.Settings.BotUsername+" → "+(monitor.Settings.Username=="" ? "личный чат "+monitor.Settings.ChatId : "@"+monitor.Settings.Username)+"\n"+monitor.TelegramStatus : "Telegram не подключён · нажмите «Telegram…»\nПолучатель: ваш личный чат";
            string extra=usage!=null ? string.Join("; ",usage.Windows.Where(w=>!w.Core).Select(w=>w.Label+" "+w.Remaining.ToString("0.#")+"%")) : "";
            detail.Text=(monitor.Paused ? "Уведомления на паузе до "+DateTime.Parse(monitor.Settings.PausedUntilUtc,null,DateTimeStyles.RoundtripKind).ToLocalTime().ToString("HH:mm") : "Закрытие окна сворачивает программу в трей. Выход — через меню значка.")+
                "\n"+(monitor.StorageStatus!="" ? monitor.StorageStatus : Storage.Warning!="" ? Storage.Warning : "Другие лимиты (справочно): "+(extra=="" ? "нет данных" : extra));
            try{
                bool fromFolder=Startup.InStartupFolder,enabled=Startup.Enabled||fromFolder;
                string hint=enabled ? "Сейчас: автозапуск включён. Программа запускается при входе в Windows.\n"+(fromFolder ? "EXE находится в папке автозагрузки. Чтобы отключить автозапуск, переместите его в другую папку." : "Нажмите, чтобы отключить автозапуск.") : "Сейчас: автозапуск выключен. Программа не добавлена в автозапуск.\nНажмите, чтобы включить запуск при входе в Windows.";
                Theme.ToggleState(startup,tips,enabled,enabled ? "Автозапуск включён" : "Автозапуск выключен",hint);
            }catch{Theme.ToggleState(startup,tips,false,"Автозапуск: статус неизвестен","Не удалось проверить настройку автозапуска Windows.");}
            bool paused=monitor.Paused;
            string pauseState=paused ? "Пауза уведомлений включена" : "Пауза уведомлений выключена";
            string pauseHint=paused ? "Сейчас: пауза включена. Автоматические уведомления приостановлены до "+DateTime.Parse(monitor.Settings.PausedUntilUtc,null,DateTimeStyles.RoundtripKind).ToLocalTime().ToString("HH:mm")+". Проверка лимитов продолжается.\nНажмите, чтобы возобновить уведомления." : "Сейчас: пауза выключена. Автоматические уведомления разрешены.\nНажмите, чтобы приостановить их на 30 минут. Проверка лимитов продолжится.";
            Theme.ToggleState(pause,tips,paused,pauseState,pauseHint);
            trayPause.Text=pauseState;trayPause.Checked=paused;trayPause.ToolTipText=pauseHint;
            Theme.Describe(telegramSetup,tips,monitor.Settings.Ready ? "Сейчас: Telegram настроен для вашего личного чата.\nНажмите, чтобы открыть настройки подключения." : "Сейчас: Telegram не настроен.\nНажмите, чтобы подключить вашего бота и личный чат.");
            test.Text=testing ? "Тест отправляется…" : "Тест ×10";
            Theme.Describe(test,tips,testing ? "Сейчас программа отправляет тестовую серию. Дождитесь завершения; повторный запуск временно недоступен." : "Отправить 10 тестовых сообщений в ваш личный Telegram-чат с интервалом не менее 2 секунд. Правая кнопка мыши или Shift+F10 открывает одиночный тест. Это разовое действие.");
            test.Enabled=monitor.Settings.Ready && !testing;
            tray.Text="Codex: "+(fresh ? remaining.Text+" осталось" : "нет свежих данных")+(monitor.Paused ? " · пауза" : "");
        }
    }
    public static class Program {
        public const string Build=BuildInfo.Version;
        public static bool StartInTray(string[] args) {return !args.Contains("--show");}
        [STAThread] public static int Main(string[] args) {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>=2 && args[0]=="--probe") {
                try {
                    var usage=CodexClient.Read(Storage.Load<Settings>("settings.json").CodexPath,CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(args[1],Json.Write(new {remaining=usage.Remaining,resetCount=usage.Resets,checkedUtc=usage.CheckedUtc.ToString("o"),windows=usage.Windows.Select(w=>new {label=w.Label,remaining=w.Remaining,reset=w.ResetText})}),new System.Text.UTF8Encoding(false));return 0;
                } catch {File.WriteAllText(args[1],"{\"error\":\"Unable to read current Codex limits\"}");return 1;}
            }
            if(args.Length>=2 && args[0]=="--render") {
                using(var monitor=new Monitor(Storage.Load<Settings>("settings.json"),new AlertState())) {
                    try {monitor.Latest=CodexClient.Read(monitor.Settings.CodexPath,CancellationToken.None).GetAwaiter().GetResult();monitor.Fresh=true;monitor.ReadStatus="Лимиты получены · обновление каждую минуту";} catch {monitor.ReadStatus="Нет свежих данных";}
                    using(var form=new MainForm(monitor,false,true,null)) {
                        form.Show();Application.DoEvents();form.RefreshStatus();Application.DoEvents();
                        using(var bitmap=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[1]);}
                        form.Close();
                    }
                }return 0;
            }
            bool created;
            using(var mutex=new Mutex(true,@"Local\CodexUsageSentinel.SingleInstance",out created))
            using(var show=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\CodexUsageSentinel.Show")) {
                if(!created){if(!args.Contains("--tray") && (!Startup.InStartupFolder || args.Contains("--show")))show.Set();return 0;}
                try {
                    var settings=Storage.Load<Settings>("settings.json");
                    using(var monitor=new Monitor(settings,Storage.Load<AlertState>("alerts.json")))
                    using(var form=new MainForm(monitor,StartInTray(args),false,show))
                    using(var context=new ApplicationContext()) {
                        form.FormClosed+=(s,e)=>context.ExitThread();
                        form.StartMonitoring();
                        if(!StartInTray(args))form.Show();
                        Application.Run(context);
                    }
                } catch {MessageBox.Show("Не удалось запустить Codex Usage Sentinel. Проверьте доступ к папке LocalAppData и наличие .NET Framework 4.8.","Codex Usage Sentinel");return 1;}
                finally{mutex.ReleaseMutex();}
            }return 0;
        }
    }
}
