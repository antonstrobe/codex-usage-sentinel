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
        public string ConnectionMode = "direct";
        public string RelayUrl = "";
        public string RelayTokenProtected = "";
        public long ChatId = 0;
        public string RecipientMode = "private";
        public long GroupChatId = 0;
        public string GroupTitle = "";
        public string Username = "";
        public string BotUsername = "";
        public string CodexPath = "";
        public string PausedUntilUtc = "";
        public bool LiveStatusEnabled = false;
        public List<AlarmRule> Alarms = AlarmRule.Defaults();
        public long TargetChatId { get { return RecipientMode=="group" ? GroupChatId : RecipientMode=="private" ? ChatId : 0; } }
        public bool Ready { get {
            if(ConnectionMode=="relay") return RecipientMode=="private" && ChatId>0 && !string.IsNullOrEmpty(RelayTokenProtected) && RelayClient.ValidUrl(RelayUrl);
            return ConnectionMode=="direct" && !string.IsNullOrEmpty(TokenProtected) &&
                ((RecipientMode=="private" && ChatId>0) || (RecipientMode=="group" && GroupChatId<0));
        } }
        public string RecipientLabel { get { return RecipientMode=="group" ? "группа «"+(string.IsNullOrEmpty(GroupTitle) ? GroupChatId.ToString(CultureInfo.InvariantCulture) : GroupTitle)+"»" :
            RecipientMode=="private" ? (string.IsNullOrEmpty(Username) ? "личный чат" : "@"+Username) : "получатель не выбран"; } }
        public string RelayToken() {
            try {return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(RelayTokenProtected),null,DataProtectionScope.CurrentUser));}
            catch {throw new InvalidOperationException("Подключите этот компьютер через Start заново.");}
        }
        public void SetRelayToken(string token) {RelayTokenProtected=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token),null,DataProtectionScope.CurrentUser));}
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
