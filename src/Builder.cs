using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexUsageSentinel {
    public sealed class BuildResult {public int ExitCode;public string Output;}
    public static class Compiler {
        public static async Task<BuildResult> Build(string project) {
            project=Path.GetFullPath(project);
            string[] files={"Version.cs","Core.cs","Monitor.cs","RelayClient.cs","RelaySetupForm.cs","App.cs"};
            if(files.Any(f=>!File.Exists(Path.Combine(project,"src",f))) || !File.Exists(Path.Combine(project,"src","app.manifest")))
                throw new InvalidOperationException("Выберите корневую папку исходников: рядом должны находиться папка src и файл build.ps1.");
            string compiler=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Microsoft.NET","Framework64","v4.0.30319","csc.exe");
            if(!File.Exists(compiler))compiler=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Microsoft.NET","Framework","v4.0.30319","csc.exe");
            if(!File.Exists(compiler))throw new InvalidOperationException("Не найден компилятор .NET Framework. Установите .NET Framework 4.8.");
            string output=Path.Combine(project,"dist","CodexUsageSentinel.exe");Directory.CreateDirectory(Path.GetDirectoryName(output));
            string arguments="/nologo /target:winexe /platform:anycpu /optimize+ /utf8output "+
                "/r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "+
                "/win32manifest:\""+Path.Combine(project,"src","app.manifest")+"\" /out:\""+output+"\" "+
                string.Join(" ",files.Select(f=>"\""+Path.Combine(project,"src",f)+"\""));
            using(var process=new Process {StartInfo=new ProcessStartInfo(compiler,arguments) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=project}}) {
                process.Start();var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdout,stderr);await Task.Run(()=>process.WaitForExit());
                return new BuildResult {ExitCode=process.ExitCode,Output=stdout.Result+stderr.Result+(process.ExitCode==0 ? "\r\nГотово: "+output : "\r\nСборка не выполнена.")};
            }
        }
    }
    public sealed class BuilderForm : Form {
        readonly TextBox folder,log;
        readonly Button build,browse;
        public BuilderForm() {
            Text="Codex Usage Sentinel · Build.exe "+BuildInfo.Version;ClientSize=new Size(710,440);MinimumSize=new Size(680,430);StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(15,19,28);ForeColor=Color.FromArgb(236,241,248);Font=new Font("Segoe UI",10);
            var root=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=1,RowCount=5};Controls.Add(root);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,58));root.RowStyles.Add(new RowStyle(SizeType.Absolute,35));root.RowStyles.Add(new RowStyle(SizeType.Absolute,55));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
            root.Controls.Add(new Label {Text="Собрать программу из исходников\nВыберите папку проекта. Результат появится в dist.",Dock=DockStyle.Fill},0,0);
            string initial=AppDomain.CurrentDomain.BaseDirectory;
            if(!Directory.Exists(Path.Combine(initial,"src")) && Directory.Exists(Path.Combine(initial,"..","src")))initial=Path.GetFullPath(Path.Combine(initial,".."));
            folder=new TextBox {Text=initial,Dock=DockStyle.Fill,BackColor=Color.FromArgb(23,30,43),ForeColor=ForeColor,AccessibleName="Папка исходников"};root.Controls.Add(folder,0,1);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill};
            browse=ActionButton("Выбрать папку…",()=>{using(var dialog=new FolderBrowserDialog()){dialog.SelectedPath=folder.Text;if(dialog.ShowDialog(this)==DialogResult.OK)folder.Text=dialog.SelectedPath;}});actions.Controls.Add(browse);
            build=ActionButton("Собрать EXE",()=>Build());actions.Controls.Add(build);
            actions.Controls.Add(ActionButton("Открыть результат",()=>{try{string path=Path.Combine(Path.GetFullPath(folder.Text),"dist");if(Directory.Exists(path))Process.Start(new ProcessStartInfo("explorer.exe","\""+path+"\"") {UseShellExecute=true});else log.Text="Сначала выполните сборку.";}catch{log.Text="Не удалось открыть папку результата.";}}));root.Controls.Add(actions,0,2);
            log=new TextBox {Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(23,30,43),ForeColor=ForeColor,AccessibleName="Результат сборки",Text="Для обычного запуска скачайте CodexUsageSentinel.exe.\r\nBuild.exe нужен только для сборки из исходников."};root.Controls.Add(log,0,3);
            root.Controls.Add(new Label {Text="Build "+BuildInfo.Version+" · .NET Framework · без загрузки пакетов",Dock=DockStyle.Fill,Padding=new Padding(0,8,0,0)},0,4);
            FormClosing+=(s,e)=>{if(!build.Enabled)e.Cancel=true;};
        }
        Button ActionButton(string text,Action action) {
            var button=new Button {Text=text,AutoSize=true,Height=38,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(37,48,67),ForeColor=ForeColor,Padding=new Padding(8,3,8,3),AccessibleName=text};button.Click+=(s,e)=>action();return button;
        }
        async void Build() {
            build.Enabled=false;browse.Enabled=false;folder.ReadOnly=true;log.Text="Собираю…";
            try{var result=await Compiler.Build(folder.Text);log.Text=result.Output;}
            catch(Exception ex){log.Text=ex is InvalidOperationException ? ex.Message : "Не удалось собрать программу. Проверьте путь, доступ к папке и закройте запущенный EXE из dist.";}
            finally{build.Enabled=true;browse.Enabled=true;folder.ReadOnly=false;}
        }
    }
    public static class BuilderProgram {
        [STAThread] public static int Main(string[] args) {
            if(args.Length==2 && args[0]=="--build") {
                try {return Compiler.Build(args[1]).GetAwaiter().GetResult().ExitCode;} catch {return 1;}
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new BuilderForm());return 0;
        }
    }
}
