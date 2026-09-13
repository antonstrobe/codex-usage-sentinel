using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexUsageSentinel {
    public sealed class RelaySetupForm : Form {
        readonly Monitor monitor;
        readonly RelayClient relay=new RelayClient();
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly ToolTip tips=Theme.Tooltips();
        TextBox username,device,endpoint;
        Button connect,open;
        Label status;
        Pairing pair;
        bool busy;
        public RelaySetupForm(Monitor monitor) {
            this.monitor=monitor;
            Text="Общий Telegram-бот · Codex Usage Sentinel";BackColor=Theme.Bg;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);
            ClientSize=new Size(620,570);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;
            HandleCreated+=(s,e)=>Theme.DarkTitle(this);
            var flow=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24)};Controls.Add(flow);
            flow.Controls.Add(Theme.Label("Подключить через Start",21,Theme.Text,FontStyle.Bold));
            flow.Controls.Add(new Label {Text="Введите свой username, откройте ссылку и нажмите Start в Telegram.\nПрограмма сама получит ваш личный Chat ID.",Size=new Size(570,51),ForeColor=Theme.Muted});
            username=Input(flow,"Ваш Telegram username",monitor.Settings.Username);
            device=Input(flow,"Имя этого компьютера в уведомлениях",Environment.MachineName);
            endpoint=Input(flow,"Адрес сервиса общего бота",monitor.Settings.RelayUrl=="" ? RelayClient.DefaultUrl : monitor.Settings.RelayUrl);
            connect=Theme.Button("Подключить через Start",()=>Begin(),true);flow.Controls.Add(connect);
            Theme.Describe(connect,tips,"Создать ссылку на 10 минут для вашего аккаунта и этого компьютера. Затем откройте ссылку и нажмите Start в Telegram.");
            open=Theme.Button("Открыть Telegram",()=>OpenTelegram());open.Enabled=false;flow.Controls.Add(open);
            Theme.Describe(open,tips,"Открыть текущую ссылку подключения в Telegram. Это разовое действие.");
            status=new Label {Size=new Size(570,66),ForeColor=Theme.Muted};flow.Controls.Add(status);
            bool linked=monitor.Settings.Ready && monitor.Settings.ConnectionMode=="relay";
            status.Text=linked ? "✓ Подключение включено · @"+monitor.Settings.Username+"\nДля отключения всех своих компьютеров отправьте боту /stop." : "○ Общий бот не подключён. Токен бота вводить не нужно.";
            if(linked)status.ForeColor=Theme.Green;
            var advanced=Theme.Button("Настроить своего бота…",()=>{if(busy)return;using(var own=new SetupForm(monitor))own.ShowDialog(this);if(monitor.Settings.Ready && monitor.Settings.ConnectionMode!="relay")Close();});flow.Controls.Add(advanced);
            Theme.Describe(advanced,tips,"Открыть отдельную настройку собственного бота по его токену. Токен общего бота для подключения через Start не нужен.");
            FormClosed+=(s,e)=>{cancel.Cancel();pair=null;};
        }
        static TextBox Input(FlowLayoutPanel flow,string caption,string value) {
            flow.Controls.Add(Theme.Label(caption,10,Theme.Text));
            var box=new TextBox {Width=565,Text=value,BackColor=Theme.Panel,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle,Font=new Font("Segoe UI",12),Margin=new Padding(0,0,0,14),AccessibleName=caption};flow.Controls.Add(box);return box;
        }
        void OpenTelegram() {
            if(pair==null)return;
            try{Process.Start(new ProcessStartInfo(pair.Url) {UseShellExecute=true});}
            catch{status.Text="Не удалось открыть Telegram. Откройте бота вручную и отправьте /start c_"+pair.Id;status.ForeColor=Theme.Red;}
        }
        async void Begin() {
            if(busy)return;busy=true;connect.Enabled=false;username.ReadOnly=true;device.ReadOnly=true;endpoint.ReadOnly=true;
            status.Text="Создаю ссылку подключения…";status.ForeColor=Theme.Muted;
            try {
                pair=await relay.Begin(endpoint.Text.Trim(),username.Text,device.Text,cancel.Token);
                open.Enabled=true;status.Text="Ожидаю Start от @"+pair.Username+" в @"+pair.BotUsername+".\nСсылка действует 10 минут. Это окно можно оставить открытым.";
                OpenTelegram();DateTime deadline=DateTime.UtcNow.AddMinutes(10);
                while(DateTime.UtcNow<deadline) {
                    await Task.Delay(2000,cancel.Token);
                    Settings settings=null;
                    try{settings=await relay.Finish(endpoint.Text.Trim(),pair,monitor.Settings,cancel.Token);}
                    catch(HttpRequestException){status.Text="Нет связи с сервисом. Повторяю проверку подключения…";continue;}
                    if(settings==null)continue;
                    monitor.SetSettings(settings);status.Text="✓ Подключение включено · @"+settings.Username+"\nКомпьютер получает отдельный доступ только к вашему личному чату.";status.ForeColor=Theme.Green;
                    connect.Text="Подключить заново через Start";return;
                }
                status.Text="Ссылка истекла. Нажмите «Подключить через Start» ещё раз.";
            } catch(OperationCanceledException) {}
            catch(Exception ex) {if(!IsDisposed){status.Text=ex is TelegramFailure || ex is InvalidOperationException ? ex.Message : "Не удалось подключиться к сервису. Проверьте интернет и адрес.";status.ForeColor=Theme.Red;}}
            finally {busy=false;pair=null;if(!IsDisposed){connect.Enabled=true;open.Enabled=false;username.ReadOnly=false;device.ReadOnly=false;endpoint.ReadOnly=false;}}
        }
        protected override void Dispose(bool disposing) {if(disposing){cancel.Cancel();relay.Dispose();tips.Dispose();}base.Dispose(disposing);}
    }
}
