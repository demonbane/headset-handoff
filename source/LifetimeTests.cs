using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HeadsetHandoff;

// Integration test with harmless, bounded child processes; never starts the tray app
// or changes audio, registry entries, or the user's desktop session.
class LifetimeTests {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct StartupInfo {
        public int Size; public string Reserved,Desktop,Title;
        public int X,Y,XSize,YSize,XChars,YChars,Fill,Flags;
        public short Show,ReservedSize; public IntPtr ReservedData,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint ProcessId,ThreadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int info,byte[] data,int size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static void Check(bool success,string label) { if(!success) throw new Exception(label+" (Windows error "+Marshal.GetLastWin32Error()+")"); }
    static int Main(string[] args) {
        try {
            if(args.Length==3 && args[0]=="--worker") { Worker(args[1],args[2]); return 0; }
            RunCase(false); RunCase(true);
            Console.WriteLine("PASS: lifecycle integration tests"); return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Worker(string folder,string mode) {
        string executable=Process.GetCurrentProcess().MainModule.FileName;
        Action<string> log=s=>File.AppendAllText(Path.Combine(folder,"trace.log"),s+Environment.NewLine);
        if(mode!="baseline" && ProcessLifetime.TryDetach(executable,"--worker \""+folder+"\" independent",mode=="independent",log)) return;
        File.WriteAllText(Path.Combine(folder,"ready.txt"),Process.GetCurrentProcess().Id.ToString());
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<15000 && !File.Exists(Path.Combine(folder,"stop.txt"))) Thread.Sleep(50);
    }
    static void RunCase(bool detach) {
        string folder=Path.Combine(Path.GetTempPath(),"HeadsetHandoff-lifetime-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IntPtr job=IntPtr.Zero; ProcessInfo child=new ProcessInfo(); Process running=null;
        try {
            job=CreateJobObject(IntPtr.Zero,null); Check(job!=IntPtr.Zero,"Create test job");
            byte[] limits=new byte[144]; Array.Copy(BitConverter.GetBytes(0x2800u),0,limits,16,4);
            Check(SetInformationJobObject(job,9,limits,limits.Length),"Set test job kill-on-close and breakaway limits");
            string executable=Process.GetCurrentProcess().MainModule.FileName;
            var start=new StartupInfo(); start.Size=Marshal.SizeOf(typeof(StartupInfo)); start.Flags=1; start.Show=0;
            var command=new StringBuilder("\""+executable+"\" --worker \""+folder+"\" "+(detach ? "fixed" : "baseline"));
            Check(CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000004,IntPtr.Zero,folder,ref start,out child),"Create suspended test child");
            Check(AssignProcessToJobObject(job,child.Process),"Assign test child to job");
            Check(ResumeThread(child.Thread)!=0xFFFFFFFF,"Resume test child");
            string ready=Path.Combine(folder,"ready.txt"); var watch=Stopwatch.StartNew();
            while(!File.Exists(ready) && watch.ElapsedMilliseconds<5000) Thread.Sleep(25);
            Check(File.Exists(ready),"Child reported ready");
            running=Process.GetProcessById(int.Parse(File.ReadAllText(ready)));
            bool inJob; Check(IsProcessInJob(running.Handle,IntPtr.Zero,out inJob),"Inspect child lifetime");
            if(inJob==detach && File.Exists(Path.Combine(folder,"trace.log"))) Console.Error.WriteLine(File.ReadAllText(Path.Combine(folder,"trace.log")));
            Check(inJob!=detach,detach ? "Fixed child is independent of all launcher jobs" : "Baseline child inherits its launcher job");
            CloseHandle(job); job=IntPtr.Zero;
            if(detach) {
                Check(!running.WaitForExit(300),"Fixed child survives closing launcher job");
                Console.WriteLine("PASS: independent child survives launcher job closing");
                File.WriteAllText(Path.Combine(folder,"stop.txt"),"");
                Check(running.WaitForExit(3000),"Independent child exits normally on request");
                Check(running.ExitCode==0,"Independent child exits successfully");
            } else {
                Check(running.WaitForExit(3000),"Baseline child is terminated when launcher job closes");
                Console.WriteLine("PASS: reproduced original kill-on-launcher-close failure");
            }
        } finally {
            if(job!=IntPtr.Zero) CloseHandle(job);
            File.WriteAllText(Path.Combine(folder,"stop.txt"),"");
            if(running!=null) { if(!running.HasExited) running.WaitForExit(3000); running.Dispose(); }
            if(child.Process!=IntPtr.Zero) { if(WaitForSingleObject(child.Process,500)==258) TerminateProcess(child.Process,1); CloseHandle(child.Process); }
            if(child.Thread!=IntPtr.Zero) CloseHandle(child.Thread);
            // The bounded helper may still be finishing after an assertion failure.
            // Delete only the known test files; never recursively delete a computed path.
            foreach(string name in new[]{"ready.txt","stop.txt","trace.log"}) { try { File.Delete(Path.Combine(folder,name)); } catch(IOException) {} }
            try { Directory.Delete(folder,false); } catch(IOException) {}
        }
    }
}
