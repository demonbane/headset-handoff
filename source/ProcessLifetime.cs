using System;
using System.Management;
using System.Runtime.InteropServices;

namespace HeadsetHandoff {
// Terminal/automation runners can have nested kill-on-close jobs. CreateProcess
// breakaway may leave an outer job attached, even when the immediate job permits it.
// Windows documents that local Win32_Process.Create children do not inherit jobs:
// https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
public static class ProcessLifetime {
    const uint KillOnClose=0x2000;
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int info,byte[] data,int size,IntPtr returned);
    public static bool ShouldDetach(bool inJob,uint flags) { return inJob && (flags & KillOnClose)!=0; }
    public static bool TryDetach(string executable,string arguments,bool alreadyDetached,Action<string> log) {
        bool inJob;
        if(!IsProcessInJob(new IntPtr(-1),IntPtr.Zero,out inJob)) { log("Cannot inspect launcher lifetime: "+Marshal.GetLastWin32Error()); return false; }
        uint flags=0;
        if(inJob) {
            byte[] data=new byte[144]; // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on x64.
            if(!QueryInformationJobObject(IntPtr.Zero,9,data,data.Length,IntPtr.Zero)) { log("Cannot inspect launcher limits: "+Marshal.GetLastWin32Error()); return false; }
            flags=BitConverter.ToUInt32(data,16);
        }
        log("Launch pid="+System.Diagnostics.Process.GetCurrentProcess().Id+" inJob="+inJob+" jobFlags=0x"+flags.ToString("X"));
        if(alreadyDetached || !ShouldDetach(inJob,flags)) {
            if(ShouldDetach(inJob,flags)) log("Warning: launcher still controls app lifetime; launch from Explorer to run independently.");
            return false;
        }
        try {
            // Local Windows process broker, same user; no elevation or remote connection.
            using(var processClass=new ManagementClass("Win32_Process"))
            using(var startupClass=new ManagementClass("Win32_ProcessStartup"))
            using(var startup=startupClass.CreateInstance())
            using(var input=processClass.GetMethodParameters("Create")) {
                startup["ShowWindow"]=(ushort)0;
                input["CommandLine"]="\""+executable+"\" "+arguments;
                input["CurrentDirectory"]=System.IO.Path.GetDirectoryName(executable);
                input["ProcessStartupInformation"]=startup;
                using(var output=processClass.InvokeMethod("Create",input,null)) {
                    uint result=(uint)output["ReturnValue"];
                    if(result!=0) { log("Independent launch failed: Windows process broker returned "+result); return false; }
                    log("Relaunched independently as pid="+output["ProcessId"]);
                    return true;
                }
            }
        } catch(Exception e) { log("Independent launch failed: "+e.Message); return false; }
    }
}
}
