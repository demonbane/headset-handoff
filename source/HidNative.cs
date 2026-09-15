using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeadsetHandoff {
public sealed class HidInfo {
    public string Path;
    public ushort Vendor, Product, Usage, UsagePage, InputLength, OutputLength, FeatureLength;
    public override string ToString() {
        return string.Format("VID={0:X4} PID={1:X4} Usage={2:X4}:{3:X4} Input={4} Output={5} Feature={6} Path={7}", Vendor,Product,UsagePage,Usage,InputLength,OutputLength,FeatureLength,Path);
    }
}
public static class HidNative {
    [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort Vendor, Product, Version; }
    [StructLayout(LayoutKind.Sequential)] struct Caps {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] data, int length);
    [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] data, int length);
    [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] data, int length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static SafeFileHandle Open(string path, bool readWrite, bool overlapped) {
        SafeFileHandle h=CreateFile(path,readWrite ? 0xC0000000u : 0u,3,IntPtr.Zero,3,overlapped ? 0x40000000u : 0u,IntPtr.Zero);
        if(h.IsInvalid) { int error=Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
        return h;
    }
    public static List<HidInfo> Enumerate() {
        var result=new List<HidInfo>(); Guid guid; HidD_GetHidGuid(out guid);
        IntPtr set=SetupDiGetClassDevs(ref guid,IntPtr.Zero,IntPtr.Zero,0x12);
        if(set==new IntPtr(-1)) throw new Win32Exception();
        try {
            for(uint i=0;;i++) {
                InterfaceData data=new InterfaceData(); data.Size=Marshal.SizeOf(typeof(InterfaceData));
                if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref data)) { if(Marshal.GetLastWin32Error()==259) break; throw new Win32Exception(); }
                uint required; SetupDiGetDeviceInterfaceDetail(set,ref data,IntPtr.Zero,0,out required,IntPtr.Zero);
                IntPtr detail=Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(detail,IntPtr.Size==8 ? 8 : 6);
                    if(!SetupDiGetDeviceInterfaceDetail(set,ref data,detail,required,out required,IntPtr.Zero)) throw new Win32Exception();
                    string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                    if(path.IndexOf("vid_1038",StringComparison.OrdinalIgnoreCase)<0) continue;
                    using(SafeFileHandle h=Open(path,false,false)) {
                        Attributes a=new Attributes(); a.Size=Marshal.SizeOf(typeof(Attributes));
                        if(!HidD_GetAttributes(h,ref a)) continue;
                        IntPtr p; if(!HidD_GetPreparsedData(h,out p)) continue;
                        try {
                            Caps c; if(HidP_GetCaps(p,out c)!=0x110000) continue;
                            result.Add(new HidInfo { Path=path,Vendor=a.Vendor,Product=a.Product,Usage=c.Usage,UsagePage=c.UsagePage,InputLength=c.InputLength,OutputLength=c.OutputLength,FeatureLength=c.FeatureLength });
                        } finally { HidD_FreePreparsedData(p); }
                    }
                } finally { Marshal.FreeHGlobal(detail); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
}
}
