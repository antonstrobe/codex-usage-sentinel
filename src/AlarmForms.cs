using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageSentinel {
    public sealed class AlarmEditorForm : Form {
        readonly AlarmRule original;
        readonly NumericUpDown percent,count,interval;
        readonly ComboBox unit;
        readonly Button repeat;
        readonly ToolTip tips=Theme.Tooltips();
        readonly Label preview;
        bool continuous;
        public AlarmRule Result;
        public AlarmEditorForm(AlarmRule rule,bool isNew) {
            original=AlarmRule.CheckedCopy(new[]{rule})[0];continuous=original.Continuous;
            Text=isNew ? "Новый будильник" : "Изменить будильник";BackColor=Theme.Bg;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);
            ClientSize=new Size(510,570);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;
            HandleCreated+=(s,e)=>Theme.DarkTitle(this);
            var flow=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24)};Controls.Add(flow);
            flow.Controls.Add(Theme.Label(isNew ? "Новый будильник" : "Изменить будильник",20,Theme.Text,FontStyle.Bold));
            flow.Controls.Add(Theme.Label("Когда останется столько процентов или меньше",10,Theme.Muted));
            percent=Number(0,100,original.Percent,140,"Остаток лимита в процентах");flow.Controls.Add(percent);
            flow.Controls.Add(Theme.Label("Количество сообщений",10,Theme.Text));
            count=Number(1,10000,original.MessageCount,140,"Количество сообщений в серии");flow.Controls.Add(count);
            flow.Controls.Add(Theme.Label("Интервал между сообщениями",10,Theme.Text));
            var timeRow=new FlowLayoutPanel {Width=460,Height=47,WrapContents=false,Margin=new Padding(0)};
            interval=Number(2,86400,original.IntervalSeconds,140,"Интервал между сообщениями");timeRow.Controls.Add(interval);
            unit=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=25,Width=140,BackColor=Theme.Panel,ForeColor=Theme.Text,Font=new Font("Segoe UI",12),Margin=new Padding(10,0,0,0),AccessibleName="Единица интервала"};
            unit.Items.AddRange(new object[]{"сек","мин","ч"});
            unit.DrawItem+=(s,e)=>{using(var brush=new SolidBrush((e.State&DrawItemState.Selected)!=0 ? Theme.ButtonBg : Theme.Panel))e.Graphics.FillRectangle(brush,e.Bounds);if(e.Index>=0)TextRenderer.DrawText(e.Graphics,unit.Items[e.Index].ToString(),unit.Font,e.Bounds,Theme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);};
            unit.SelectedIndex=0;timeRow.Controls.Add(unit);flow.Controls.Add(timeRow);
            if(original.IntervalSeconds%3600==0){unit.SelectedIndex=2;interval.Minimum=1;interval.Maximum=24;interval.Value=original.IntervalSeconds/3600;}
            else if(original.IntervalSeconds%60==0){unit.SelectedIndex=1;interval.Minimum=1;interval.Maximum=1440;interval.Value=original.IntervalSeconds/60;}
            repeat=Theme.Button("",()=>{continuous=!continuous;UpdatePreview();});flow.Controls.Add(repeat);
            preview=new Label {Width=460,Height=68,ForeColor=Theme.Muted,Margin=new Padding(0,8,0,10)};flow.Controls.Add(preview);
            var actions=new FlowLayoutPanel {Width=460,Height=48,WrapContents=false,Margin=new Padding(0)};
            var save=Theme.Button("Сохранить",()=>Save(),true);var cancel=Theme.Button("Отмена",()=>Close());actions.Controls.Add(save);actions.Controls.Add(cancel);flow.Controls.Add(actions);
            AcceptButton=save;CancelButton=cancel;
            Theme.Describe(save,tips,"Сохранить настройки будильника. Если порог уже достигнут, серия может начаться сразу после сохранения.");
            Theme.Describe(cancel,tips,"Закрыть окно без сохранения изменений.");
            unit.SelectedIndexChanged+=(s,e)=>{interval.Minimum=unit.SelectedIndex==0 ? 2 : 1;interval.Maximum=unit.SelectedIndex==2 ? 24 : unit.SelectedIndex==1 ? 1440 : 86400;UpdatePreview();};
            percent.ValueChanged+=(s,e)=>UpdatePreview();count.ValueChanged+=(s,e)=>UpdatePreview();interval.ValueChanged+=(s,e)=>UpdatePreview();UpdatePreview();
        }
        static NumericUpDown Number(int min,int max,int value,int width,string label) {
            return new NumericUpDown {Minimum=min,Maximum=max,Value=value,Width=width,BackColor=Theme.Panel,ForeColor=Theme.Text,Font=new Font("Segoe UI",12),BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(0,0,0,12),AccessibleName=label};
        }
        int Seconds {get{return (int)interval.Value*(unit.SelectedIndex==2 ? 3600 : unit.SelectedIndex==1 ? 60 : 1);}}
        void UpdatePreview() {
            if(preview==null)return;
            count.Enabled=!continuous;
            Theme.ToggleState(repeat,tips,continuous,continuous ? "Непрерывный повтор включён" : "Непрерывный повтор выключен",
                continuous ? "Выбран повтор до восстановления процента выше порога. Нажмите, чтобы выбрать конечное количество сообщений. Изменение применяется после сохранения." : "Выбрана серия с заданным числом сообщений. Нажмите, чтобы повторять до восстановления процента выше порога. Изменение применяется после сохранения.");
            preview.Text="При остатке ≤ "+percent.Value+"%: "+(continuous ? "повтор до восстановления" : AlarmRule.MessageText((int)count.Value))+", интервал "+interval.Value+" "+unit.Text+".\nИзменения применятся после сохранения. Минимальный интервал — 2 секунды.";
        }
        void Save() {
            try {Result=AlarmRule.CheckedCopy(new[]{new AlarmRule {Id=original.Id,Percent=(int)percent.Value,MessageCount=(int)count.Value,IntervalSeconds=Seconds,Continuous=continuous,Enabled=original.Enabled}})[0];DialogResult=DialogResult.OK;Close();}
            catch(InvalidOperationException ex){MessageBox.Show(this,ex.Message,"Проверьте будильник");}
        }
        protected override void Dispose(bool disposing) {if(disposing)tips.Dispose();base.Dispose(disposing);}
    }
    public sealed class AlarmsForm : Form {
        readonly Monitor monitor;
        readonly ListView list;
        readonly Label status;
        readonly Button edit,remove,toggle;
        readonly ToolTip tips=Theme.Tooltips();
        public AlarmsForm(Monitor monitor) {
            this.monitor=monitor;Text="Будильники по процентам";BackColor=Theme.Bg;ForeColor=Theme.Text;Font=new Font("Segoe UI",10);
            ClientSize=new Size(760,530);MinimumSize=new Size(720,500);StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;
            HandleCreated+=(s,e)=>Theme.DarkTitle(this);
            var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(24)};Controls.Add(root);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,80));root.RowStyles.Add(new RowStyle(SizeType.Absolute,96));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,67));
            var header=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};header.Controls.Add(Theme.Label("Будильники по процентам",20,Theme.Text,FontStyle.Bold));
            header.Controls.Add(Theme.Label("Добавляйте события, открывайте их для изменения или удаляйте.",10,Theme.Muted));root.Controls.Add(header,0,0);
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill};
            var add=Theme.Button("+ Добавить",()=>Edit(true),true);buttons.Controls.Add(add);
            edit=Theme.Button("Открыть…",()=>Edit(false));buttons.Controls.Add(edit);
            remove=Theme.Button("Удалить",()=>Delete());buttons.Controls.Add(remove);buttons.SetFlowBreak(remove,true);
            toggle=Theme.Button("Выберите будильник",()=>Toggle());buttons.Controls.Add(toggle);root.Controls.Add(buttons,0,1);
            Theme.Describe(add,tips,"Создать будильник: процент, количество сообщений и интервал.");Theme.Describe(edit,tips,"Открыть выбранный будильник для изменения. Также можно дважды нажать строку.");Theme.Describe(remove,tips,"Удалить выбранный будильник и остановить оставшуюся серию. Уже начатая отправка может завершиться.");
            list=new ListView {Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BackColor=Theme.Panel,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle,OwnerDraw=true,AccessibleName="Список будильников",AccessibleDescription="Выберите правило. Двойное нажатие открывает его настройки."};
            list.Columns.Add("Осталось",105);list.Columns.Add("Сообщения",190);list.Columns.Add("Интервал",130);list.Columns.Add("Состояние",190);
            list.Resize+=(s,e)=>{list.Columns[3].Width=Math.Max(130,list.ClientSize.Width-427);};
            list.DrawColumnHeader+=(s,e)=>{using(var brush=new SolidBrush(Theme.ButtonBg))e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,e.Header.Text,Font,e.Bounds,Theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
            list.DrawItem+=(s,e)=>{};
            list.DrawSubItem+=(s,e)=>{
                bool selected=e.Item.Selected;using(var brush=new SolidBrush(selected ? Color.FromArgb(30,69,57) : Theme.Panel))e.Graphics.FillRectangle(brush,e.Bounds);
                var rule=e.Item.Tag as AlarmRule;var color=e.ColumnIndex==3 && rule!=null && rule.Enabled ? Theme.Green : Theme.Text;
                var bounds=e.Bounds;bounds.Inflate(-5,0);TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Font,bounds,color,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
                if(selected && e.ColumnIndex==0)ControlPaint.DrawFocusRectangle(e.Graphics,e.Item.Bounds,Theme.Green,Theme.Panel);
            };
            list.SelectedIndexChanged+=(s,e)=>Selection();list.DoubleClick+=(s,e)=>Edit(false);list.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){e.Handled=true;Edit(false);}};root.Controls.Add(list,0,2);
            status=new Label {Dock=DockStyle.Fill,ForeColor=Theme.Muted,Padding=new Padding(0,12,0,0)};root.Controls.Add(status,0,3);RefreshRules(null);
        }
        AlarmRule Selected {get{return list.SelectedItems.Count==1 ? list.SelectedItems[0].Tag as AlarmRule : null;}}
        void Selection() {
            var rule=Selected;edit.Enabled=remove.Enabled=toggle.Enabled=rule!=null;
            Theme.ToggleState(toggle,tips,rule!=null && rule.Enabled,rule==null ? "Выберите будильник" : rule.Enabled ? "Будильник включён" : "Будильник выключен",
                rule==null ? "Выберите будильник в списке." : rule.Enabled ? "Сейчас будильник включён. Нажмите, чтобы отключить его и остановить оставшиеся сообщения." : "Сейчас будильник выключен. Нажмите, чтобы включить; при достигнутом пороге серия начнётся сразу.");
        }
        void RefreshRules(string id) {
            status.ForeColor=Theme.Muted;
            var rules=monitor.AlarmRules;list.BeginUpdate();list.Items.Clear();
            foreach(var rule in rules.OrderByDescending(r=>r.Percent)) {
                var row=new ListViewItem(new[]{"≤ "+rule.Percent+"%",rule.CountText,rule.IntervalText,rule.Enabled ? "✓ Включён" : "○ Выключен"}) {Tag=rule};list.Items.Add(row);if(rule.Id==id)row.Selected=true;
            }
            list.EndUpdate();Selection();
            status.Text=rules.Count==0 ? "Будильников нет. Нажмите «+ Добавить», чтобы получать уведомления по процентам." : "Настройки сохраняются сразу. При перескоке порогов действует самый низкий.\nПосле восстановления процента выше порога будильник снова готов к срабатыванию.";
        }
        void Edit(bool create) {
            var selected=Selected;if(!create && selected==null)return;
            using(var editor=new AlarmEditorForm(create ? new AlarmRule() : selected,create)) {
                if(editor.ShowDialog(this)!=DialogResult.OK)return;
                var rules=monitor.AlarmRules;if(create)rules.Add(editor.Result);else rules[rules.FindIndex(r=>r.Id==selected.Id)]=editor.Result;
                Save(rules,editor.Result.Id);
            }
        }
        void Delete() {var rule=Selected;if(rule==null)return;var rules=monitor.AlarmRules;rules.RemoveAll(r=>r.Id==rule.Id);Save(rules,null);}
        void Toggle() {var selected=Selected;if(selected==null)return;var rules=monitor.AlarmRules;var rule=rules.First(r=>r.Id==selected.Id);rule.Enabled=!rule.Enabled;Save(rules,rule.Id);}
        void Save(List<AlarmRule> rules,string selected) {
            try{monitor.SetAlarms(rules);RefreshRules(selected);}
            catch(Exception ex){status.Text=ex is InvalidOperationException ? ex.Message : "Не удалось сохранить настройки. Изменение не применено.";status.ForeColor=Theme.Red;}
        }
        protected override void Dispose(bool disposing) {if(disposing)tips.Dispose();base.Dispose(disposing);}
    }
}
