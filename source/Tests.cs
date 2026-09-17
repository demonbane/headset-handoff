using System;
using HeadsetHandoff;
class SelfTests {
    static int count;
    static void Check(bool condition,string name) { if(!condition) throw new Exception("FAIL: "+name); count++; Console.WriteLine("PASS: "+name); }
    static void Main() {
        // Representative status packets: unrelated headset settings are zeroed.
        byte[] off={1,0xB0,0,0,0,0,0,0,0,0,0,0,0,0,4,0,0};
        byte[] on={1,0xB0,0,0,0,0,0,0,0,0,0,0,0,0,8,0,0};
        byte[] link={7,0xB5,1,0,8};
        int raw;
        Check(RadioProtocol.TryParse(off,off.Length,out raw) && RadioProtocol.Connected(raw)==false,"Standby is disconnected");
        Check(RadioProtocol.TryParse(on,on.Length,out raw) && RadioProtocol.Connected(raw)==true,"Active link is connected");
        Check(RadioProtocol.TryParse(link,link.Length,out raw) && RadioProtocol.Connected(raw)==true,"Connection event is connected");
        byte[] bluetooth={7,0xB5,4,8,4};
        Check(RadioProtocol.TryParse(bluetooth,bluetooth.Length,out raw) && RadioProtocol.Connected(raw)==false,"Bluetooth bytes cannot impersonate a radio connection");
        Check(!RadioProtocol.TryParse(on,14,out raw),"Truncated query ignored");
        Check(!RadioProtocol.TryParse(link,4,out raw),"Truncated connection event ignored");
        Check(!RadioProtocol.TryParse(new byte[]{7,0xB7,80,100,8},5,out raw),"Battery event cannot impersonate a radio connection");
        Check(!RadioProtocol.TryParse(null,2,out raw),"Null report ignored");
        Check(!RadioProtocol.TryParse(link,100,out raw),"Invalid report length ignored");
        Check(RadioProtocol.Connected(0)==null && RadioProtocol.Connected(255)==null,"Unknown radio values never select speakers");
        var gate=new StateGate(1500);
        Check(!gate.Observe(false,0),"Initial status waits for debounce");
        Check(!gate.Observe(false,1499),"No early switch");
        Check(gate.Observe(false,1500) && gate.Stable==false,"Stable initial disconnect accepted");
        Check(!gate.Observe(false,2000),"Unchanged polling does not fight manual output changes");
        Check(!gate.Observe(true,2100),"Connection starts debounce");
        Check(!gate.Observe(false,2200),"Brief connection cancelled");
        Check(!gate.Observe(false,4000),"Bounce back does not trigger another switch");
        Check(!gate.Observe(true,4100) && gate.Observe(true,5600) && gate.Stable==true,"Sustained connection switches once");
        Check(!gate.Observe(null,5700) && gate.Stable==null,"Missing telemetry cancels any stale switch");
        Check(!gate.Observe(false,5800) && !gate.Observe(null,5900) && gate.Stable==null,"Uncertain disconnect cannot switch");
        Check(!gate.Observe(true,6000) && gate.Observe(true,7500),"Recovery requires a fresh stable observation");
        Check(!ProcessLifetime.ShouldDetach(false,0),"Normal desktop launches stay in the same process");
        Check(!ProcessLifetime.ShouldDetach(true,0x800),"Jobs without kill-on-close need no detachment");
        Check(ProcessLifetime.ShouldDetach(true,0x2000),"Use the Windows broker for kill-on-close jobs without breakaway");
        Check(ProcessLifetime.ShouldDetach(true,0x2800),"Detach from a kill-on-close launcher job");
        Console.WriteLine(count+" tests passed.");
    }
}
