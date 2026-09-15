using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HeadsetHandoff {
public sealed class AudioDevice {
    public string Id, Name;
    public override string ToString() { return Name; }
}
public static class Audio {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class EnumeratorClass {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out ICollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICollection {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevice {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, out IntPtr value);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size=24)] struct PropVariant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyStore {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyClass {}
    // Windows PolicyConfig is an undocumented COM interface. Only SetDefaultEndpoint is called.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPolicy {
        [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr id, int defaults, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr id);
        [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod(IntPtr id, int defaults, IntPtr period, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr id, int visible);
    }
    static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    static string DeviceId(IDevice d) { string id; Check(d.GetId(out id)); return id; }
    static AudioDevice Describe(IDevice d) {
        string id=DeviceId(d), name=id; IPropertyStore store=null;
        try {
            Check(d.OpenPropertyStore(0,out store));
            PropertyKey key=new PropertyKey { Format=new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),Id=14 };
            PropVariant v; Check(store.GetValue(ref key,out v));
            try { if(v.Type==31) name=Marshal.PtrToStringUni(v.Value); } finally { PropVariantClear(ref v); }
        } finally { Release(store); }
        return new AudioDevice { Id=id,Name=name };
    }
    public static List<AudioDevice> List() {
        IEnumerator e=(IEnumerator)new EnumeratorClass(); ICollection collection=null;
        try {
            Check(e.EnumAudioEndpoints(0,1,out collection)); uint count; Check(collection.GetCount(out count));
            var result=new List<AudioDevice>();
            for(uint i=0;i<count;i++) { IDevice d; Check(collection.Item(i,out d)); try { result.Add(Describe(d)); } finally { Release(d); } }
            return result;
        } finally { Release(collection); Release(e); }
    }
    public static string Default(int role) {
        IEnumerator e=(IEnumerator)new EnumeratorClass(); IDevice d=null;
        try { int hr=e.GetDefaultAudioEndpoint(0,role,out d); if(hr==unchecked((int)0x80070490)) return null; Check(hr); return DeviceId(d); }
        finally { Release(d); Release(e); }
    }
    public static bool IsActive(string id) {
        if(string.IsNullOrEmpty(id)) return false;
        IEnumerator e=(IEnumerator)new EnumeratorClass(); IDevice d=null;
        try { int hr=e.GetDevice(id,out d); if(hr<0) return false; uint state; Check(d.GetState(out state)); return state==1; }
        finally { Release(d); Release(e); }
    }
    public static void SetDefault(string id, bool communications) {
        if(!IsActive(id)) throw new InvalidOperationException("The selected audio output is unavailable.");
        IPolicy policy=(IPolicy)new PolicyClass();
        try {
            int count=communications ? 3 : 2;
            for(int role=0;role<count;role++) if(Default(role)!=id) Check(policy.SetDefaultEndpoint(id,role));
            for(int role=0;role<count;role++) if(Default(role)!=id) throw new InvalidOperationException("Windows did not confirm the output change.");
        } finally { Release(policy); }
    }
}
}
