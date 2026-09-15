using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HeadsetHandoff {
[DataContract] public sealed class Settings {
    [DataMember] public string HeadsetId;
    [DataMember] public string FallbackId;
    [DataMember] public bool Enabled=true;
    [DataMember] public bool Communications=false;
    public static readonly string FilePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.json");
    public static Settings Load() {
        if(!File.Exists(FilePath)) return new Settings();
        using(var file=File.OpenRead(FilePath)) {
            var settings=(Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(file);
            if(settings==null) throw new InvalidDataException("The settings file is empty.");
            return settings;
        }
    }
    public void Save() {
        string temp=FilePath+".tmp";
        using(var file=File.Create(temp)) { new DataContractJsonSerializer(typeof(Settings)).WriteObject(file,this); file.Flush(true); }
        if(File.Exists(FilePath)) File.Replace(temp,FilePath,FilePath+".bak"); else File.Move(temp,FilePath);
    }
}
public static class AppLog {
    static readonly object gate=new object();
    public static readonly string FilePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"HeadsetHandoff.log");
    public static void Write(string value) {
        lock(gate) {
            try {
                if(File.Exists(FilePath) && new FileInfo(FilePath).Length>1024*1024) {
                    string previous=FilePath+".previous";
                    if(File.Exists(previous)) File.Delete(previous);
                    File.Move(FilePath,previous);
                }
                File.AppendAllText(FilePath,DateTimeOffset.Now.ToString("o")+" "+value+Environment.NewLine);
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
    }
}
public sealed class TrayApp : ApplicationContext {
    readonly NotifyIcon tray=new NotifyIcon();
    readonly ContextMenuStrip menu=new ContextMenuStrip();
    readonly ToolStripMenuItem status=new ToolStripMenuItem("Reading receiver status");
    readonly ToolStripMenuItem current=new ToolStripMenuItem("Windows output");
    readonly ToolStripMenuItem automatic=new ToolStripMenuItem("Automatic switching");
    readonly ToolStripMenuItem headset=new ToolStripMenuItem("When headset connects");
    readonly ToolStripMenuItem fallback=new ToolStripMenuItem("When headset disconnects");
    readonly ToolStripMenuItem communications=new ToolStripMenuItem("Also switch call audio");
    readonly ToolStripMenuItem startup=new ToolStripMenuItem("Start when I sign in");
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly Stopwatch clock=Stopwatch.StartNew();
    StateGate state=new StateGate(1500);
    readonly Settings settings;
    readonly Receiver receiver;
    readonly EventWaitHandle quit;
    readonly Icon icon;
    bool pending=true, closing=false;
    long retryAt;
    string lastStatus="",lastError="";
    const string StartupKey="Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string StartupValue="HeadsetHandoff";
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    static Icon CreateIcon() {
        using(var bitmap=new Bitmap(32,32)) {
            using(var g=Graphics.FromImage(bitmap)) {
                g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using(var pen=new Pen(Color.FromArgb(91,185,245),4)) g.DrawArc(pen,5,4,22,23,180,180);
                using(var brush=new SolidBrush(Color.FromArgb(91,185,245))) { g.FillRectangle(brush,3,14,7,13); g.FillRectangle(brush,22,14,7,13); }
            }
            IntPtr handle=bitmap.GetHicon();
            try { using(var temp=Icon.FromHandle(handle)) return (Icon)temp.Clone(); } finally { DestroyIcon(handle); }
        }
    }
    public TrayApp(Settings value,EventWaitHandle quitEvent) {
        settings=value; quit=quitEvent;
        icon=CreateIcon(); tray.Icon=icon; tray.Text="Headset Handoff";
        status.Enabled=false; current.Enabled=false;
        menu.Items.Add(status); menu.Items.Add(current); menu.Items.Add(new ToolStripSeparator());
        automatic.Checked=settings.Enabled;
        automatic.Click+=delegate { settings.Enabled=!settings.Enabled; Save(); automatic.Checked=settings.Enabled; Resync(); };
        menu.Items.Add(automatic); menu.Items.Add(headset); menu.Items.Add(fallback);
        communications.Checked=settings.Communications;
        communications.Click+=delegate { settings.Communications=!settings.Communications; Save(); communications.Checked=settings.Communications; Resync(); };
        menu.Items.Add(communications);
        menu.Items.Add(new ToolStripSeparator());
        var sync=new ToolStripMenuItem("Apply current headset state"); sync.Click+=delegate { Resync(); }; menu.Items.Add(sync);
        startup.Click+=delegate { ToggleStartup(); }; menu.Items.Add(startup);
        var log=new ToolStripMenuItem("View activity log"); log.Click+=delegate { try { Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute=true }); } catch(Exception e) { ShowError(e); } }; menu.Items.Add(log);
        menu.Items.Add(new ToolStripSeparator());
        var exit=new ToolStripMenuItem("Exit"); exit.Click+=delegate { ExitThread(); }; menu.Items.Add(exit);
        menu.Opening+=delegate { PopulateDevices(); RefreshStartup(); };
        tray.ContextMenuStrip=menu; tray.Visible=true;
        receiver=new Receiver(AppLog.Write);
        timer.Interval=250; timer.Tick+=delegate { Tick(); }; timer.Start();
        SystemEvents.PowerModeChanged+=PowerChanged;
        SystemEvents.SessionEnding+=SessionEnding;
        AppLog.Write("Started Headset Handoff 1.0; automatic="+settings.Enabled+"; call audio="+settings.Communications);
    }
    void PowerChanged(object sender,PowerModeChangedEventArgs e) {
        // SystemEvents can run on a worker thread. Tick handles the resync request.
        if(e.Mode==PowerModes.Resume) Interlocked.Exchange(ref resumeRequested,1);
    }
    int resumeRequested;
    void SessionEnding(object sender,SessionEndingEventArgs e) { quit.Set(); }
    void Resync() { state=new StateGate(1500); pending=true; retryAt=0; lastError=""; }
    void Save() { try { settings.Save(); } catch(Exception e) { ShowError(e); } }
    void ShowError(Exception e) { AppLog.Write("Error: "+e.Message); MessageBox.Show(e.Message,"Headset Handoff",MessageBoxButtons.OK,MessageBoxIcon.Error); }
    void PopulateDevices() {
        try {
            headset.DropDownItems.Clear(); fallback.DropDownItems.Clear();
            var devices=Audio.List();
            foreach(var device in devices) {
                string id=device.Id;
                var h=new ToolStripMenuItem(device.Name); h.Checked=id==settings.HeadsetId; h.Enabled=id!=settings.FallbackId;
                h.Click+=delegate { settings.HeadsetId=id; Save(); Resync(); }; headset.DropDownItems.Add(h);
                var f=new ToolStripMenuItem(device.Name); f.Checked=id==settings.FallbackId; f.Enabled=id!=settings.HeadsetId;
                f.Click+=delegate { settings.FallbackId=id; Save(); Resync(); }; fallback.DropDownItems.Add(f);
            }
            string defaultId=Audio.Default(0); AudioDevice selected=devices.Find(d=>d.Id==defaultId);
            current.Text="Windows output: "+(selected==null ? "unavailable" : selected.Name);
        } catch(Exception e) { AppLog.Write("Device menu: "+e.Message); }
    }
    void RefreshStartup() {
        using(var key=Registry.CurrentUser.OpenSubKey(StartupKey)) { startup.Checked=key!=null && string.Equals(key.GetValue(StartupValue) as string,"\""+Application.ExecutablePath+"\"",StringComparison.OrdinalIgnoreCase); }
    }
    void ToggleStartup() {
        try {
            RefreshStartup();
            using(var key=Registry.CurrentUser.CreateSubKey(StartupKey)) { if(startup.Checked) key.DeleteValue(StartupValue,false); else key.SetValue(StartupValue,"\""+Application.ExecutablePath+"\""); }
            RefreshStartup(); AppLog.Write("Start at sign-in="+startup.Checked);
        } catch(Exception e) { ShowError(e); }
    }
    void Tick() {
        if(closing) return;
        if(quit.WaitOne(0)) { ExitThread(); return; }
        if(Interlocked.Exchange(ref resumeRequested,0)==1) { AppLog.Write("Windows resumed; waiting for fresh headset state"); receiver.Invalidate(); Resync(); }
        RadioSnapshot snapshot=receiver.Snapshot();
        long now=clock.ElapsedMilliseconds;
        bool changed=state.Observe(snapshot.Connected,now);
        if(changed) { pending=true; retryAt=0; lastError=""; }
        string label=settings.Enabled ? snapshot.Detail : "Paused — "+snapshot.Detail;
        bool configured=!string.IsNullOrEmpty(settings.HeadsetId) && !string.IsNullOrEmpty(settings.FallbackId) && settings.HeadsetId!=settings.FallbackId;
        if(!configured) label="Choose both audio outputs in the tray menu";
        if(label!=lastStatus) { status.Text=label; tray.Text=label.Length>63 ? label.Substring(0,63) : label; lastStatus=label; }
        if(!settings.Enabled || !configured || !state.Stable.HasValue || !pending || now<retryAt) return;
        string target=state.Stable.Value ? settings.HeadsetId : settings.FallbackId;
        try {
            Audio.SetDefault(target,settings.Communications);
            pending=false;
            status.Text=label;
            AppLog.Write("Verified default output -> "+(state.Stable.Value ? "HEADSET " : "SPEAKERS ")+target);
            lastError="";
        } catch(Exception e) {
            retryAt=now+5000;
            if(e.Message!=lastError) { lastError=e.Message; AppLog.Write("Switch pending: "+e.Message); }
            status.Text="Waiting for selected audio output";
        }
    }
    protected override void ExitThreadCore() {
        if(closing) return; closing=true;
        timer.Stop(); SystemEvents.PowerModeChanged-=PowerChanged; SystemEvents.SessionEnding-=SessionEnding;
        receiver.Dispose(); tray.Visible=false; tray.Dispose(); icon.Dispose(); menu.Dispose(); timer.Dispose();
        AppLog.Write("Stopped; Windows output left at its current selection");
        base.ExitThreadCore();
    }
}
static class Program {
    [STAThread] static void Main(string[] args) {
        if(args.Length>0 && args[0]=="--exit") {
            try { using(var e=EventWaitHandle.OpenExisting("Local\\HeadsetHandoff.Stop")) e.Set(); } catch(WaitHandleCannotBeOpenedException) {}
            return;
        }
        bool owner;
        using(var mutex=new Mutex(true,"Local\\HeadsetHandoff.SingleInstance",out owner)) {
            if(!owner) return;
            try {
                using(var quit=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\HeadsetHandoff.Stop")) {
                    quit.Reset(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Settings settings=Settings.Load();
                    Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e) { AppLog.Write("UI error: "+e.Exception); };
                    Application.Run(new TrayApp(settings,quit));
                }
            } catch(Exception e) { AppLog.Write("Fatal error: "+e); MessageBox.Show(e.Message,"Headset Handoff",MessageBoxButtons.OK,MessageBoxIcon.Error); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
}
