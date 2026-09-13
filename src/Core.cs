using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageSentinel {
    public static class Json {
        public static string Write(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static T Read<T>(string text) { return new JavaScriptSerializer().Deserialize<T>(text); }
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
        public static object Get(object value, string key) { var obj = Obj(value); return obj != null && obj.ContainsKey(key) ? obj[key] : null; }
        public static string Str(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
        public static double? Number(object value) {
            if (value == null || value is bool) return null;
            double n; return double.TryParse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !double.IsNaN(n) && !double.IsInfinity(n) ? (double?)n : null;
        }
    }
    public sealed class Settings {
        public int Version = 1;
        public string TokenProtected = "";
        public long ChatId = 0;
        public string Username = "";
        public string BotUsername = "";
        public string CodexPath = "";
        public string PausedUntilUtc = "";
        public bool Ready { get { return ChatId > 0 && !string.IsNullOrEmpty(TokenProtected); } }
        public string Token() {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.CurrentUser)); }
            catch { throw new InvalidOperationException("Не удалось открыть токен. Введите его заново в настройках Telegram."); }
        }
        public void SetToken(string token) { TokenProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser)); }
    }
    public static class Storage {
        public static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageSentinel");
        public static string Warning = "";
        static readonly object Gate = new object();
        public static T Load<T>(string name) where T : class, new() {
            lock (Gate) {
                string path = Path.Combine(Root, name);
                if (!File.Exists(path)) return new T();
                try { return Json.Read<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T(); }
                catch { Warning = "Не удалось прочитать " + name + ". Проверьте настройки."; return new T(); }
            }
        }
        public static void Save(string name, object value) {
            lock (Gate) {
                Directory.CreateDirectory(Root);
                string path = Path.Combine(Root, name), temp = path + ".new";
                File.WriteAllText(temp, Json.Write(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }
    }
    public sealed class WindowUsage {
        public string Key, Label;
        public double Remaining;
        public long? ResetsAt;
        public int Duration;
        public bool Core;
        public string ResetText {
            get {
                if (!ResetsAt.HasValue) return "время сброса неизвестно";
                try { return "сброс " + new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(ResetsAt.Value).ToLocalTime().ToString("dd.MM HH:mm"); }
                catch { return "время сброса неизвестно"; }
            }
        }
    }
    public sealed class Usage {
        public List<WindowUsage> Windows = new List<WindowUsage>();
        public int? Resets;
        public string AccountId = "";
        public DateTime CheckedUtc;
        public List<WindowUsage> Core { get { return Windows.Where(w => w.Core).ToList(); } }
        public double Remaining { get { return Core.Min(w => w.Remaining); } }
        public static Usage Parse(object data, DateTime now) {
            var result = new Usage { CheckedUtc = now, AccountId = Json.Str(Json.Get(data, "accountId")) };
            var count = Json.Number(Json.Get(Json.Get(data, "rateLimitResetCredits"), "availableCount"));
            if (count.HasValue && count >= 0 && count <= int.MaxValue) result.Resets = (int)count.Value;
            var buckets = Json.Obj(Json.Get(data, "rateLimitsByLimitId"));
            object legacy = Json.Get(data, "rateLimits");
            if (buckets != null && buckets.Count > 0) {
                foreach (var bucket in buckets) result.AddBucket(bucket.Value, bucket.Key);
                // Some versions omit the core bucket in the map, but retain it in the legacy field.
                if (!result.Core.Any() && Json.Str(Json.Get(legacy,"limitId")) == "codex") result.AddBucket(legacy,"codex");
            } else if (legacy != null) {
                string id = Json.Str(Json.Get(legacy,"limitId"));
                result.AddBucket(legacy, string.IsNullOrEmpty(id) ? "codex" : id);
            }
            if (!result.Core.Any()) throw new InvalidOperationException("Codex не вернул основной лимит. Нет свежих данных.");
            return result;
        }
        void AddBucket(object bucket, string id) {
            foreach (string slot in new [] {"primary", "secondary"}) {
                object raw = Json.Get(bucket,slot);
                var used = Json.Number(Json.Get(raw,"usedPercent"));
                if (!used.HasValue) continue;
                var minutes = Json.Number(Json.Get(raw,"windowDurationMins"));
                int duration = minutes.HasValue && minutes > 0 && minutes <= int.MaxValue ? (int)minutes.Value : 0;
                string period = duration == 10080 ? "Неделя" : duration == 300 ? "5 часов" : duration > 0 ? duration + " мин" : "Окно " + slot;
                string title = id == "codex" ? "Codex" : Json.Str(Json.Get(bucket,"limitName"));
                if (title == "") title = id;
                var reset = Json.Number(Json.Get(raw,"resetsAt"));
                Windows.Add(new WindowUsage {Key=id+":"+slot+":"+duration, Core=id=="codex", Duration=duration, Label=title+" · "+period,
                    Remaining=Math.Max(0,Math.Min(100,100-used.Value)), ResetsAt=reset.HasValue && reset>=0 && reset<=253402300799 ? (long?)reset.Value : null});
            }
        }
        public string Description() {
            return string.Join("\n", Core.Select(w => w.Label + ": осталось " + w.Remaining.ToString("0.#",CultureInfo.InvariantCulture) + "% · " + w.ResetText)) +
                "\nДоступные сбросы: " + (Resets.HasValue ? Resets.Value.ToString() : "нет данных") +
                "\nПроверено: " + CheckedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss");
        }
    }
    public sealed class AlertState {
        public string AccountId = "";
        public Dictionary<string, WindowState> Windows = new Dictionary<string, WindowState>();
    }
    public sealed class WindowState {
        public bool Fired10, Fired5, Fired3;
        public int Pending, Stage;
        public long Generation;
    }
    public sealed class AlertItem {
        public string Key;
        public int Stage;
        public long Generation;
        public bool Continuous;
    }
    // Pure state machine. A skipped threshold escalates directly to the most urgent applicable burst.
    public sealed class AlertPolicy {
        public AlertState State;
        public Usage Current;
        public AlertPolicy(AlertState state) { State = state ?? new AlertState(); if(State.Windows == null) State.Windows=new Dictionary<string,WindowState>(); }
        public void Update(Usage usage) {
            if (usage.AccountId != "" && State.AccountId != "" && usage.AccountId != State.AccountId) State.Windows.Clear();
            if (usage.AccountId != "") State.AccountId=usage.AccountId;
            Current=usage;
            foreach (var key in State.Windows.Keys.Where(k=>!usage.Core.Any(w=>w.Key==k)).ToList()) State.Windows.Remove(key);
            foreach(var w in usage.Core) {
                WindowState s;
                if (!State.Windows.TryGetValue(w.Key,out s)) State.Windows[w.Key]=s=new WindowState();
                double r=w.Remaining;
                if(r>10) s.Fired10=false;
                if(r>5) s.Fired5=false;
                if(r>3) s.Fired3=false;
                if (s.Pending>0 && r>s.Stage) { s.Pending=0; s.Generation++; }
                int stage=r<=2 ? 2 : r<=3 && !s.Fired3 ? 3 : r<=5 && !s.Fired5 ? 5 : r<=10 && !s.Fired10 ? 10 : 0;
                if (stage>0) {
                    s.Fired10=true;
                    if(stage<=5) s.Fired5=true;
                    if(stage<=3) s.Fired3=true;
                    if(stage==2) { if(s.Stage!=2 || s.Pending>0) s.Generation++; s.Pending=0; s.Stage=2; }
                    else { s.Stage=stage; s.Pending=stage==3 ? 50 : 10; s.Generation++; }
                }
            }
        }
        public AlertItem Next(DateTime now) {
            if(Current==null || now-Current.CheckedUtc>TimeSpan.FromSeconds(90)) return null;
            var critical=Current.Core.OrderBy(w=>w.Remaining).FirstOrDefault(w=>w.Remaining<=2);
            if(critical!=null) { var s=State.Windows[critical.Key]; return new AlertItem { Key=critical.Key, Stage=2, Continuous=true, Generation=s.Generation }; }
            foreach(var w in Current.Core.OrderBy(w=>w.Remaining)) {
                var s=State.Windows[w.Key];
                if(s.Pending>0 && w.Remaining<=s.Stage) return new AlertItem {Key=w.Key,Stage=s.Stage,Generation=s.Generation};
            }
            return null;
        }
        public bool IsCurrent(AlertItem item, DateTime now) {
            var next=Next(now);
            return next!=null && next.Key==item.Key && next.Generation==item.Generation && next.Stage==item.Stage;
        }
        public void Acknowledge(AlertItem item) {
            WindowState s;
            if(!item.Continuous && State.Windows.TryGetValue(item.Key,out s) && s.Generation==item.Generation && s.Pending>0) s.Pending--;
        }
    }
    public static class CodexClient {
        public static string Find(string configured) {
            if(!string.IsNullOrWhiteSpace(configured)) {
                if(File.Exists(configured) && string.Equals(Path.GetFileName(configured),"codex.exe",StringComparison.OrdinalIgnoreCase)) return configured;
                throw new InvalidOperationException("Указанный codex.exe не найден. Выберите его заново.");
            }
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
            if(Directory.Exists(root)) {
                string path=Directory.GetDirectories(root).Select(d=>Path.Combine(d,"codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if(path!=null) return path;
            }
            foreach(string directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(';')) {
                try { string path=Path.Combine(directory.Trim('"'),"codex.exe"); if(File.Exists(path)) return path; } catch {}
            }
            throw new InvalidOperationException("Codex CLI не найден. Установите Codex или выберите codex.exe.");
        }
        static async Task<object> Request(Process process, int id, string method, object parameters, CancellationToken ct) {
            process.StandardInput.WriteLine(Json.Write(new {id=id,method=method,@params=parameters}));
            var timer=Task.Delay(25000,ct);
            while(true) {
                var read=process.StandardOutput.ReadLineAsync();
                if(await Task.WhenAny(read,timer)!=read) { ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("Codex не ответил за 25 секунд."); }
                string line=await read;
                if(line==null) throw new InvalidOperationException("Codex App Server завершился. Повторная попытка через минуту.");
                object msg; try { msg=Json.Read<object>(line); } catch { continue; }
                if(Json.Str(Json.Get(msg,"id"))!=id.ToString()) {
                    if(Json.Get(msg,"id")!=null && Json.Get(msg,"method")!=null)
                        process.StandardInput.WriteLine(Json.Write(new {id=Json.Get(msg,"id"),error=new {code=-32601,message="Unsupported by usage monitor"}}));
                    continue;
                }
                if(Json.Get(msg,"error")!=null) throw new InvalidOperationException("Не удалось прочитать лимиты Codex. Проверьте вход в Codex через ChatGPT и подключение к сети.");
                return Json.Get(msg,"result");
            }
        }
        public static async Task<Usage> Read(string configured, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Find(configured),"app-server") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Storage.Root
            };
            Directory.CreateDirectory(Storage.Root);
            using(var process=new Process {StartInfo=start}) {
                process.Start();
                // Drain stderr without recording account, request or credential data.
                var drain=process.StandardError.ReadToEndAsync();
                using(ct.Register(()=>{try {if(!process.HasExited) process.Kill();}catch{}})) {
                    try {
                        await Request(process,1,"initialize",new {clientInfo=new {name="codex_usage_sentinel",title="Codex Usage Sentinel",version=Program.Build}},ct);
                        process.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                        var data=await Request(process,2,"account/rateLimits/read",null,ct);
                        return Usage.Parse(data,DateTime.UtcNow);
                    } finally {
                        try {process.StandardInput.Close(); if(!process.HasExited && !process.WaitForExit(500)) process.Kill(); process.WaitForExit(1000);}catch{}
                    }
                }
            }
        }
    }
}
