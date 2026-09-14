using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageSentinel {
    public class RelayTransport : HttpMessageHandler {
        public List<string> Bodies=new List<string>(),Paths=new List<string>();
        public Func<int,HttpResponseMessage> Reply;
        public bool HadAuth;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Paths.Add(request.RequestUri.AbsolutePath);Bodies.Add(request.Content==null ? "" : await request.Content.ReadAsStringAsync());
            HadAuth=request.Headers.Authorization!=null && request.Headers.Authorization.Scheme=="Bearer";
            return Reply(Paths.Count);
        }
        public static HttpResponseMessage Result(object body,int code=200) {return new HttpResponseMessage((HttpStatusCode)code) {Content=new StringContent(Json.Write(body),Encoding.UTF8,"application/json")};}
    }
    public class FakeTransport : HttpMessageHandler {
        public List<string> Methods=new List<string>(),Bodies=new List<string>();
        public Func<string,string> Reply;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            string method=request.RequestUri.Segments.Last();Methods.Add(method);Bodies.Add(await request.Content.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(Reply(method),Encoding.UTF8,"application/json")};
        }
    }
    public static class Tests {
        static int passed,failed;
        static readonly DateTime Now=DateTime.UtcNow;
        static object Window(double? used,int duration=10080,long reset=2000000000) {return new {usedPercent=used,windowDurationMins=duration,resetsAt=reset};}
        static Usage UsageAt(double remaining,double? other=null,long reset=2000000000) {
            return Usage.Parse(Json.Read<object>(Json.Write(new {accountId="account-a",rateLimitsByLimitId=new Dictionary<string,object> {{"codex",new {primary=Window(100-remaining,10080,reset),secondary=other.HasValue ? Window(100-other.Value,300,reset) : null}}},rateLimitResetCredits=new {availableCount=1}})),Now);
        }
        static AlertPolicy Policy() {return new AlertPolicy(new AlertState());}
        static void Check(bool condition,string name) {if(!condition)throw new Exception(name);}
        static void Test(string name,Action action) {try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.GetType().Name);}}
        static void Throws(Action action) {bool threw=false;try{action();}catch{threw=true;}Check(threw,"must reject");}
        static int Drain(AlertPolicy policy) {int n=0;while(policy.Next(Now)!=null && n<1000){var item=policy.Next(Now);if(item.Continuous)break;policy.Acknowledge(item);n++;}return n;}
        static Settings TestSettings() {var settings=new Settings {ChatId=123,Username="example_user",BotUsername="ExampleOld_bot"};settings.SetToken("123456:"+new string('a',35));return settings;}
        static string Ok(object result) {return Json.Write(new {ok=true,result=result});}
        static Settings StatusSettings(){var s=TestSettings();s.LiveStatusEnabled=true;s.RecipientMode="group";s.GroupChatId=-999;return s;}
        static FakeTransport StatusTransport(){long id=100;var fake=new FakeTransport();fake.Reply=m=>{var body=Json.Read<object>(fake.Bodies.Last());return m=="deleteMessage" ? Ok(true) : Ok(new {message_id=m=="editMessageText" ? (long)Json.Number(Json.Get(body,"message_id")).Value : id++,chat=new {id=(long)Json.Number(Json.Get(body,"chat_id")).Value}});};return fake;}
        static AlarmRule Rule(string id="example-alarm",int percent=30,int count=7,int interval=15,bool continuous=false) {return new AlarmRule {Id=id,Percent=percent,MessageCount=count,IntervalSeconds=interval,Continuous=continuous};}
        [STAThread] public static int Main(string[] args) {
            if(args.Length==2 && (args[0]=="--render-alarms" || args[0]=="--render-alarm-editor" || args[0]=="--render-main" || args[0]=="--render-group")) {
                System.Windows.Forms.Application.EnableVisualStyles();
                using(var monitor=new Monitor(new Settings(),new AlertState()))
                using(var form=args[0]=="--render-group" ? (System.Windows.Forms.Form)new GroupSetupForm(monitor) : args[0]=="--render-alarms" ? (System.Windows.Forms.Form)new AlarmsForm(monitor) : args[0]=="--render-main" ? (System.Windows.Forms.Form)new MainForm(monitor,false,true,null) : new AlarmEditorForm(Rule(interval:60),true)) {
                    form.Show();System.Windows.Forms.Application.DoEvents();
                    using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size));bitmap.Save(args[1]);}
                    form.Close();
                }return 0;
            }
            if(args.Length==2 && args[0]=="--render-relay") {
                System.Windows.Forms.Application.EnableVisualStyles();
                using(var monitor=new Monitor(new Settings(),new AlertState()))
                using(var form=new RelaySetupForm(monitor)) {
                    form.Show();System.Windows.Forms.Application.DoEvents();
                    using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {
                        form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size));bitmap.Save(args[1]);
                    }
                    form.Close();
                }
                return 0;
            }
            Test("old settings migrate to four default alarms and empty list stays empty",()=>{
                Check(Json.Read<Settings>("{\"Version\":1}").Alarms.Count==4,"defaults");
                var settings=Json.Read<Settings>("{\"Alarms\":[]}");var p=new AlertPolicy(new AlertState(),settings.Alarms);p.Update(UsageAt(0));Check(p.Next(Now)==null,"explicit empty");
            });
            Test("custom threshold and exact message count",()=>{
                var p=new AlertPolicy(new AlertState(),new[]{Rule()});p.Update(UsageAt(31));Check(p.Next(Now)==null,"above threshold");p.Update(UsageAt(30));Check(Drain(p)==7,"custom count");p.Update(UsageAt(29));Check(p.Next(Now)==null,"once per crossing");
            });
            Test("message interval persists across restart",()=>{
                var rules=new[]{Rule(interval:120)};var p=new AlertPolicy(new AlertState(),rules);p.Update(UsageAt(30));var item=p.Next(Now);Check(item.Due(Now),"first immediate");p.Acknowledge(item,Now);
                var resumed=new AlertPolicy(Json.Read<AlertState>(Json.Write(p.State)),rules);var usage=UsageAt(29);usage.CheckedUtc=Now.AddSeconds(60);resumed.Update(usage);
                Check(!resumed.Next(Now.AddSeconds(119)).Due(Now.AddSeconds(119)),"no early send");Check(resumed.Next(Now.AddSeconds(120)).Due(Now.AddSeconds(120)),"due after interval");
            });
            Test("delete or disable cancels an item already waiting to send",()=>{
                var rules=new[]{Rule()};var p=new AlertPolicy(new AlertState(),rules);p.Update(UsageAt(20));var item=p.Next(Now);
                var disabled=AlarmRule.CheckedCopy(rules);disabled[0].Enabled=false;p.SetRules(disabled);Check(!p.IsCurrent(item,Now) && p.Next(Now)==null,"disabled immediately");
                p.SetRules(rules);Check(Drain(p)==7,"reenabled");p.Update(UsageAt(90));p.Update(UsageAt(20));item=p.Next(Now);p.SetRules(new AlarmRule[0]);Check(!p.IsCurrent(item,Now) && p.Next(Now)==null,"deleted immediately");
            });
            Test("editing interval or count starts new revision without accepting old ack",()=>{
                var p=new AlertPolicy(new AlertState(),new[]{Rule()});p.Update(UsageAt(20));var old=p.Next(Now);p.SetRules(new[]{Rule(count:4,interval:30)});p.Acknowledge(old,Now);
                Check(!p.IsCurrent(old,Now) && p.Next(Now).IntervalSeconds==30 && Drain(p)==4,"new configuration");
            });
            Test("unrelated alarm edit preserves completed series",()=>{
                var p=new AlertPolicy(new AlertState(),new[]{Rule("first",30,7),Rule("second",10,5)});p.Update(UsageAt(25));Check(Drain(p)==7,"first sent");
                p.SetRules(new[]{Rule("first",30,7),Rule("second",8,2)});Check(p.Next(Now)==null,"first not repeated");p.Update(UsageAt(8));Check(Drain(p)==2,"edited second");
            });
            Test("custom escalation cancels a higher series even during its interval",()=>{
                var p=new AlertPolicy(new AlertState(),new[]{Rule("first",40,100,3600),Rule("second",25,3,2)});p.Update(UsageAt(40));var item=p.Next(Now);p.Acknowledge(item,Now);p.Update(UsageAt(25));
                Check(!p.IsCurrent(item,Now) && p.Next(Now).Due(Now) && Drain(p)==3,"urgent series immediate");
            });
            Test("custom continuous rule has its own interval and stops on recovery",()=>{
                var p=new AlertPolicy(new AlertState(),new[]{Rule(percent:12,interval:60,continuous:true)});p.Update(UsageAt(12));var a=p.Next(Now);Check(a.Continuous,"continuous");p.Acknowledge(a,Now);
                Check(!p.Next(Now).Due(Now.AddSeconds(59)) && p.Next(Now).Due(Now.AddSeconds(60)),"own interval");p.Update(UsageAt(13));Check(p.Next(Now)==null,"recovered");p.Update(UsageAt(12));Check(p.Next(Now).Due(Now),"rearmed immediately");
            });
            Test("same threshold supports separate alarms",()=>{
                var p=new AlertPolicy(new AlertState(),new[]{Rule("first",20,3),Rule("second",20,4)});p.Update(UsageAt(20));Check(Drain(p)==7,"both rules");
            });
            Test("legacy pending series and fired thresholds migrate without duplication",()=>{
                var usage=UsageAt(3);var state=new AlertState();state.Windows[usage.Core[0].Key]=new WindowState {Fired10=true,Fired5=true,Fired3=true,Stage=3,Pending=43,Generation=4};
                var p=new AlertPolicy(state);p.Update(usage);Check(state.Version==2 && Drain(p)==43,"remaining series");p.Update(UsageAt(4));Check(p.Next(Now)==null,"old 5 percent already fired");
            });
            Test("alarm validation rejects invalid ranges and duplicate ids",()=>{
                foreach(var bad in new[]{Rule(percent:-1),Rule(percent:101),Rule(count:0),Rule(count:10001),Rule(interval:1),Rule(interval:86401)})Throws(()=>AlarmRule.CheckedCopy(new[]{bad}));
                Throws(()=>AlarmRule.CheckedCopy(new[]{Rule(),Rule()}));Check(AlarmRule.CheckedCopy(new[]{Rule(percent:0,interval:86400)}).Count==1,"boundary values valid");
            });
            Test("alarm settings save atomically and retain other settings",()=>{
                string previousRoot=Storage.Root;string folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"sentinel-alarm-test-"+Guid.NewGuid().ToString("N"));Storage.Root=folder;
                try {
                    var settings=TestSettings();settings.CodexPath="example-codex.exe";
                    using(var monitor=new Monitor(settings,new AlertState())) {
                        monitor.SetAlarms(new[]{Rule()});var saved=Storage.Load<Settings>("settings.json");Check(saved.Alarms.Count==1 && saved.Alarms[0].MessageCount==7 && saved.Token()==settings.Token() && saved.CodexPath==settings.CodexPath,"saved and preserved");
                        var snapshot=monitor.Settings;Storage.Root=System.IO.Path.Combine(folder,"settings.json");Throws(()=>monitor.SetAlarms(new AlarmRule[0]));Check(monitor.Settings==snapshot && monitor.AlarmRules.Count==1,"failure does not change active alarms");
                    }
                } finally {Storage.Root=previousRoot;foreach(string name in new[]{"settings.json","settings.json.new","alerts.json","alerts.json.new"}){string file=System.IO.Path.Combine(folder,name);if(System.IO.File.Exists(file))System.IO.File.Delete(file);}if(System.IO.Directory.Exists(folder))System.IO.Directory.Delete(folder);}
            });
            Test("relay URLs require HTTPS without embedded credentials",()=>{
                Check(RelayClient.ValidUrl("https://example.com/relay"),"https");
                foreach(var url in new[]{"http://example.com","https://user:password@example.com","https://example.com/?token=x","https://example.com/#x"})Check(!RelayClient.ValidUrl(url),"reject unsafe url");
            });
            Test("Start sends only credential hash and uses Telegram link",()=>{
                var fake=new RelayTransport {Reply=n=>RelayTransport.Result(new {id=new string('a',32),bot_username="Example_bot",url="https://untrusted.example"})};
                using(var relay=new RelayClient(fake)) {
                    var pair=relay.Begin("https://example.com/relay","@USER_ONE","Example PC",CancellationToken.None).GetAwaiter().GetResult();
                    Check(pair.Url=="https://t.me/Example_bot?start=c_"+new string('a',32),"safe link");
                    Check(fake.Bodies[0].Contains(RelayClient.Hash(pair.Token)) && !fake.Bodies[0].Contains(pair.Token),"only hash transmitted");
                    Check(!fake.HadAuth,"new enrollment has no owner credentials");
                }
            });
            Test("Start waits then stores independent DPAPI device credentials",()=>{
                var old=TestSettings();old.CodexPath="example-codex.exe";old.Alarms=new List<AlarmRule>{Rule()};
                var pair=new Pairing {Id=new string('a',32),Token=RelayClient.NewToken(),Username="user_two",BotUsername="Example_bot"};
                var fake=new RelayTransport {Reply=n=>n==1 ? RelayTransport.Result(new {state="waiting"}) : RelayTransport.Result(new {state="connected",chat_id=222,username="user_two",bot_username="Example_bot"})};
                using(var relay=new RelayClient(fake)) {
                    Check(relay.Finish("https://example.com",pair,old,CancellationToken.None).GetAwaiter().GetResult()==null,"not connected early");
                    var result=relay.Finish("https://example.com",pair,old,CancellationToken.None).GetAwaiter().GetResult();
                    Check(result.Ready && result.ConnectionMode=="relay" && result.ChatId==222 && result.TokenProtected=="" && result.RelayToken()==pair.Token,"independent connection");
                    Check(result.CodexPath==old.CodexPath && old.ChatId==123 && old.ConnectionMode=="direct","old preserved");
                    Check(result.Alarms.Count==1 && result.Alarms[0].Id==old.Alarms[0].Id && result.Alarms[0].IntervalSeconds==15,"alarms preserved on relay connection");
                    Check(!Json.Write(result).Contains(pair.Token) && fake.HadAuth,"encrypted and authenticated");
                }
            });
            Test("relay rejects mismatched recipient from pairing response",()=>{
                var fake=new RelayTransport {Reply=n=>RelayTransport.Result(new {state="connected",chat_id=111,username="user_one",bot_username="Example_bot"})};
                using(var relay=new RelayClient(fake))Throws(()=>relay.Finish("https://example.com",new Pairing {Id=new string('a',32),Token=RelayClient.NewToken(),Username="user_two",BotUsername="Example_bot"},new Settings(),CancellationToken.None).GetAwaiter().GetResult());
            });
            Test("relay send routes without bot token or client destination",()=>{
                var settings=new Settings {ConnectionMode="relay",RelayUrl="https://example.com",ChatId=222};settings.SetRelayToken(RelayClient.NewToken());
                var direct=new FakeTransport {Reply=m=>{throw new Exception("must not call Telegram directly");}};
                var fake=new RelayTransport {Reply=n=>RelayTransport.Result(new {state="sent"})};
                using(var bot=new Telegram(direct,fake))Check(bot.Send(settings,"Test",()=>true,CancellationToken.None).GetAwaiter().GetResult(),"sent");
                Check(direct.Methods.Count==0 && fake.HadAuth && !fake.Bodies[0].Contains("chat_id"),"only device authorization");
            });
            Test("relay retries rate limit with same message id",()=>{
                var settings=new Settings {ConnectionMode="relay",RelayUrl="https://example.com",ChatId=222};settings.SetRelayToken(RelayClient.NewToken());
                var fake=new RelayTransport {Reply=n=>n==1 ? RelayTransport.Result(new {retry_after=2},429) : RelayTransport.Result(new {state="sent"})};
                using(var relay=new RelayClient(fake))Check(relay.Send(settings,"Test",()=>true,CancellationToken.None).GetAwaiter().GetResult(),"sent after retry");
                Check(fake.Bodies.Count==2 && fake.Bodies[0]==fake.Bodies[1],"same id and body");
            });
            Test("relay does not call server for stale alert",()=>{
                var settings=new Settings {ConnectionMode="relay",RelayUrl="https://example.com",ChatId=222};settings.SetRelayToken(RelayClient.NewToken());
                var fake=new RelayTransport {Reply=n=>RelayTransport.Result(new {state="sent"})};
                using(var relay=new RelayClient(fake))Check(!relay.Send(settings,"Test",()=>false,CancellationToken.None).GetAwaiter().GetResult(),"stopped");
                Check(fake.Bodies.Count==0,"no network");
            });
            Test("uncertain relay delivery never reported as sent",()=>{
                var settings=new Settings {ConnectionMode="relay",RelayUrl="https://example.com",ChatId=222};settings.SetRelayToken(RelayClient.NewToken());
                var fake=new RelayTransport {Reply=n=>RelayTransport.Result(new {state="unknown"})};
                bool uncertain=false;
                using(var relay=new RelayClient(fake))try{relay.Send(settings,"Test",()=>true,CancellationToken.None).GetAwaiter().GetResult();}catch(DeliveryUncertain){uncertain=true;}
                Check(uncertain && fake.Bodies.Count==1,"uncertain result preserved");
            });
            Test("fresh install contains no personal configuration",()=>{var s=new Settings();Check(!s.Ready && s.ChatId==0 && s.Username=="" && s.BotUsername=="" && s.TokenProtected=="","empty defaults");});
            Test("launch defaults to tray with explicit show override",()=>Check(Program.StartInTray(new string[0]) && Program.StartInTray(new [] {"--tray"}) && !Program.StartInTray(new [] {"--show"}),"tray default"));
            Test("explicit private id works without username",()=>{
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : Ok(new {id=456,type="private"})};
                using(var bot=new Telegram(fake)){var s=bot.Connect(TestSettings(),"","456","",CancellationToken.None).GetAwaiter().GetResult();Check(s.ChatId==456 && s.Username=="" && s.Ready,"id verified");}
            });
            Test("recipient change does not reuse former chat id",()=>{
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : m=="getWebhookInfo" ? Ok(new {url=""}) : Ok(new object[0])};
                using(var bot=new Telegram(fake))Throws(()=>bot.Connect(TestSettings(),"","","new_user",CancellationToken.None).GetAwaiter().GetResult());
                Check(!fake.Methods.Contains("getChat"),"old id not reused");
            });
            Test("fractional private ids rejected",()=>Throws(()=>Telegram.PrivateRecipient(Json.Read<object>("{\"id\":1.5,\"type\":\"private\"}"),"")));
            Test("two users discover and receive only their own private chat",()=>{
                Func<string,string> reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : m=="getWebhookInfo" ? Ok(new {url=""}) : m=="getUpdates" ? Ok(new [] {
                    new {message=new {chat=new {id=111,type="private",username="user_one"},from=new {id=111}}},
                    new {message=new {chat=new {id=222,type="private",username="user_two"},from=new {id=222}}}
                }) : Ok(new {message_id=1});
                var firstTransport=new FakeTransport {Reply=reply};var secondTransport=new FakeTransport {Reply=reply};
                var empty=new Settings();string token="123456:"+new string('a',35);
                using(var firstBot=new Telegram(firstTransport))using(var secondBot=new Telegram(secondTransport)) {
                    var first=firstBot.Connect(empty,token,"","user_one",CancellationToken.None).GetAwaiter().GetResult();
                    var second=secondBot.Connect(empty,token,"","@USER_TWO",CancellationToken.None).GetAwaiter().GetResult();
                    Check(first.ChatId==111 && second.ChatId==222 && empty.ChatId==0,"independent settings");
                    firstBot.Send(first,"first fixture",()=>true,CancellationToken.None).GetAwaiter().GetResult();
                    secondBot.Send(second,"second fixture",()=>true,CancellationToken.None).GetAwaiter().GetResult();
                    Check(Json.Number(Json.Get(Json.Read<object>(firstTransport.Bodies.Last()),"chat_id"))==111,"first destination");
                    Check(Json.Number(Json.Get(Json.Read<object>(secondTransport.Bodies.Last()),"chat_id"))==222,"second destination");
                }
            });
            Test("another person's Start does not connect the requested recipient",()=>{
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : m=="getWebhookInfo" ? Ok(new {url=""}) : Ok(new [] {new {message=new {chat=new {id=111,type="private",username="user_one"},from=new {id=111}}}})};
                using(var bot=new Telegram(fake))Throws(()=>bot.Connect(new Settings(),"123456:"+new string('a',35),"","user_two",CancellationToken.None).GetAwaiter().GetResult());
                Check(!fake.Methods.Contains("sendMessage"),"no messages to another user");
            });
            Test("remaining is 100 minus used",()=>Check(UsageAt(90).Remaining==90,"90"));
            Test("minimum across core windows",()=>Check(UsageAt(90,4).Remaining==4,"4"));
            Test("reset count read",()=>Check(UsageAt(90).Resets==1,"count"));
            Test("null values mean missing",()=>Throws(()=>Usage.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":null}}}"),Now)));
            Test("no core bucket rejected",()=>Throws(()=>Usage.Parse(Json.Read<object>("{\"rateLimitsByLimitId\":{\"spark\":{\"primary\":{\"usedPercent\":99}}}}"),Now)));
            Test("map takes precedence",()=>Check(Usage.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":10}}}}"),Now).Remaining==90,"map"));
            Test("legacy fallback",()=>Check(Usage.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":10}}}"),Now).Remaining==90,"legacy"));
            Test("spark does not trigger core alert",()=>Check(Usage.Parse(Json.Read<object>("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":10}},\"spark\":{\"primary\":{\"usedPercent\":100}}}}"),Now).Remaining==90,"core only"));
            Test("unknown resets not zero",()=>Check(!Usage.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":0}}}"),Now).Resets.HasValue,"null"));
            Test("percent clamped",()=>Check(UsageAt(-10).Remaining==0 && UsageAt(110).Remaining==100,"bounds"));
            Test("boolean value rejected",()=>Throws(()=>Usage.Parse(Json.Read<object>("{\"rateLimits\":{\"primary\":{\"usedPercent\":false}}}"),Now)));
            Test("10 percent exactly ten messages",()=>{var p=Policy();p.Update(UsageAt(10));Check(Drain(p)==10,"burst");});
            Test("5 percent exactly ten messages",()=>{var p=Policy();p.Update(UsageAt(5));Check(Drain(p)==10,"burst");});
            Test("3 percent exactly fifty messages",()=>{var p=Policy();p.Update(UsageAt(3));Check(Drain(p)==50,"burst");});
            Test("above threshold remains quiet",()=>{var p=Policy();p.Update(UsageAt(10.2));Check(p.Next(Now)==null,"quiet");});
            Test("repeated low samples do not duplicate burst",()=>{var p=Policy();p.Update(UsageAt(10));Drain(p);p.Update(UsageAt(8));Check(Drain(p)==0,"dedupe");});
            Test("escalation replaces pending lower priority",()=>{var p=Policy();p.Update(UsageAt(10));p.Acknowledge(p.Next(Now));p.Update(UsageAt(3));Check(Drain(p)==50,"replace");});
            Test("continuous at two and zero",()=>{var p=Policy();p.Update(UsageAt(2));for(int i=0;i<100;i++){var a=p.Next(Now);Check(a.Continuous,"repeat");p.Acknowledge(a);}p.Update(UsageAt(0));Check(p.Next(Now).Continuous,"zero");});
            Test("recovery stops continuous and queued messages",()=>{var p=Policy();p.Update(UsageAt(2));p.Update(UsageAt(90));Check(p.Next(Now)==null,"stop");});
            Test("recovery rearms for next drop",()=>{var p=Policy();p.Update(UsageAt(3));Drain(p);p.Update(UsageAt(90));p.Update(UsageAt(3));Check(Drain(p)==50,"rearm");});
            Test("timer drift alone does not rearm",()=>{var p=Policy();p.Update(UsageAt(3));Drain(p);p.Update(UsageAt(3,null,2000000060));Check(p.Next(Now)==null,"no drift duplicate");});
            Test("stale data suppresses alerts",()=>{var p=Policy();p.Update(UsageAt(2));Check(p.Next(Now.AddSeconds(91))==null,"stale");});
            Test("old acknowledgment cannot consume new burst",()=>{var p=Policy();p.Update(UsageAt(10));var a=p.Next(Now);p.Update(UsageAt(3));p.Acknowledge(a);Check(Drain(p)==50,"generation");});
            Test("recovered window cancels queued send predicate",()=>{var p=Policy();p.Update(UsageAt(3));var a=p.Next(Now);p.Update(UsageAt(90));Check(!p.IsCurrent(a,Now),"cancel");});
            Test("relaunch resumes remaining messages",()=>{var p=Policy();p.Update(UsageAt(3));for(int i=0;i<7;i++)p.Acknowledge(p.Next(Now));var resumed=new AlertPolicy(Json.Read<AlertState>(Json.Write(p.State)));resumed.Update(UsageAt(3));Check(Drain(resumed)==43,"resume");});
            Test("completed burst stays completed after relaunch",()=>{var p=Policy();p.Update(UsageAt(3));Drain(p);var resumed=new AlertPolicy(Json.Read<AlertState>(Json.Write(p.State)));resumed.Update(UsageAt(3));Check(Drain(resumed)==0,"dedupe disk");});
            Test("multiple windows use independent thresholds",()=>{var p=Policy();p.Update(UsageAt(5,3));Check(Drain(p)==60,"both");});
            Test("absent window removes its pending burst",()=>{var p=Policy();p.Update(UsageAt(90,3));p.Update(UsageAt(90));Check(p.Next(Now)==null,"removed");});
            Test("account switch resets threshold state",()=>{var p=Policy();p.Update(UsageAt(3));Drain(p);var usage=UsageAt(3);usage.AccountId="account-b";p.Update(usage);Check(Drain(p)==50,"account");});
            Test("DPAPI roundtrip",()=>{var s=TestSettings();Check(s.Token().StartsWith("123456:"),"dpapi");Check(!Json.Write(s).Contains(new string('a',35)),"encrypted only");});
            Test("recipient must be private",()=>Throws(()=>Telegram.PrivateRecipient(Json.Read<object>("{\"id\":123,\"type\":\"group\",\"username\":\"example_user\"}"),"example_user")));
            Test("recipient must match username",()=>Throws(()=>Telegram.PrivateRecipient(Json.Read<object>("{\"id\":123,\"type\":\"private\",\"username\":\"elsewhere\"}"),"example_user")));
            Test("verified private recipient accepted",()=>Check(Telegram.PrivateRecipient(Json.Read<object>("{\"id\":123,\"type\":\"private\",\"username\":\"Example_User\"}"),"example_user")==123,"private"));
            Test("Telegram retry_after honored",()=>Check(Telegram.RetryDelay(Json.Read<object>("{\"parameters\":{\"retry_after\":37}}"))==38,"retry"));
            Test("safe lookup does not acknowledge audit updates",()=>{
                var fake=new FakeTransport {Reply=method=>method=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : method=="getWebhookInfo" ? Ok(new {url=""}) : Ok(new [] {new {message=new {chat=new {id=123,type="private",username="example_user"},from=new {id=123}}}})};
                var previous=TestSettings();previous.ChatId=0;
                using(var bot=new Telegram(fake)){var s=bot.Connect(previous,"","",CancellationToken.None).GetAwaiter().GetResult();Check(s.ChatId==123,"bound");Check(!fake.Bodies.Last().Contains("offset") && !fake.Bodies.Last().Contains("allowed_updates"),"non-consuming");}
            });
            Test("webhook audit is never disabled",()=>{
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : Ok(new {url="https://example.invalid/audit"})};
                var previous=TestSettings();previous.ChatId=0;
                using(var bot=new Telegram(fake)){Throws(()=>bot.Connect(previous,"","",CancellationToken.None).GetAwaiter().GetResult());Check(!fake.Methods.Contains("getUpdates") && !fake.Methods.Contains("deleteWebhook"),"preserved");}
            });
            Test("new bot reuses saved private id without polling",()=>{
                var previous=TestSettings();previous.BotUsername="ExampleOld_bot";previous.CodexPath="saved-cli.exe";previous.PausedUntilUtc=Now.AddMinutes(10).ToString("o");previous.Alarms=new List<AlarmRule>{Rule()};
                string replacement="123456:"+new string('b',35);
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : Ok(new {id=123,type="private",username="example_user"})};
                using(var bot=new Telegram(fake)) {
                    var s=bot.Connect(previous,replacement,"",CancellationToken.None).GetAwaiter().GetResult();
                    Check(s.ChatId==123 && s.BotUsername=="ExampleNotify_bot" && s.Token()==replacement,"new bot connected");
                    Check(s.CodexPath==previous.CodexPath && s.PausedUntilUtc==previous.PausedUntilUtc,"preferences preserved");
                    Check(s.Alarms.Count==1 && s.Alarms[0].Id==previous.Alarms[0].Id && s.Alarms[0].MessageCount==7,"alarms preserved on direct connection");
                    Check(fake.Methods.SequenceEqual(new [] {"getMe","getChat"}),"no competing update consumer");
                    Check(previous.BotUsername=="ExampleOld_bot" && previous.Token()!=replacement,"old settings untouched");
                }
            });
            Test("saved id is revalidated against private recipient",()=>{
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : Ok(new {id=123,type="private",username="someone_else"})};
                using(var bot=new Telegram(fake))Throws(()=>bot.Connect(TestSettings(),"","",CancellationToken.None).GetAwaiter().GetResult());
            });
            Test("unavailable chat asks for Start without replacing settings",()=>{
                var previous=TestSettings();previous.BotUsername="ExampleOld_bot";
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : "{\"ok\":false,\"error_code\":400}"};
                using(var bot=new Telegram(fake)) {
                    bool rejected=false;
                    try{bot.Connect(previous,"","",CancellationToken.None).GetAwaiter().GetResult();}
                    catch(TelegramFailure ex){rejected=ex.Message.Contains("Start");}
                    Check(rejected && previous.BotUsername=="ExampleOld_bot","activation required");
                }
            });
            Test("missing id and no personal update cannot enable delivery",()=>{
                var previous=TestSettings();previous.ChatId=0;
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleNotify_bot",is_bot=true}) : m=="getWebhookInfo" ? Ok(new {url=""}) : Ok(new object[0])};
                using(var bot=new Telegram(fake))Throws(()=>bot.Connect(previous,"","",CancellationToken.None).GetAwaiter().GetResult());
            });
            Test("malformed bot identity rejected",()=>{var fake=new FakeTransport {Reply=m=>Ok(new {username="another_bot"})};using(var bot=new Telegram(fake))Throws(()=>bot.Connect(TestSettings(),"","123",CancellationToken.None).GetAwaiter().GetResult());});
            Test("send cancellation predicate stops stale alerts",()=>{var fake=new FakeTransport {Reply=m=>Ok(new {message_id=1})};using(var bot=new Telegram(fake)){Check(!bot.Send(TestSettings(),"test",()=>false,CancellationToken.None).GetAwaiter().GetResult(),"cancel");Check(fake.Methods.Count==0,"no request");}});
            Test("all sends have at least two second spacing",()=>{var fake=new FakeTransport {Reply=m=>Ok(new {message_id=1})};using(var bot=new Telegram(fake)){var s=TestSettings();bot.Send(s,"test",()=>true,CancellationToken.None).GetAwaiter().GetResult();var watch=Stopwatch.StartNew();bot.Send(s,"test",()=>true,CancellationToken.None).GetAwaiter().GetResult();Check(watch.Elapsed.TotalSeconds>=1.95,"spacing");Check(fake.Bodies.All(b=>Json.Str(Json.Get(Json.Read<object>(b),"chat_id"))=="123"),"private id");}});
            Test("rate limit response raises controlled retry",()=>{var fake=new FakeTransport {Reply=m=>"{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":15}}"};using(var bot=new Telegram(fake)){try{bot.Send(TestSettings(),"test",()=>true,CancellationToken.None).GetAwaiter().GetResult();throw new Exception();}catch(TelegramFailure ex){Check(ex.RetrySeconds==16,"backoff");}}});
            Test("old settings retain private destination",()=>{var s=Json.Read<Settings>("{\"ChatId\":123,\"TokenProtected\":\"opaque\"}");Check(s.Ready && s.RecipientMode=="private" && s.TargetChatId==123,"migration");});
            Test("group validation rejects private channel fractional and out of range ids",()=>{
                foreach(var chat in new object[]{new {id=-123,type="private"},new {id=-123,type="channel"},new {id=123,type="group"},new {id=-1.5,type="group"},new {id=-9007199254740992L,type="supergroup"}})Throws(()=>GroupConnection.Validate(Json.Read<object>(Json.Write(chat))));
                Check(GroupConnection.Validate(Json.Read<object>("{\"id\":-1001234567890,\"type\":\"supergroup\"}"))==-1001234567890L,"64 bit group id");
            });
            Test("group connection verifies destination and keeps private settings",()=>{
                var old=TestSettings();old.Alarms=new List<AlarmRule>{Rule()};old.PausedUntilUtc=Now.AddMinutes(8).ToString("o");
                var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {id=555,is_bot=true,username="ExampleOld_bot"}) : m=="getChat" ? Ok(new {id=-999,type="group",title="Example group"}) : Ok(new {status="member"})};
                using(var bot=new Telegram(fake)){var next=GroupConnection.Connect(bot,old,"-999",CancellationToken.None).GetAwaiter().GetResult();Check(next.Ready && next.TargetChatId==-999 && next.ChatId==123 && next.Username==old.Username && next.Token()==old.Token(),"only group selected, private preserved");Check(next.Alarms[0].Id==old.Alarms[0].Id && next.PausedUntilUtc==old.PausedUntilUtc && old.GroupChatId==0,"other state unchanged");Check(fake.Methods.SequenceEqual(new[]{"getMe","getChat","getChatMember"}),"no messages or update consumption");}
            });
            Test("wrong group or missing bot permission does not replace recipient",()=>{
                foreach(var scenario in new[]{"wrong-id","left","kicked","restricted","read-only"}){
                    var old=TestSettings();var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {id=555,is_bot=true,username="ExampleOld_bot"}) : m=="getChat" ? Ok(new {id=scenario=="wrong-id" ? -888 : -999,type="group",title="Example",permissions=new {can_send_messages=scenario!="read-only"}}) : Ok(new {status=scenario=="read-only" ? "member" : scenario,is_member=true,can_send_messages=false})};
                    using(var bot=new Telegram(fake))Throws(()=>GroupConnection.Connect(bot,old,"-999",CancellationToken.None).GetAwaiter().GetResult());Check(old.TargetChatId==123 && old.GroupChatId==0 && !fake.Methods.Contains("sendMessage"),"no mutation or send");
                }
            });
            Test("group discovery reads group events without acknowledging updates",()=>{
                var fake=new FakeTransport {Reply=m=>m=="getWebhookInfo" ? Ok(new {url=""}) : Ok(new object[]{new {my_chat_member=new {chat=new {id=-999,type="group",title="Example"}}},new {message=new {chat=new {id=123,type="private",title="ignore"}}},new {message=new {chat=new {id=-999,type="group",title="Example"}}}})};
                using(var bot=new Telegram(fake)){var groups=GroupConnection.Find(bot,TestSettings(),CancellationToken.None).GetAwaiter().GetResult();Check(groups.Count==1 && groups[0].Id==-999,"only group once");Check(!fake.Bodies.Any(b=>b.Contains("offset") || b.Contains("allowed_updates")),"read only lookup");}
            });
            Test("group discovery preserves existing webhook",()=>{var fake=new FakeTransport {Reply=m=>Ok(new {url="https://example.invalid/hook"})};using(var bot=new Telegram(fake))Throws(()=>GroupConnection.Find(bot,TestSettings(),CancellationToken.None).GetAwaiter().GetResult());Check(fake.Methods.SequenceEqual(new[]{"getWebhookInfo"}),"no competing consumer");});
            Test("selected group receives sends and failures never fall back to private",()=>{
                foreach(bool fail in new[]{false,true}){var s=TestSettings();s.RecipientMode="group";s.GroupChatId=-999;var fake=new FakeTransport {Reply=m=>fail ? "{\"ok\":false,\"error_code\":403}" : Ok(new {message_id=7})};
                    using(var bot=new Telegram(fake)){if(fail)Throws(()=>bot.Send(s,"test",()=>true,CancellationToken.None).GetAwaiter().GetResult());else Check(bot.Send(s,"test",()=>true,CancellationToken.None).GetAwaiter().GetResult(),"delivered");}Check(fake.Bodies.Count==1 && Json.Str(Json.Get(Json.Read<object>(fake.Bodies[0]),"chat_id"))=="-999","group only");}
            });
            Test("unconfigured group invalid mode and relay group cannot send privately",()=>{
                foreach(var mode in new[]{"group","unknown","relay-group"}){var s=TestSettings();s.RecipientMode=mode=="unknown" ? "unknown" : "group";if(mode=="relay-group"){s.ConnectionMode="relay";s.GroupChatId=-999;s.RelayUrl="https://example.com";s.SetRelayToken(RelayClient.NewToken());}var fake=new FakeTransport {Reply=m=>Ok(new {message_id=1})};using(var bot=new Telegram(fake))Throws(()=>bot.Send(s,"test",()=>true,CancellationToken.None).GetAwaiter().GetResult());Check(!s.Ready && fake.Methods.Count==0,"no fallback");}
            });
            Test("same bot reconnect keeps group choice new token clears it",()=>{
                foreach(bool replacement in new[]{false,true}){var old=TestSettings();old.GroupChatId=-999;old.GroupTitle="Example";old.RecipientMode="group";var fake=new FakeTransport {Reply=m=>m=="getMe" ? Ok(new {username="ExampleOld_bot",is_bot=true}) : Ok(new {id=123,type="private",username="example_user"})};using(var bot=new Telegram(fake)){var next=bot.Connect(old,replacement ? "123456:"+new string('b',35) : "","123",CancellationToken.None).GetAwaiter().GetResult();Check(replacement ? next.RecipientMode=="private" && next.GroupChatId==0 : next.RecipientMode=="group" && next.GroupChatId==-999,"recipient validity follows bot");}}
            });
            Test("recipient switch persists both targets and cancels a queued old send",()=>{
                string previousRoot=Storage.Root;string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"sentinel-recipient-"+Guid.NewGuid().ToString("N"));Storage.Root=root;
                try {var original=TestSettings();original.GroupChatId=-999;original.GroupTitle="Example";using(var monitor=new Monitor(original,new AlertState())){
                    var fake=new FakeTransport {Reply=m=>Ok(new {message_id=1})};using(var bot=new Telegram(fake)){
                        bot.Send(original,"first",()=>true,CancellationToken.None).GetAwaiter().GetResult();var pending=bot.Send(original,"stale",()=>original==monitor.Settings,CancellationToken.None);
                        monitor.SelectRecipient("group");Check(!pending.GetAwaiter().GetResult() && fake.Bodies.Count==1,"stale send cancelled after wait");
                        var saved=Storage.Load<Settings>("settings.json");Check(saved.RecipientMode=="group" && saved.ChatId==123 && saved.GroupChatId==-999,"selection persisted");
                        monitor.SelectRecipient("private");Check(monitor.Settings.TargetChatId==123 && monitor.Settings.GroupChatId==-999,"switch back retains group");
                        var current=monitor.Settings;Throws(()=>monitor.SelectRecipient("invalid"));Check(object.ReferenceEquals(current,monitor.Settings),"invalid mode unchanged");
                    }
                }}finally{Storage.Root=previousRoot;foreach(string name in new[]{"settings.json","settings.json.new","alerts.json","alerts.json.new"}){string file=System.IO.Path.Combine(root,name);if(System.IO.File.Exists(file))System.IO.File.Delete(file);}if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root);}
            });
            Test("quiet status sends once then edits and survives restart",()=>{
                var s=StatusSettings();var state=new StatusMessageState();string saved="";var fake=StatusTransport();
                using(var bot=new Telegram(fake)){var publisher=new StatusPublisher(state,v=>saved=Json.Write(v));publisher.Update(bot,s,"45%",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();publisher.Update(bot,s,"44%",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();publisher.Update(bot,s,"44%",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();}
                Check(fake.Methods.SequenceEqual(new[]{"sendMessage","editMessageText"}),"one message plus edit");Check(object.Equals(Json.Get(Json.Read<object>(fake.Bodies[0]),"disable_notification"),true),"silent creation");
                var restarted=StatusTransport();using(var bot=new Telegram(restarted))new StatusPublisher(Json.Read<StatusMessageState>(saved),v=>{}).Update(bot,s,"43%",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();
                Check(restarted.Methods.Single()=="editMessageText" && Json.Str(Json.Get(Json.Read<object>(restarted.Bodies[0]),"message_id"))=="100","same id after restart");
            });
            Test("alarm stays audible and new quiet status follows it without deleting alarm",()=>{
                var s=StatusSettings();var fake=StatusTransport();var state=new StatusMessageState();
                using(var bot=new Telegram(fake)){var publisher=new StatusPublisher(state,v=>{});publisher.Update(bot,s,"3%",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();bot.Send(s,"alarm",()=>true,CancellationToken.None).GetAwaiter().GetResult();publisher.Update(bot,s,"3%",true,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();}
                Check(fake.Methods.SequenceEqual(new[]{"sendMessage","sendMessage","sendMessage","deleteMessage"}),"status below alarm then cleanup");
                Check(object.Equals(Json.Get(Json.Read<object>(fake.Bodies[1]),"disable_notification"),false) && object.Equals(Json.Get(Json.Read<object>(fake.Bodies[2]),"disable_notification"),true),"sound only on alarm");
                Check(Json.Str(Json.Get(Json.Read<object>(fake.Bodies[3]),"message_id"))=="100" && state.Messages.Values.Single().MessageId==102,"delete only old status");
            });
            Test("deleted status is recreated silently but edit denial does not duplicate",()=>{
                foreach(bool missing in new[]{true,false}){var s=StatusSettings();var state=new StatusMessageState();state.Messages[StatusPublisher.Key(s)]=new StatusMessageRecord {MessageId=5,Text="old"};var fake=new FakeTransport {Reply=m=>m=="editMessageText" ? "{\"ok\":false,\"error_code\":400,\"description\":\""+(missing ? "Bad Request: message to edit not found" : "Bad Request: message can't be edited")+"\"}" : Ok(new {message_id=8,chat=new {id=-999}})};
                    using(var bot=new Telegram(fake)){var publisher=new StatusPublisher(state,v=>{});if(missing)publisher.Update(bot,s,"new",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();else Throws(()=>publisher.Update(bot,s,"new",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult());}
                    Check(fake.Methods.Count==(missing ? 2 : 1),"only explicit missing permits recreation");if(missing)Check(state.Messages.Values.Single().MessageId==8 && object.Equals(Json.Get(Json.Read<object>(fake.Bodies.Last()),"disable_notification"),true),"new silent status");
                }
            });
            Test("Telegram unchanged status response succeeds",()=>{
                var s=StatusSettings();var state=new StatusMessageState();state.Messages[StatusPublisher.Key(s)]=new StatusMessageRecord {MessageId=5,Text="old"};var fake=new FakeTransport {Reply=m=>"{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}"};using(var bot=new Telegram(fake))new StatusPublisher(state,v=>{}).Update(bot,s,"new",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();Check(fake.Methods.Count==1 && state.Messages.Values.Single().MessageId==5,"no duplicate");
            });
            Test("ambiguous status creation is not retried after restart",()=>{
                var s=StatusSettings();string persisted="";var state=new StatusMessageState();var fake=new FakeTransport {Reply=m=>{throw new HttpRequestException("offline");}};
                using(var bot=new Telegram(fake))Throws(()=>new StatusPublisher(state,v=>persisted=Json.Write(v)).Update(bot,s,"status",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult());
                var other=StatusTransport();using(var bot=new Telegram(other))Throws(()=>new StatusPublisher(Json.Read<StatusMessageState>(persisted),v=>{}).Update(bot,s,"status",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult());Check(other.Methods.Count==0,"no duplicate on uncertainty");
            });
            Test("status creation requires durable intent and post-send save failure preserves uncertainty",()=>{
                foreach(bool afterSend in new[]{false,true}){var s=StatusSettings();var fake=StatusTransport();int saves=0;string persisted="";using(var bot=new Telegram(fake))Throws(()=>new StatusPublisher(new StatusMessageState(),v=>{if(!afterSend || saves++>0)throw new System.IO.IOException();persisted=Json.Write(v);}).Update(bot,s,"status",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult());Check(fake.Methods.Count==(afterSend ? 1 : 0),"safe persistence ordering");if(afterSend)Check(Json.Read<StatusMessageState>(persisted).Messages.Values.Single().Creating,"restart stays conservative");}
            });
            Test("explicit status recovery can create after uncertain result",()=>{
                var s=StatusSettings();var state=new StatusMessageState();state.Messages[StatusPublisher.Key(s)]=new StatusMessageRecord {Creating=true};var fake=StatusTransport();using(var bot=new Telegram(fake))new StatusPublisher(state,v=>{}).Update(bot,s,"status",true,true,()=>true,CancellationToken.None).GetAwaiter().GetResult();Check(fake.Methods.Single()=="sendMessage" && !state.Messages.Values.Single().Creating,"manual retry");
            });
            Test("quiet status shares destination selection and does not update unselected chat",()=>{
                var s=StatusSettings();var state=new StatusMessageState();var fake=StatusTransport();using(var bot=new Telegram(fake)){var p=new StatusPublisher(state,v=>{});p.Update(bot,s,"group",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();var personal=Json.Read<Settings>(Json.Write(s));personal.RecipientMode="private";p.Update(bot,personal,"private",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();p.Update(bot,s,"stale",false,false,()=>false,CancellationToken.None).GetAwaiter().GetResult();}
                Check(state.Messages.Count==2 && fake.Methods.Count==2,"separate messages, cancelled stale write");Check(Json.Str(Json.Get(Json.Read<object>(fake.Bodies[0]),"chat_id"))=="-999" && Json.Str(Json.Get(Json.Read<object>(fake.Bodies[1]),"chat_id"))=="123","exclusive destinations");
            });
            Test("quiet status disabled or relay cannot create a message",()=>{
                foreach(bool relay in new[]{false,true}){var s=StatusSettings();if(relay)s.ConnectionMode="relay";else s.LiveStatusEnabled=false;var fake=StatusTransport();using(var bot=new Telegram(fake))new StatusPublisher(new StatusMessageState(),v=>{}).Update(bot,s,"status",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();Check(fake.Methods.Count==0,"no send");}
            });
            Test("quiet status freshness is explicit and alarm pause does not hide percentages",()=>{
                var usage=UsageAt(45);Check(StatusPublisher.Format(usage,true,true,Now).Contains("45%") && StatusPublisher.Format(usage,true,true,Now).Contains("на паузе"),"visible while paused");Check(StatusPublisher.Format(usage,false,false,Now).Contains("Нет свежих данных") && !StatusPublisher.Format(usage,true,false,Now.AddMinutes(3)).Contains("45%"),"no stale percentages presented as current");
            });
            Test("status rate limit is respected without ambiguous-create lock",()=>{
                var s=StatusSettings();var state=new StatusMessageState();var fake=new FakeTransport {Reply=m=>"{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":12}}"};using(var bot=new Telegram(fake)){try{new StatusPublisher(state,v=>{}).Update(bot,s,"status",false,false,()=>true,CancellationToken.None).GetAwaiter().GetResult();throw new Exception();}catch(TelegramFailure ex){Check(ex.RateLimited && ex.RetrySeconds==13 && !state.Messages.Values.Single().Creating,"explicit rejection can retry later");}}
            });
            Console.WriteLine("RESULT "+passed+" passed; "+failed+" failed");return failed==0 ? 0 : 1;
        }
    }
}
