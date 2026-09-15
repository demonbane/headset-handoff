using System;
using HeadsetHandoff;
class AudioProbe {
    [STAThread] static void Main(string[] args) {
        foreach(var d in Audio.List()) Console.WriteLine(d.Id+" | "+d.Name);
        for(int role=0;role<3;role++) Console.WriteLine("DEFAULT "+role+" "+Audio.Default(role));
    }
}
