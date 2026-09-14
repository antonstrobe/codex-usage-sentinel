using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexUsageSentinel {
    public sealed class GroupChoice {
        public long Id;public string Title;
        public override string ToString(){return Title+" · "+Id.ToString(CultureInfo.InvariantCulture);}
    }
    public static class GroupConnection {
        public static long Validate(object chat) {
            var id=Json.Number(Json.Get(chat,"id"));var type=Json.Str(Json.Get(chat,"type"));
            if((type!="group" && type!="supergroup") || !id.HasValue || id>=0 || id < -9007199254740991L || id!=Math.Truncate(id.Value))
                throw new TelegramFailure("Нужна группа или супергруппа с отрицательным Chat ID. Личный чат и канал не подходят.");
            return (long)id.Value;
        }
        static void RequireDirect(Settings settings) {
            if(settings.ConnectionMode!="direct" || string.IsNullOrEmpty(settings.TokenProtected))
                throw new InvalidOperationException("Для группы нужен бот с токеном, настроенным на этом компьютере. Подключение relay через Start обслуживает личный чат.");
        }
        public static async Task<List<GroupChoice>> Find(Telegram bot,Settings settings,CancellationToken ct) {
            RequireDirect(settings);string token=settings.Token();
            var hook=await bot.Call(token,"getWebhookInfo",new {},ct);
            if(Json.Str(Json.Get(hook,"url"))!="")throw new TelegramFailure("У бота включён webhook. Укажите ID группы вручную; webhook останется включён.");
            var updates=await bot.Call(token,"getUpdates",new {timeout=0,limit=100},ct) as IEnumerable;
            var groups=new Dictionary<long,GroupChoice>();
            if(updates!=null)foreach(var update in updates)foreach(string kind in new[]{"my_chat_member","message","edited_message"}) {
                var chat=Json.Get(Json.Get(update,kind),"chat");
                try{long id=Validate(chat);groups[id]=new GroupChoice {Id=id,Title=Json.Str(Json.Get(chat,"title"))};}catch(TelegramFailure){}
            }
            return groups.Values.OrderBy(g=>g.Title).ToList();
        }
        public static async Task<Settings> Connect(Telegram bot,Settings old,string idText,CancellationToken ct) {
            RequireDirect(old);long id;
            if(!long.TryParse(idText,NumberStyles.Integer,CultureInfo.InvariantCulture,out id) || id>=0 || id < -9007199254740991L)
                throw new TelegramFailure("Введите отрицательный числовой ID группы.");
            string token=old.Token();var me=await bot.Call(token,"getMe",new {},ct);
            var botId=Json.Number(Json.Get(me,"id"));
            if(!(Json.Get(me,"is_bot") is bool) || !(bool)Json.Get(me,"is_bot") || !botId.HasValue || botId<=0 || botId>9007199254740991L || botId!=Math.Truncate(botId.Value) ||
                !string.Equals(old.BotUsername,Json.Str(Json.Get(me,"username")),StringComparison.OrdinalIgnoreCase))
                throw new TelegramFailure("Сохранённый бот изменился. Проверьте его настройки перед подключением группы.");
            var chat=await bot.Call(token,"getChat",new {chat_id=id},ct);
            if(Validate(chat)!=id)throw new TelegramFailure("Telegram вернул другую группу. Настройки не изменены.");
            var member=await bot.Call(token,"getChatMember",new {chat_id=id,user_id=(long)botId.Value},ct);
            string status=Json.Str(Json.Get(member,"status"));
            bool administrator=status=="administrator" || status=="creator";
            bool ordinary=status=="member" && !object.Equals(Json.Get(Json.Get(chat,"permissions"),"can_send_messages"),false);
            bool restricted=status=="restricted" && object.Equals(Json.Get(member,"is_member"),true) && object.Equals(Json.Get(member,"can_send_messages"),true);
            if(!(administrator || ordinary || restricted))throw new TelegramFailure("Бот отсутствует в группе или ему запрещено отправлять сообщения. Разрешите отправку и повторите проверку.");
            var next=Json.Read<Settings>(Json.Write(old));
            next.GroupChatId=id;next.GroupTitle=Json.Str(Json.Get(chat,"title"));next.RecipientMode="group";
            return next;
        }
    }
    public sealed class GroupSetupForm : Form {
        readonly Monitor monitor;readonly CancellationTokenSource cancel=new CancellationTokenSource();
        TextBox chatId;ComboBox groups;Button find,save;Label status;
        public GroupSetupForm(Monitor monitor) {
            this.monitor=monitor;Text="Группа для уведомлений";BackColor=Theme.Bg;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);
            ClientSize=new Size(620,490);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;
            HandleCreated+=(s,e)=>Theme.DarkTitle(this);
            var flow=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24)};Controls.Add(flow);
            flow.Controls.Add(Theme.Label("Уведомления в группу",21,Theme.Text,FontStyle.Bold));
            flow.Controls.Add(new Label {Text="Добавьте настроенного бота в нужную группу. Выберите её ниже.\nСохранение включит отправку только в эту группу.",Size=new Size(565,50),ForeColor=Theme.Muted});
            find=Theme.Button("Найти группы",()=>Find());flow.Controls.Add(find);
            groups=new ComboBox {Width=565,DropDownStyle=ComboBoxStyle.DropDownList,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=25,BackColor=Theme.Panel,ForeColor=Theme.Text,AccessibleName="Найденные группы",Margin=new Padding(0,0,0,14)};
            groups.FlatStyle=FlatStyle.Flat;
            groups.DrawItem+=(s,e)=>{using(var brush=new SolidBrush((e.State&DrawItemState.Selected)!=0 ? Theme.ButtonBg : Theme.Panel))e.Graphics.FillRectangle(brush,e.Bounds);if(e.Index>=0)TextRenderer.DrawText(e.Graphics,groups.Items[e.Index].ToString(),groups.Font,e.Bounds,Theme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
            groups.SelectedIndexChanged+=(s,e)=>{var group=groups.SelectedItem as GroupChoice;if(group!=null)chatId.Text=group.Id.ToString(CultureInfo.InvariantCulture);};flow.Controls.Add(groups);
            flow.Controls.Add(Theme.Label("Chat ID группы · отрицательное число",10,Theme.Text));
            chatId=new TextBox {Width=565,Text=monitor.Settings.GroupChatId<0 ? monitor.Settings.GroupChatId.ToString(CultureInfo.InvariantCulture) : "",BackColor=Theme.Panel,ForeColor=Theme.Text,Font=new Font("Segoe UI",12),AccessibleName="ID группы для уведомлений",Margin=new Padding(0,0,0,14)};flow.Controls.Add(chatId);
            var row=new FlowLayoutPanel {Size=new Size(565,48)};
            save=Theme.Button("Проверить и выбрать группу",()=>Save(),true);row.Controls.Add(save);
            var close=Theme.Button("Отмена",()=>Close());close.DialogResult=DialogResult.Cancel;CancelButton=close;row.Controls.Add(close);flow.Controls.Add(row);
            status=new Label {Size=new Size(565,106),ForeColor=Theme.Muted,Text=monitor.Settings.GroupChatId<0 ? "Сохранена группа «"+monitor.Settings.GroupTitle+"». Личный чат сохранится для переключения обратно." : "Если список пуст, отправьте команду /start@имя_вашего_бота в группе и повторите поиск, либо введите ID. Сообщения при настройке не отправляются."};flow.Controls.Add(status);
            FormClosed+=(s,e)=>cancel.Cancel();
        }
        void Busy(bool value){find.Enabled=!value;save.Enabled=!value;chatId.ReadOnly=value;groups.Enabled=!value;}
        void Failure(Exception ex){if(!IsDisposed){status.Text=ex is TelegramFailure || ex is InvalidOperationException ? ex.Message : "Не удалось проверить группу. Попробуйте снова.";status.ForeColor=Theme.Red;}}
        async void Find(){Busy(true);status.Text="Ищу группы…";try{var list=await GroupConnection.Find(monitor.Bot,monitor.Settings,cancel.Token);groups.Items.Clear();foreach(var item in list)groups.Items.Add(item);status.Text=list.Count==0 ? "Группы не найдены. Отправьте в группе /start@"+monitor.Settings.BotUsername+" и повторите поиск, либо введите ID." : "Выберите нужную группу и нажмите «Проверить и выбрать группу».";status.ForeColor=Theme.Muted;}catch(OperationCanceledException){}catch(Exception ex){Failure(ex);}finally{if(!IsDisposed)Busy(false);}}
        async void Save(){Busy(true);status.Text="Проверяю группу и права бота…";try{var next=await GroupConnection.Connect(monitor.Bot,monitor.Settings,chatId.Text,cancel.Token);monitor.SetSettings(next);DialogResult=DialogResult.OK;Close();}catch(OperationCanceledException){}catch(Exception ex){Failure(ex);}finally{if(!IsDisposed)Busy(false);}}
    }
}
