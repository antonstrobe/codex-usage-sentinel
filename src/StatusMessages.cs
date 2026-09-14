using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageSentinel {
    public sealed class StatusMessageRecord {
        public long MessageId;
        public string Text="",UpdatedUtc="";
        public bool Creating;
        public List<long> Obsolete=new List<long>();
    }
    public sealed class StatusMessageState {
        public Dictionary<string,StatusMessageRecord> Messages=new Dictionary<string,StatusMessageRecord>();
    }
    public sealed partial class Telegram {
        public async Task<long> WriteStatus(Settings settings,long messageId,string text,Func<bool> stillNeeded,CancellationToken ct) {
            await sendGate.WaitAsync(ct);
            try {
                var wait=nextSendUtc-DateTime.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait,ct);
                if(!stillNeeded())return 0;
                if(!settings.Ready || settings.ConnectionMode!="direct")throw new InvalidOperationException("Тихий статус требует прямого подключения бота.");
                try {
                    object reply=messageId>0 ? await Call(settings.Token(),"editMessageText",new {chat_id=settings.TargetChatId,message_id=messageId,text=text},ct) :
                        await Call(settings.Token(),"sendMessage",new {chat_id=settings.TargetChatId,text=text,disable_notification=true},ct);
                    nextSendUtc=DateTime.UtcNow.AddSeconds(2);
                    if(messageId>0 && object.Equals(Json.Get(reply,"unchanged"),true))return messageId;
                    var id=Json.Number(Json.Get(reply,"message_id"));var chat=Json.Number(Json.Get(Json.Get(reply,"chat"),"id"));
                    if(!id.HasValue || id<=0 || id>int.MaxValue || id!=Math.Truncate(id.Value) || chat!=settings.TargetChatId || (messageId>0 && id!=messageId))
                        throw new InvalidOperationException("Telegram не подтвердил ID тихого сообщения.");
                    return (long)id.Value;
                }catch(TelegramFailure ex){if(!ex.MessageMissing)nextSendUtc=DateTime.UtcNow.AddSeconds(ex.RetrySeconds);throw;}
            }finally{sendGate.Release();}
        }
        public async Task<bool> DeleteStatus(Settings settings,long messageId,Func<bool> stillNeeded,CancellationToken ct) {
            await sendGate.WaitAsync(ct);
            try {
                if(!stillNeeded())return false;
                await Call(settings.Token(),"deleteMessage",new {chat_id=settings.TargetChatId,message_id=messageId},ct);return true;
            }catch(TelegramFailure ex){if(ex.RateLimited)nextSendUtc=DateTime.UtcNow.AddSeconds(ex.RetrySeconds);throw;}
            finally{sendGate.Release();}
        }
    }
    public sealed class StatusPublisher {
        readonly StatusMessageState state;readonly Action<StatusMessageState> save;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        public StatusPublisher(StatusMessageState state,Action<StatusMessageState> save){this.state=state??new StatusMessageState();this.save=save;if(this.state.Messages==null)this.state.Messages=new Dictionary<string,StatusMessageRecord>();}
        public static string Key(Settings settings){return settings.Token().Split(':')[0]+":"+settings.TargetChatId.ToString(CultureInfo.InvariantCulture);}
        public static string Format(Usage usage,bool fresh,bool paused,DateTime now) {
            bool current=fresh && usage!=null && now-usage.CheckedUtc<=TimeSpan.FromSeconds(90);
            return current ? usage.Remaining.ToString("0.#",CultureInfo.InvariantCulture)+"%" : "—%";
        }
        public async Task<string> Update(Telegram bot,Settings settings,string text,bool moveToBottom,bool recover,Func<bool> stillNeeded,CancellationToken ct) {
            await gate.WaitAsync(ct);
            try {
                if(!settings.LiveStatusEnabled || !settings.Ready || settings.ConnectionMode!="direct" || !stillNeeded())return null;
                string key=Key(settings);StatusMessageRecord record;
                if(!state.Messages.TryGetValue(key,out record) || record==null){record=new StatusMessageRecord();state.Messages[key]=record;}
                if(record.Obsolete==null)record.Obsolete=new List<long>();
                if(recover){record.Creating=false;moveToBottom=true;save(state);}
                if(record.Creating)throw new InvalidOperationException("Создание тихого статуса не подтверждено. Чтобы избежать дублей, выберите «Создать статус заново» через правую кнопку переключателя.");
                for(int attempt=0;attempt<2;attempt++) {
                    bool create=record.MessageId<=0 || moveToBottom;
                    if(!create && record.Text==text)break;
                    if(!stillNeeded())return null;
                    long previous=record.MessageId;
                    if(create){record.Creating=true;save(state);}
                    try {
                        long result=await bot.WriteStatus(settings,create ? 0 : previous,text,stillNeeded,ct);
                        if(result==0){if(create){record.Creating=false;save(state);}return null;}
                        if(create && previous>0 && previous!=result && !record.Obsolete.Contains(previous))record.Obsolete.Insert(0,previous);
                        record.MessageId=result;record.Text=text;record.UpdatedUtc=DateTime.UtcNow.ToString("o");record.Creating=false;
                        save(state);break;
                    }catch(TelegramFailure ex){
                        if(create && ex.ErrorCode>=400 && ex.ErrorCode<500){record.Creating=false;save(state);}
                        if(!create && ex.MessageMissing){record.MessageId=0;record.Text="";save(state);continue;}
                        throw;
                    }
                }
                // Only IDs of earlier status messages created by this publisher are removed; alarms are never stored here.
                bool cleanupFailed=false;
                foreach(long obsolete in record.Obsolete.Take(3).ToArray()) {
                    if(obsolete<=0 || obsolete==record.MessageId){record.Obsolete.Remove(obsolete);continue;}
                    try{if(!await bot.DeleteStatus(settings,obsolete,stillNeeded,ct))break;record.Obsolete.Remove(obsolete);save(state);}
                    catch(TelegramFailure){cleanupFailed=true;break;}
                }
                return cleanupFailed ? "Тихий статус обновлён; старый статус пока не удалось удалить" : "Тихий статус обновлён · "+DateTime.Now.ToString("HH:mm:ss");
            }finally{gate.Release();}
        }
    }
    public sealed partial class Monitor {
        readonly SemaphoreSlim publishStatusGate=new SemaphoreSlim(1,1);
        DateTime statusRetryUtc=DateTime.MinValue;
        async Task PublishStatus(Settings settings,bool moveToBottom,bool recover) {
            if(!settings.LiveStatusEnabled || !settings.Ready || settings.ConnectionMode!="direct" || (!recover && DateTime.UtcNow<statusRetryUtc))return;
            await publishStatusGate.WaitAsync(cancel.Token);
            try {
            if(settings!=Settings || !settings.LiveStatusEnabled)return;
            Usage usage;bool fresh,paused;int revision,bottomVersion;
            lock(gate){usage=Latest;fresh=Fresh;paused=Paused;revision=statusRevision;bottomVersion=statusBottomVersion;moveToBottom=recover || bottomVersion>statusBottomHandled;}
            try {
                string result=await liveStatus.Update(Bot,settings,StatusPublisher.Format(usage,fresh,paused,DateTime.UtcNow),moveToBottom,recover,
                    ()=>{lock(gate)return settings==Settings && settings.LiveStatusEnabled && revision==statusRevision;},cancel.Token);
                if(result!=null){LiveStatusText=result;statusRetryUtc=DateTime.MinValue;if(moveToBottom)statusBottomHandled=bottomVersion;Notify();}
            }catch(OperationCanceledException){}
            catch(Exception ex){LiveStatusText=ex is TelegramFailure || ex is InvalidOperationException ? ex.Message : "Не удалось обновить или сохранить тихий статус";statusRetryUtc=DateTime.UtcNow.AddSeconds(Math.Max(60,ex is TelegramFailure ? ((TelegramFailure)ex).RetrySeconds : 60));Notify();}
            }finally{publishStatusGate.Release();}
        }
        async Task StatusLoop() {
            int seen=-1;DateTime next=DateTime.MinValue;
            try {while(!cancel.IsCancellationRequested) {
                Settings settings=Settings;int revision=statusRevision;
                if(!settings.LiveStatusEnabled)LiveStatusText="Тихий статус выключен";
                else if(settings.ConnectionMode!="direct")LiveStatusText="Тихий статус доступен при прямом подключении бота";
                else if(!settings.Ready)LiveStatusText="Подключите выбранный чат для тихого статуса";
                else if(revision>0 && (revision!=seen || DateTime.UtcNow>=next)) {
                    bool recover=Interlocked.Exchange(ref recreateStatus,0)!=0;
                    await PublishStatus(settings,recover,recover);seen=revision;next=DateTime.UtcNow.AddSeconds(60);
                }
                await Task.Delay(1000,cancel.Token);
            }}catch(OperationCanceledException){}
        }
    }
}
