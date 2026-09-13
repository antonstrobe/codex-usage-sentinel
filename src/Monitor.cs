using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageSentinel {
    public sealed class TelegramFailure : Exception {
        public int RetrySeconds;
        public bool RateLimited;
        public TelegramFailure(string message,int retry=10) : base(message) { RetrySeconds=retry; }
    }
    public sealed class Telegram : IDisposable {
        readonly HttpClient http;
        readonly RelayClient relay;
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1,1);
        DateTime nextSendUtc=DateTime.MinValue;
        public Telegram(HttpMessageHandler handler=null,HttpMessageHandler relayHandler=null) { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; http=new HttpClient(handler??new HttpClientHandler {AllowAutoRedirect=false});http.Timeout=TimeSpan.FromSeconds(15);relay=new RelayClient(relayHandler); }
        public static int RetryDelay(object response) {
            double n=Json.Number(Json.Get(Json.Get(response,"parameters"),"retry_after")) ?? 10;
            return (int)Math.Min(86400,Math.Max(2,n+1));
        }
        public async Task<object> Call(string token,string method,object args,CancellationToken ct) {
            if(!Regex.IsMatch(token??"",@"^\d{6,12}:[A-Za-z0-9_-]{30,50}$")) throw new TelegramFailure("Некорректный формат токена Telegram.",60);
            try {
                using(var content=new StringContent(Json.Write(args),Encoding.UTF8,"application/json"))
                using(var response=await http.PostAsync("https://api.telegram.org/bot"+token+"/"+method,content,ct)) {
                    object data;
                    try {data=Json.Read<object>(await response.Content.ReadAsStringAsync());} catch {throw new TelegramFailure("Telegram вернул нечитаемый ответ.");}
                    if(Json.Get(data,"ok") is bool && (bool)Json.Get(data,"ok")) return Json.Get(data,"result");
                    int code=(int)(Json.Number(Json.Get(data,"error_code"))??(int)response.StatusCode);
                    if(code==429) throw new TelegramFailure("Telegram просит снизить частоту. Ожидание перед повтором.",RetryDelay(data));
                    if(code==409) throw new TelegramFailure("Бот уже получает сообщения в другой программе. Укажите личный Chat ID вручную.",60);
                    if(code==401) throw new TelegramFailure("Telegram отклонил токен. Обновите его в настройках.",60);
                    if(code==403) throw new TelegramFailure("Бот не может написать вам. Откройте бота, нажмите Start и снимите блокировку.",60);
                    if(code==400 && (method=="getChat" || method=="sendMessage")) throw new TelegramFailure("Личный чат недоступен. Откройте вашего бота, нажмите Start и проверьте личный Chat ID.",60);
                    throw new TelegramFailure("Ошибка Telegram ("+code+"). Проверьте токен и личный Chat ID.",15);
                }
            } catch(OperationCanceledException) {ct.ThrowIfCancellationRequested(); throw new TelegramFailure("Telegram не ответил за 15 секунд.");}
            catch(HttpRequestException) {throw new TelegramFailure("Нет соединения с Telegram. Проверьте интернет.");}
        }
        public static long PrivateRecipient(object chat,string expected) {
            var id=Json.Number(Json.Get(chat,"id"));
            if(Json.Str(Json.Get(chat,"type"))!="private" || !id.HasValue || id<=0 || id>9007199254740991L || id!=Math.Truncate(id.Value) ||
                (!string.IsNullOrEmpty(expected) && !string.Equals(Json.Str(Json.Get(chat,"username")),expected.TrimStart('@'),StringComparison.OrdinalIgnoreCase)))
                throw new TelegramFailure("Получатель не подтверждён: нужен личный чат"+(string.IsNullOrEmpty(expected) ? "." : " @"+expected+"."),60);
            return (long)id.Value;
        }
        public async Task<Settings> Connect(Settings old,string token,string idText,CancellationToken ct) {
            return await Connect(old,token,idText,old.Username,ct);
        }
        public async Task<Settings> Connect(Settings old,string token,string idText,string username,CancellationToken ct) {
            username=(username??"").Trim().TrimStart('@');
            if(username!="" && !Regex.IsMatch(username,@"^[A-Za-z0-9_]{5,32}$"))throw new TelegramFailure("Проверьте ваш Telegram username: только латинские буквы, цифры и подчёркивания.");
            if(string.IsNullOrWhiteSpace(token)) token=old.Token();
            token=token.Trim();
            var me=await Call(token,"getMe",new {},ct);
            string botUsername=Json.Str(Json.Get(me,"username"));
            if(!(Json.Get(me,"is_bot") is bool) || !(bool)Json.Get(me,"is_bot") || !Regex.IsMatch(botUsername,@"^[A-Za-z0-9_]{5,32}$"))
                throw new TelegramFailure("Telegram не подтвердил бота. Проверьте токен.",60);
            long id=string.Equals(username,old.Username,StringComparison.OrdinalIgnoreCase) ? old.ChatId : 0;
            if(!string.IsNullOrWhiteSpace(idText)) {
                if(!long.TryParse(idText,out id) || id<=0) throw new TelegramFailure("Нужен числовой ID личного чата, больше нуля.");
            }
            if(id>0) {
                var chat=await Call(token,"getChat",new {chat_id=id},ct);
                if(PrivateRecipient(chat,username)!=id) throw new TelegramFailure("Личный Chat ID не совпал.");
            } else {
                if(username=="")throw new TelegramFailure("Укажите ваш Telegram username для поиска или числовой ID личного чата.");
                var hook=await Call(token,"getWebhookInfo",new {},ct);
                if(Json.Str(Json.Get(hook,"url"))!="") throw new TelegramFailure("У бота включён webhook аудита. Укажите личный Chat ID из настроек аудита; webhook останется включён.",60);
                // Read pending updates without an offset or allowed_updates: do not acknowledge or change an existing audit integration.
                var updates=await Call(token,"getUpdates",new {timeout=0,limit=100},ct) as IEnumerable;
                if(updates!=null) foreach(var update in updates) {
                    var message=Json.Get(update,"message");
                    var chat=Json.Get(message,"chat");
                    if(string.Equals(Json.Str(Json.Get(chat,"username")),username,StringComparison.OrdinalIgnoreCase) &&
                        Json.Str(Json.Get(Json.Get(message,"from"),"id"))==Json.Str(Json.Get(chat,"id"))) {
                        try {id=PrivateRecipient(chat,username);} catch(TelegramFailure) {}
                    }
                }
                if(id==0) throw new TelegramFailure("Личный чат пока не найден. Напишите боту любое сообщение и повторите подключение, либо укажите свой числовой Chat ID.",30);
            }
            var settings=new Settings {ChatId=id,Username=username,BotUsername=botUsername,CodexPath=old.CodexPath,PausedUntilUtc=old.PausedUntilUtc};
            settings.SetToken(token);
            return settings;
        }
        public async Task<bool> Send(Settings settings,string text,Func<bool> stillNeeded,CancellationToken ct) {
            await sendGate.WaitAsync(ct);
            try {
                TimeSpan wait=nextSendUtc-DateTime.UtcNow;
                if(wait>TimeSpan.Zero) await Task.Delay(wait,ct);
                if(!stillNeeded()) return false;
                if(!settings.Ready) throw new TelegramFailure("Сначала подключите личный Telegram-чат.");
                try {
                    if(settings.ConnectionMode=="relay") {
                        bool delivered=await relay.Send(settings,text,stillNeeded,ct);
                        nextSendUtc=DateTime.UtcNow.AddSeconds(2);return delivered;
                    }
                    await Call(settings.Token(),"sendMessage",new {chat_id=settings.ChatId,text=text,disable_notification=false},ct);
                    nextSendUtc=DateTime.UtcNow.AddSeconds(2);
                    return true;
                } catch(TelegramFailure ex) {nextSendUtc=DateTime.UtcNow.AddSeconds(ex.RetrySeconds);throw;}
            } finally {sendGate.Release();}
        }
        public void Dispose() {http.Dispose();relay.Dispose();}
    }
    public sealed class Monitor : IDisposable {
        readonly object gate=new object();
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly SemaphoreSlim checkSignal=new SemaphoreSlim(0,1);
        readonly AlertPolicy policy;
        public readonly Telegram Bot=new Telegram();
        public Settings Settings;
        public Usage Latest;
        public string ReadStatus="Ожидание первой проверки", TelegramStatus="Telegram ещё не подключён", StorageStatus="";
        public DateTime LastAttemptUtc, NextCheckUtc;
        public bool Fresh;
        public int SentCount;
        public event Action Changed;
        int failures=0;
        bool outagePending=false, outageSent=false;
        long outageVersion=0;
        public Monitor(Settings settings,AlertState state) {Settings=settings;policy=new AlertPolicy(state);if(settings.Ready)TelegramStatus="Подключён · ожидание порога";}
        public void Start() {Task.Run((Func<Task>)PollLoop);Task.Run((Func<Task>)SendLoop);}
        void Notify() {var handler=Changed;if(handler!=null)handler();}
        void Persist() {
            try {Storage.Save("alerts.json",policy.State);StorageStatus="";} catch {StorageStatus="Не удалось сохранить очередь уведомлений. При перезапуске возможны повторы.";}
        }
        public void SetSettings(Settings settings) {lock(gate){Storage.Save("settings.json",settings);Settings=settings;if(settings.Ready)TelegramStatus="Подключён · ожидание порога";}Notify();}
        public bool Paused {
            get {DateTime until;return DateTime.TryParse(Settings.PausedUntilUtc,null,DateTimeStyles.RoundtripKind,out until) && until.ToUniversalTime()>DateTime.UtcNow;}
        }
        public void Pause() {
            lock(gate) {Settings.PausedUntilUtc=Paused ? "" : DateTime.UtcNow.AddMinutes(30).ToString("o");Storage.Save("settings.json",Settings);} Notify();
        }
        public void CheckNow() {if(checkSignal.CurrentCount==0)try{checkSignal.Release();}catch(SemaphoreFullException){}}
        public async Task PollOnce() {
            LastAttemptUtc=DateTime.UtcNow;ReadStatus="Проверяю лимиты…";Notify();
            try {
                Usage usage=await CodexClient.Read(Settings.CodexPath,cancel.Token);
                lock(gate) {
                    Latest=usage;Fresh=true;failures=0;outagePending=false;outageSent=false;outageVersion++;
                    policy.Update(usage);Persist();ReadStatus="Лимиты получены · обновление каждую минуту";
                }
            } catch(OperationCanceledException) {return;}
            catch(Exception ex) {
                lock(gate) {
                    Fresh=false;failures++;
                    ReadStatus=ex is InvalidOperationException ? ex.Message : "Ошибка чтения Codex. Повторная попытка через минуту.";
                    if(failures>=3 && !outageSent) {outagePending=true;outageVersion++;}
                }
            }
            Notify();
        }
        async Task PollLoop() {
            try {
                while(!cancel.IsCancellationRequested) {
                    DateTime started=DateTime.UtcNow;
                    NextCheckUtc=started.AddSeconds(60);
                    await PollOnce();
                    int wait=(int)Math.Max(0,(NextCheckUtc-DateTime.UtcNow).TotalMilliseconds);
                    await checkSignal.WaitAsync(wait,cancel.Token);
                }
            } catch(OperationCanceledException) {}
        }
        string Message(AlertItem item,Usage usage) {
            var w=usage.Core.First(x=>x.Key==item.Key);
            return (item.Continuous ? "🚨 КРИТИЧЕСКИЙ ЛИМИТ CODEX" : "⚠️ НИЗКИЙ ЛИМИТ CODEX")+
                "\n"+w.Label+": осталось "+w.Remaining.ToString("0.#",CultureInfo.InvariantCulture)+"%."+
                "\nПодготовьте или примените доступный сброс в Codex.\n\n"+usage.Description()+
                (item.Continuous ? "\n\nПовтор каждые 2 секунды до восстановления лимита или паузы в программе." : "\n\nПорог "+item.Stage+"%. Серия "+(item.Stage==3 ? "50" : "10")+" сообщений; интервал 2 секунды.");
        }
        async Task SendLoop() {
            try {
                while(!cancel.IsCancellationRequested) {
                    AlertItem item=null;Usage snapshot=null;Settings settings;bool outage;long version;
                    lock(gate) {
                        settings=Settings;outage=outagePending;version=outageVersion;
                        if(!Paused && settings.Ready && Fresh) {item=policy.Next(DateTime.UtcNow);snapshot=Latest;}
                    }
                    if(!settings.Ready || Paused || (item==null && !outage)) {await Task.Delay(300,cancel.Token);continue;}
                    int retryWait=0;
                    try {
                        bool sent;
                        if(item!=null) {
                            var currentItem=item;
                            sent=await Bot.Send(settings,Message(item,snapshot),()=>{lock(gate) return !Paused && Fresh && policy.IsCurrent(currentItem,DateTime.UtcNow) && settings==Settings;},cancel.Token);
                            if(sent) lock(gate) {policy.Acknowledge(item);SentCount++;Persist();}
                        } else {
                            sent=await Bot.Send(settings,"⚠️ Codex Usage Sentinel: три проверки подряд не удалось получить лимиты. Состояние неизвестно. Проверьте Codex и интернет на компьютере. Повторяющиеся сообщения о процентах приостановлены до получения свежих данных.",()=>{lock(gate)return !Paused && outagePending && outageVersion==version && settings==Settings;},cancel.Token);
                            if(sent) lock(gate) {outagePending=false;outageSent=true;SentCount++;}
                        }
                        if(sent) {TelegramStatus="Доставлено в Telegram · "+DateTime.Now.ToString("HH:mm:ss");Notify();}
                    } catch(DeliveryUncertain ex) {
                        lock(gate){if(item!=null){policy.Acknowledge(item);Persist();}else{outagePending=false;outageSent=true;}}
                        TelegramStatus=ex.Message;Notify();retryWait=10;
                    }
                    catch(TelegramFailure ex) {TelegramStatus=ex.Message;Notify();retryWait=ex.RetrySeconds;}
                    catch(OperationCanceledException) {throw;}
                    catch(Exception) {TelegramStatus="Ошибка отправки. Проверьте настройки Telegram.";Notify();retryWait=10;}
                    if(retryWait>0)await Task.Delay(TimeSpan.FromSeconds(retryWait),cancel.Token);
                }
            } catch(OperationCanceledException) {}
        }
        public async Task TestMessage(int number=1,int total=1) {
            Settings settings=Settings;
            bool sent=await Bot.Send(settings,"🔔 ТЕСТ "+number+"/"+total+" · Codex Usage Sentinel\nСообщение отправлено самой программой на вашем компьютере.\n"+
                (Latest!=null && Fresh ? Latest.Description() : "Свежие лимиты пока не получены.")+"\nТестовая серия уведомлений; интервал не менее 2 секунд.",()=>settings==Settings,cancel.Token);
            if(!sent)throw new InvalidOperationException("Настройки изменились. Тестовая серия остановлена.");
            TelegramStatus="Тест "+number+"/"+total+" доставлен · "+DateTime.Now.ToString("HH:mm:ss");SentCount++;Notify();
        }
        public CancellationToken Token {get{return cancel.Token;}}
        public void Dispose() {cancel.Cancel();}
    }
}
