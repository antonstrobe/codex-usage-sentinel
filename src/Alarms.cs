using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexUsageSentinel {
    public sealed class AlarmRule {
        public string Id=Guid.NewGuid().ToString("N");
        public int Percent=10;
        public int MessageCount=10;
        public int IntervalSeconds=2;
        public bool Continuous;
        public bool Enabled=true;
        public string Signature {get{return Percent+":"+MessageCount+":"+IntervalSeconds+":"+Continuous+":"+Enabled;}}
        public static string MessageText(int count) {int last=count%10,tail=count%100;return count+" "+(tail>=11 && tail<=14 ? "сообщений" : last==1 ? "сообщение" : last>=2 && last<=4 ? "сообщения" : "сообщений");}
        public string CountText {get{return Continuous ? "До восстановления" : MessageText(MessageCount);}}
        public string IntervalText {get{return IntervalSeconds%3600==0 ? (IntervalSeconds/3600)+" ч" : IntervalSeconds%60==0 ? (IntervalSeconds/60)+" мин" : IntervalSeconds+" сек";}}
        public static List<AlarmRule> Defaults() {return new List<AlarmRule> {
            new AlarmRule {Id="default-10",Percent=10,MessageCount=10},
            new AlarmRule {Id="default-5",Percent=5,MessageCount=10},
            new AlarmRule {Id="default-3",Percent=3,MessageCount=50},
            new AlarmRule {Id="default-2",Percent=2,Continuous=true}
        };}
        public static List<AlarmRule> CheckedCopy(IEnumerable<AlarmRule> rules) {
            if(rules==null)throw new InvalidOperationException("Список будильников не найден.");
            var copy=new List<AlarmRule>();var ids=new HashSet<string>();
            foreach(var rule in rules) {
                if(rule==null || string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))throw new InvalidOperationException("У каждого будильника должен быть отдельный идентификатор.");
                if(rule.Percent<0 || rule.Percent>100)throw new InvalidOperationException("Процент должен быть от 0 до 100.");
                if(rule.MessageCount<1 || rule.MessageCount>10000)throw new InvalidOperationException("Количество сообщений должно быть от 1 до 10000.");
                if(rule.IntervalSeconds<2 || rule.IntervalSeconds>86400)throw new InvalidOperationException("Интервал должен быть от 2 секунд до 24 часов.");
                copy.Add(new AlarmRule {Id=rule.Id,Percent=rule.Percent,MessageCount=rule.MessageCount,IntervalSeconds=rule.IntervalSeconds,Continuous=rule.Continuous,Enabled=rule.Enabled});
            }
            return copy;
        }
    }
    public sealed class AlertState {
        public int Version=1;
        public string AccountId="";
        public Dictionary<string,WindowState> Windows=new Dictionary<string,WindowState>();
    }
    public sealed class WindowState {
        // Retained only to read pending series from builds 1.1 and 1.2.
        public bool Fired10,Fired5,Fired3;
        public int Pending,Stage;
        public long Generation;
        public Dictionary<string,RuleState> Rules=new Dictionary<string,RuleState>();
    }
    public sealed class RuleState {
        public string Signature="";
        public bool Fired,Active;
        public int Pending;
        public long Generation;
        public DateTime NextSendUtc;
    }
    public sealed class AlertItem {
        public string Key,RuleId;
        public int Stage,MessageCount,IntervalSeconds;
        public long Generation,Revision;
        public bool Continuous;
        public DateTime NextSendUtc;
        public bool Due(DateTime now) {return now>=NextSendUtc;}
    }
    // A lower threshold supersedes higher pending series in the same usage window.
    // Next returns a candidate; the monitor schedules it using Due, without blocking polling.
    public sealed class AlertPolicy {
        public AlertState State;
        public Usage Current;
        List<AlarmRule> rules;
        long revision;
        public AlertPolicy(AlertState state,IEnumerable<AlarmRule> configured=null) {
            State=state??new AlertState();if(State.Windows==null)State.Windows=new Dictionary<string,WindowState>();
            rules=AlarmRule.CheckedCopy(configured??AlarmRule.Defaults());
            foreach(var window in State.Windows.Values) {
                if(window.Rules==null)window.Rules=new Dictionary<string,RuleState>();
                if(State.Version<2)foreach(var rule in AlarmRule.Defaults()) {
                    window.Rules[rule.Id]=new RuleState {Signature=rule.Signature,
                        Fired=rule.Percent==10 ? window.Fired10 : rule.Percent==5 ? window.Fired5 : rule.Percent==3 ? window.Fired3 : window.Stage==2,
                        Active=rule.Continuous && window.Stage==2,
                        Pending=window.Stage==rule.Percent && !rule.Continuous ? Math.Max(0,window.Pending) : 0,Generation=window.Generation};
                }
            }
            State.Version=2;
        }
        public void SetRules(IEnumerable<AlarmRule> configured) {
            var next=AlarmRule.CheckedCopy(configured);
            if(rules.Select(r=>r.Id+"="+r.Signature).SequenceEqual(next.Select(r=>r.Id+"="+r.Signature)))return;
            rules=next;revision++;
            if(Current!=null)Update(Current);
        }
        public void Update(Usage usage) {
            if(usage.AccountId!="" && State.AccountId!="" && usage.AccountId!=State.AccountId)State.Windows.Clear();
            if(usage.AccountId!="")State.AccountId=usage.AccountId;
            Current=usage;
            foreach(var key in State.Windows.Keys.Where(k=>!usage.Core.Any(w=>w.Key==k)).ToList())State.Windows.Remove(key);
            foreach(var window in usage.Core) {
                WindowState state;
                if(!State.Windows.TryGetValue(window.Key,out state))State.Windows[window.Key]=state=new WindowState();
                foreach(var id in state.Rules.Keys.Where(id=>!rules.Any(r=>r.Id==id)).ToList())state.Rules.Remove(id);
                foreach(var rule in rules) {
                    RuleState saved;
                    if(!state.Rules.TryGetValue(rule.Id,out saved))state.Rules[rule.Id]=saved=new RuleState();
                    if(saved.Signature!=rule.Signature) {
                        state.Rules[rule.Id]=saved=new RuleState {Signature=rule.Signature,Generation=saved.Generation+1};
                    }
                    if(window.Remaining>rule.Percent && (saved.Fired || saved.Pending>0 || saved.Active)) {
                        saved.Fired=false;saved.Pending=0;saved.Active=false;saved.Generation++;saved.NextSendUtc=DateTime.MinValue;
                    }
                }
                var reached=rules.Where(r=>r.Enabled && window.Remaining<=r.Percent).ToList();
                var fresh=reached.Where(r=>!state.Rules[r.Id].Fired).ToList();
                if(fresh.Count==0)continue;
                int stage=reached.Where(r=>!state.Rules[r.Id].Fired || state.Rules[r.Id].Pending>0 || state.Rules[r.Id].Active).Min(r=>r.Percent);
                foreach(var rule in fresh) {
                    var saved=state.Rules[rule.Id];saved.Fired=true;saved.Generation++;
                    if(rule.Percent==stage) {saved.Pending=rule.Continuous ? 0 : rule.MessageCount;saved.Active=rule.Continuous;saved.NextSendUtc=DateTime.MinValue;}
                }
                foreach(var rule in reached.Where(r=>r.Percent>stage)) {
                    var saved=state.Rules[rule.Id];
                    if(saved.Pending>0 || saved.Active) {saved.Pending=0;saved.Active=false;saved.Generation++;}
                }
            }
        }
        public AlertItem Next(DateTime now) {
            if(Current==null || now-Current.CheckedUtc>TimeSpan.FromSeconds(90))return null;
            foreach(var window in Current.Core.OrderBy(w=>w.Remaining)) {
                var state=State.Windows[window.Key];
                var rule=rules.Where(r=>r.Enabled && window.Remaining<=r.Percent && state.Rules.ContainsKey(r.Id) &&
                    (state.Rules[r.Id].Pending>0 || state.Rules[r.Id].Active)).OrderBy(r=>r.Percent).ThenBy(r=>state.Rules[r.Id].NextSendUtc).FirstOrDefault();
                if(rule==null)continue;
                var saved=state.Rules[rule.Id];
                return new AlertItem {Key=window.Key,RuleId=rule.Id,Stage=rule.Percent,MessageCount=rule.MessageCount,IntervalSeconds=rule.IntervalSeconds,
                    Continuous=rule.Continuous,Generation=saved.Generation,Revision=revision,NextSendUtc=saved.NextSendUtc};
            }
            return null;
        }
        public bool IsCurrent(AlertItem item,DateTime now) {
            var next=Next(now);return next!=null && next.Key==item.Key && next.RuleId==item.RuleId && next.Generation==item.Generation && next.Revision==item.Revision;
        }
        public void Acknowledge(AlertItem item) {Acknowledge(item,DateTime.UtcNow);}
        public void Acknowledge(AlertItem item,DateTime now) {
            WindowState window;RuleState state;
            if(item.Revision!=revision || !State.Windows.TryGetValue(item.Key,out window) || !window.Rules.TryGetValue(item.RuleId,out state) || state.Generation!=item.Generation)return;
            if(!item.Continuous && state.Pending>0)state.Pending--;
            state.NextSendUtc=now.AddSeconds(item.IntervalSeconds);
        }
    }
}
