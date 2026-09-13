using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsageSentinel {
    public sealed class DeliveryUncertain : Exception {
        public DeliveryUncertain() : base("Telegram не подтвердил результат отправки. Возможная доставка не засчитана; это сообщение повторно не отправляю.") {}
    }
    public sealed class Pairing {
        public string Id,Token,Url,Username,BotUsername;
    }
    public sealed class RelayClient : IDisposable {
        public const string DefaultUrl="";
        readonly HttpClient http;
        public RelayClient(HttpMessageHandler handler=null) {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http=new HttpClient(handler??new HttpClientHandler {AllowAutoRedirect=false});http.Timeout=TimeSpan.FromSeconds(20);
        }
        public static bool ValidUrl(string value) {
            Uri uri;return Uri.TryCreate(value,UriKind.Absolute,out uri) && uri.Scheme=="https" && uri.UserInfo=="" && uri.Query=="" && uri.Fragment=="";
        }
        public static string NewToken() {var bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');}
        public static string Hash(string value) {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
        async Task<object> Call(string endpoint,string path,string token,object body,CancellationToken ct) {
            if(!ValidUrl(endpoint))throw new TelegramFailure("Адрес сервиса должен быть HTTPS, без пароля и параметров.",60);
            try {
                using(var request=new HttpRequestMessage(body==null ? HttpMethod.Get : HttpMethod.Post,endpoint.TrimEnd('/')+path)) {
                    if(token!="")request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                    if(body!=null)request.Content=new StringContent(Json.Write(body),Encoding.UTF8,"application/json");
                    using(var response=await http.SendAsync(request,ct)) {
                        object data;
                        try{data=Json.Read<object>(await response.Content.ReadAsStringAsync());}catch{throw new TelegramFailure("Сервис вернул нечитаемый ответ.");}
                        if(response.IsSuccessStatusCode)return data;
                        int code=(int)response.StatusCode;
                        int retry=(int)Math.Max(2,Math.Min(86400,Json.Number(Json.Get(data,"retry_after"))??10));
                        // Do not display arbitrary server responses or request headers containing credentials.
                        if(code==401)throw new TelegramFailure("Подключение отключено. Подключите этот компьютер через Start заново.",60);
                        if(code==403)throw new TelegramFailure("Ваш аккаунт ещё не добавлен владельцем общего бота.",60);
                        if(code==410 || code==404)throw new TelegramFailure("Ссылка подключения истекла или не найдена. Создайте новую.",30);
                        if(code==429)throw new TelegramFailure("Ожидание разрешённого интервала сервиса или Telegram.",retry) {RateLimited=true};
                        throw new TelegramFailure("Сервис общего бота отклонил запрос ("+code+"). Попробуйте подключиться заново.",15);
                    }
                }
            } catch(OperationCanceledException) {ct.ThrowIfCancellationRequested();throw new HttpRequestException("Relay timeout");}
        }
        public async Task<Pairing> Begin(string endpoint,string username,string device,CancellationToken ct) {
            username=(username??"").Trim().TrimStart('@').ToLowerInvariant();
            if(!Regex.IsMatch(username,@"^[A-Za-z0-9_]{5,32}$"))throw new TelegramFailure("Укажите свой Telegram username, например example_user.");
            if(string.IsNullOrWhiteSpace(device) || device.Length>64)throw new TelegramFailure("Укажите имя компьютера длиной до 64 символов.");
            string token=NewToken();
            var result=await Call(endpoint,"/v1/pairings","",new {username=username,device=device.Trim(),secret_hash=Hash(token)},ct);
            string id=Json.Str(Json.Get(result,"id")),bot=Json.Str(Json.Get(result,"bot_username"));
            if(!Regex.IsMatch(id,@"^[A-Za-z0-9_-]{32}$") || !Regex.IsMatch(bot,@"^[A-Za-z0-9_]{5,32}$"))throw new TelegramFailure("Сервис не подтвердил ссылку подключения.");
            // Construct only a Telegram link; never open a server-supplied arbitrary URL.
            return new Pairing {Id=id,Token=token,Username=username,BotUsername=bot,Url="https://t.me/"+bot+"?start=c_"+id};
        }
        public async Task<Settings> Finish(string endpoint,Pairing pair,Settings old,CancellationToken ct) {
            var result=await Call(endpoint,"/v1/pairings/"+pair.Id,pair.Token,null,ct);
            string state=Json.Str(Json.Get(result,"state"));
            if(state=="waiting")return null;
            var id=Json.Number(Json.Get(result,"chat_id"));
            if(state!="connected" || !id.HasValue || id<=0 || id>9007199254740991 || Math.Floor(id.Value)!=id ||
                !string.Equals(pair.Username,Json.Str(Json.Get(result,"username")),StringComparison.OrdinalIgnoreCase) || pair.BotUsername!=Json.Str(Json.Get(result,"bot_username")))
                throw new TelegramFailure("Сервис не подтвердил ваш личный чат.");
            var settings=new Settings {ConnectionMode="relay",RelayUrl=endpoint.TrimEnd('/'),ChatId=(long)id.Value,Username=pair.Username,BotUsername=pair.BotUsername,CodexPath=old.CodexPath,PausedUntilUtc=old.PausedUntilUtc};
            settings.SetRelayToken(pair.Token);return settings;
        }
        public async Task<bool> Send(Settings settings,string text,Func<bool> stillNeeded,CancellationToken ct) {
            string id=Guid.NewGuid().ToString("N"),token=settings.RelayToken();
            bool attempted=false;
            for(int attempt=0;attempt<3;attempt++) {
                if(!stillNeeded()) {if(attempted)throw new DeliveryUncertain();return false;}
                bool retry=false;int wait=2;
                try {
                    attempted=true;
                    var result=await Call(settings.RelayUrl,"/v1/messages",token,new {id=id,text=text},ct);
                    string state=Json.Str(Json.Get(result,"state"));
                    if(state=="sent")return true;
                    if(state=="unknown")throw new DeliveryUncertain();
                    throw new TelegramFailure("Telegram отклонил уведомление. Проверьте блокировку бота и подключение.",60);
                } catch(HttpRequestException) {retry=true;}
                catch(TelegramFailure ex){if(!ex.RateLimited)throw;retry=true;wait=ex.RetrySeconds;}
                if(retry && attempt<2)await Task.Delay(TimeSpan.FromSeconds(wait),ct);
            }
            throw new DeliveryUncertain();
        }
        public void Dispose() {http.Dispose();}
    }
}
