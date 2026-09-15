using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HeadsetHandoff {
public static class RadioProtocol {
    // Nova Elite: status reply 01 B0, radio byte 14; pushed connection event 07 B5, radio byte 4.
    public static bool TryParse(byte[] data, int length, out int radio) {
        radio=0;
        if(data==null || length>data.Length || length<2) return false;
        if(data[0]==1 && data[1]==0xB0 && length>=17) radio=data[14];
        else if(data[0]==7 && data[1]==0xB5 && length>=5) radio=data[4];
        else return false;
        return true;
    }
    public static bool? Connected(int radio) {
        if(radio==8) return true;
        if(radio==1 || radio==2 || radio==4) return false;
        return null;
    }
}
public sealed class StateGate {
    readonly long delay;
    bool? candidate;
    long since;
    public bool? Stable { get; private set; }
    public StateGate(long delayMilliseconds) { delay=delayMilliseconds; }
    public bool Observe(bool? value,long now) {
        if(!value.HasValue) { candidate=null; Stable=null; return false; }
        if(candidate!=value) { candidate=value; since=now; }
        if(now-since<delay || Stable==value) return false;
        Stable=value;
        return true;
    }
}
public sealed class RadioSnapshot {
    public bool? Connected;
    public string Detail;
}
public sealed class Receiver : IDisposable {
    readonly object gate=new object();
    readonly ManualResetEvent stop=new ManualResetEvent(false);
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly Action<string> log;
    readonly Thread worker;
    readonly List<FileStream> readers=new List<FileStream>();
    SafeFileHandle command;
    HidInfo commandInfo;
    bool? connected;
    string detail="Waiting for receiver";
    long received=-1;
    volatile bool fault;
    volatile int generation;
    int lastRadio=-1;
    public Receiver(Action<string> logger) {
        log=logger; worker=new Thread(Run); worker.IsBackground=true; worker.Name="Headset receiver"; worker.Start();
    }
    public RadioSnapshot Snapshot() {
        lock(gate) {
            if(received>=0 && clock.ElapsedMilliseconds-received>12000) return new RadioSnapshot { Connected=null, Detail="Waiting for a fresh receiver status" };
            return new RadioSnapshot { Connected=connected,Detail=detail };
        }
    }
    public void Invalidate() { Publish(null,"Waiting for a fresh receiver status",false); }
    void Publish(bool? value,string description,bool expires) {
        lock(gate) { connected=value; detail=description; received=expires ? clock.ElapsedMilliseconds : -1; }
    }
    void Report(byte[] data,int length,int session) {
        if(session!=generation) return;
        int raw; if(!RadioProtocol.TryParse(data,length,out raw)) return;
        bool? state=RadioProtocol.Connected(raw);
        lock(gate) {
            if(session!=generation) return;
            connected=state; detail=state==true ? "Headset connected" : state==false ? "Headset disconnected" : "Unrecognized receiver status";
            received=clock.ElapsedMilliseconds;
            if(lastRadio!=raw) { lastRadio=raw; log("Radio status="+raw+" ("+detail+")"); }
        }
    }
    void StartReader(HidInfo info,int session) {
        var stream=new FileStream(HidNative.Open(info.Path,true,true),FileAccess.ReadWrite,info.InputLength,true);
        readers.Add(stream);
        var thread=new Thread(delegate() {
            try {
                byte[] data=new byte[info.InputLength];
                while(!stop.WaitOne(0) && session==generation) { int n=stream.Read(data,0,data.Length); if(n==0) throw new IOException("Receiver closed the status stream"); Report(data,n,session); }
            } catch(Exception e) { if(session==generation && !stop.WaitOne(0)) { log("Receiver read: "+e.Message); fault=true; } }
        });
        thread.IsBackground=true; thread.Name="Headset status reader"; thread.Start();
    }
    bool Attach() {
        var all=HidNative.Enumerate();
        var commands=all.FindAll(d=>d.Vendor==0x1038 && d.Product==0x2270 && d.UsagePage==0xFFC0 && d.Usage==1);
        if(commands.Count==0) { Publish(false,"Receiver unplugged",false); return false; }
        if(commands.Count!=1) { Publish(null,"More than one matching receiver",false); return false; }
        commandInfo=commands[0];
        // Both collections must belong to the same physical receiver.
        var eventDevices=all.FindAll(d=>d.Vendor==0x1038 && d.Product==0x2270 && d.UsagePage==0xFF00 && d.Usage==1);
        HidInfo events=eventDevices.Count==1 ? eventDevices[0] : null;
        if(commandInfo.InputLength<17 || commandInfo.FeatureLength<64 || events==null || events.InputLength<5)
            throw new InvalidOperationException("The receiver's HID layout is different from the tested Nova Elite layout.");
        fault=false; lastRadio=-1; int session=++generation;
        Publish(null,"Reading receiver status",false);
        StartReader(commandInfo,session); StartReader(events,session);
        command=HidNative.Open(commandInfo.Path,true,false);
        log("Receiver attached: 1038:2270; direct HID status, no GG/Sonar dependency");
        return true;
    }
    void Detach() {
        ++generation;
        foreach(var stream in readers) {
            try { HidNative.CancelIoEx(stream.SafeFileHandle,IntPtr.Zero); stream.Dispose(); } catch(Exception) {}
        }
        readers.Clear();
        if(command!=null) { command.Dispose(); command=null; }
    }
    void Run() {
        try {
            while(!stop.WaitOne(0)) {
                try {
                    if(!Attach()) { if(stop.WaitOne(3000)) break; continue; }
                    // Allow the transmitter firmware to initialize after USB enumeration.
                    if(stop.WaitOne(5000)) break;
                    long started=clock.ElapsedMilliseconds;
                    while(!stop.WaitOne(0) && !fault) {
                        byte[] request=new byte[commandInfo.FeatureLength]; request[0]=1; request[1]=0xB0;
                        if(!HidNative.HidD_SetFeature(command,request,request.Length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Receiver status query failed");
                        if(stop.WaitOne(3000)) break;
                        long last; lock(gate) { last=received; }
                        if(clock.ElapsedMilliseconds-(last<0 ? started : last)>20000) throw new IOException("Receiver status timed out; reconnecting");
                    }
                } catch(Exception e) { log("Receiver: "+e.Message); Publish(null,"Receiver temporarily unavailable",false); }
                finally { Detach(); }
                if(stop.WaitOne(1000)) break;
            }
        } finally { Detach(); }
    }
    public void Dispose() {
        stop.Set();
        // Worker and readers are background threads, so a stalled USB driver cannot hold the app open.
        worker.Join(1500);
    }
}
}
