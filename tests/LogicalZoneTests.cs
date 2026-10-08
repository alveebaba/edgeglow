// Actual engine; generated pixels, injected clock and recording transport only.
// Compile with Engine.cs, Capture.cs, Network.cs, Settings.cs and /main:EdgeGlow.LogicalZoneTests.
using System;
using System.Collections.Generic;
using System.Drawing;
namespace EdgeGlow
{
    internal static class LogicalZoneTests
    {
        private static int passed, failed, assertions;
        private static readonly Color Identification = Color.FromArgb(100, 35, 5);
        private static readonly Color[] TopColors = { Color.FromArgb(200,0,0), Color.FromArgb(0,180,0), Color.FromArgb(0,0,160), Color.FromArgb(100,80,0), Color.FromArgb(0,100,80), Color.FromArgb(80,0,100) };
        public static int Main()
        {
            Run("rotation15 logical screen1 sends raw4", LogicalFirst);
            Run("logical wraparound maps screen18 to raw3 and screen16 to raw1", LogicalWrap);
            Run("reversal applies equally to logical test and ambient", Reverse);
            Run("raw identification bypasses mapping and preview uses logical position", Raw);
            Run("ambient preview is logical while datagrams are rotated", Ambient);
            Run("capture recovery retains logical preview and last wire colors", Recovery);
            Run("logical and raw identification expire at eight seconds", Expiry);
            Run("replacement logical test overrides raw mode and cancellation releases", Replacement);
            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + assertions + " assertions.");
            Console.WriteLine("No desktop/native DLL/LAN/GUI/settings/registry operations; generated pixels and fake transport only.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action body)
        { try { body(); passed++; Console.WriteLine("PASS " + name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
        private static void Check(bool value, string text)
        { assertions++; if(!value) throw new InvalidOperationException(text); }
        private static void Equal(Color a, Color b, string text)
        { Check(a.R==b.R && a.G==b.G && a.B==b.B, text+" expected="+b+" actual="+a); }
        private static void Pattern(Color[] colors, int selected, string text)
        { Check(colors!=null && colors.Length==18,text+" count"); for(int i=0;i<18;i++) Equal(colors[i],i==selected?Identification:Color.Black,text+" index="+i); }
        private static void LogicalFirst()
        {
            using(Fixture f=new Fixture(false,false)) {
                f.Engine.TestZone(0); f.Step(0);
                Pattern(f.Last,3,"wire"); Pattern(f.Engine.State.Colors,0,"logical preview");
                Check(f.Engine.State.Message=="Testing screen zone 1","logical label");
                Check(f.Source.Reads==0,"identification captured a screen");
            }
        }
        private static void LogicalWrap()
        {
            using(Fixture f=new Fixture(false,false)) {
                f.Engine.TestZone(17); f.Step(0); Pattern(f.Last,2,"screen18 wire"); Pattern(f.Engine.State.Colors,17,"screen18 preview");
                f.Engine.TestZone(15); f.Step(.1); Pattern(f.Last,0,"screen16 wire"); Pattern(f.Engine.State.Colors,15,"screen16 preview");
            }
        }
        private static void Reverse()
        {
            using(Fixture f=new Fixture(false,true)) {
                f.Engine.TestZone(0); f.Step(0); Pattern(f.Last,15,"reverse screen1 wire"); Pattern(f.Engine.State.Colors,0,"reverse preview");
                f.Engine.TestZone(17); f.Step(.1); Pattern(f.Last,16,"reverse wrap wire"); Pattern(f.Engine.State.Colors,17,"reverse wrap preview");
            }
            using(Fixture f=new Fixture(true,true)) {
                f.Step(0); Equal(f.Last[15],TopColors[0],"reverse ambient screen1 wire");
                Equal(f.Engine.State.Colors[0],TopColors[0],"reverse ambient logical preview");
            }
        }
        private static void Raw()
        {
            using(Fixture f=new Fixture(false,false)) {
                f.Engine.TestRawZone(0); f.Step(0); Pattern(f.Last,0,"raw wire"); Pattern(f.Engine.State.Colors,15,"raw predicted logical position");
                Check(f.Engine.State.Message=="Identifying raw strip output 1","raw label");
            }
            using(Fixture f=new Fixture(false,true)) {
                f.Engine.TestRawZone(17); f.Step(0); Pattern(f.Last,17,"reverse raw bypass"); Pattern(f.Engine.State.Colors,16,"reverse raw logical position");
            }
        }
        private static void Ambient()
        {
            using(Fixture f=new Fixture(true,false)) {
                f.Step(0); Color[] preview=f.Engine.State.Colors;
                for(int i=0;i<6;i++) Equal(preview[i],TopColors[i],"literal top screen sector"+i);
                int[] literalLogicalForWire={15,16,17,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14};
                for(int i=0;i<18;i++) Equal(f.Last[i],preview[literalLogicalForWire[i]],"rotated wire"+i);
                Check(preview[0].ToArgb()!=f.Last[0].ToArgb(),"preview still exposes raw wire order");
            }
        }
        private static void Recovery()
        {
            using(Fixture f=new Fixture(true,false)) {
                f.Step(0); Color[] expectedPreview=(Color[])f.Engine.State.Colors.Clone(), expectedWire=(Color[])f.Last.Clone();
                f.Source.Error="synthetic capture interruption"; f.Step(1.1);
                for(int i=0;i<18;i++) { Equal(f.Engine.State.Colors[i],expectedPreview[i],"hold preview"+i); Equal(f.Last[i],expectedWire[i],"hold wire"+i); }
                f.Transport.Missing=true; f.Step(.1);
                for(int i=0;i<18;i++) Equal(f.Engine.State.Colors[i],expectedPreview[i],"missing reply preview"+i);
            }
        }
        private static void Expiry()
        {
            foreach(bool raw in new bool[]{false,true}) using(Fixture f=new Fixture(false,false)) {
                if(raw)f.Engine.TestRawZone(0);else f.Engine.TestZone(0);
                f.Step(0); f.Step(7.99); int frames=f.Transport.Frames.Count;
                Check(f.Transport.Releases==0,"early expiry"); f.Step(.01);
                Check(f.Transport.Frames.Count==frames,"frame sent at expiry"); Check(f.Transport.Releases==1,"expiry release missing");
                Check(f.Source.Reads==0,"expired paused test captured pixels");
            }
        }
        private static void Replacement()
        {
            using(Fixture f=new Fixture(false,false)) {
                f.Engine.TestRawZone(0); f.Step(0); Pattern(f.Last,0,"initial raw");
                f.Clock.Seconds=7; f.Engine.TestZone(0); f.Step(0); Pattern(f.Last,3,"replacement logical");
                f.Step(1.1); Check(f.Transport.Releases==0,"replacement retained original expiry");
                f.Engine.CancelTest(); int frames=f.Transport.Frames.Count; f.Step(.1);
                Check(f.Transport.Releases==1 && f.Transport.Frames.Count==frames,"explicit cancellation failed");
            }
        }
        private sealed class Clock
        { public double Seconds; public DateTime Utc {get{return new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(Seconds);}} }
        private sealed class Pixels : IEngineCapture
        {
            public string Error=""; public int Reads;
            private readonly CaptureFrame frame;
            public Pixels() {
                frame=new CaptureFrame(180,90,new byte[180*90*4]);
                for(int y=0;y<90;y++)for(int x=0;x<180;x++) {
                    Color c=y<9?TopColors[x/30]:Color.FromArgb(40,70,90); int i=(y*180+x)*4;
                    frame.Bgra[i]=c.B;frame.Bgra[i+1]=c.G;frame.Bgra[i+2]=c.R;frame.Bgra[i+3]=255;
                }
            }
            public bool HasNewFrame{get{return Error.Length==0;}} public string LastError{get{return Error;}}
            public CaptureFrame Capture(Rectangle bounds){Reads++;return frame;} public void Dispose(){}
        }
        private sealed class Recorder : IEngineTransport
        {
            private readonly Clock clock; private DeviceInfo target;
            public readonly List<Color[]> Frames=new List<Color[]>();public int Releases;public bool Missing;
            public Recorder(Clock c){clock=c;target=new DeviceInfo{Id="LOGICAL-ZONES",Ip="192.168.50.20",Sku="H6609",LastSeenUtc=c.Utc,LastStatusUtc=c.Utc,PowerOn=true,IdentityGeneration=1};}
            public event Action<DeviceInfo> DeviceSeen; public event Action<string> Error{add{}remove{}}
            public DeviceInfo CurrentTarget{get{return Missing?null:target.Copy();}} public EndpointToken CurrentEndpoint{get{return Missing?null:new EndpointToken(target,1);}}
            public DateTime LastSeenUtc{get{return Missing?DateTime.MinValue:target.LastSeenUtc;}} public int Generation{get{return 1;}}
            public void Start(){} public void Discover(){if(Missing)return;target.LastSeenUtc=clock.Utc;Action<DeviceInfo> h=DeviceSeen;if(h!=null)h(target.Copy());}
            public void Poll(){if(!Missing)target.LastSeenUtc=clock.Utc;} public void SetTarget(DeviceInfo d){target=d.Copy();}
            public void Enable(EndpointToken e,Func<bool> authorize){if(authorize!=null&&!authorize())throw new OperationCanceledException();}
            public void Disable(EndpointToken e){Releases++;}
            public void SendColors(EndpointToken e,Color[] colors,bool flag,Func<bool> authorize){if(authorize!=null&&!authorize())throw new OperationCanceledException();GoveeLan.BuildFrame(colors,flag);Frames.Add((Color[])colors.Clone());}
            public void Dispose(){}
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Clock Clock=new Clock();public readonly Pixels Source=new Pixels();public readonly Recorder Transport;public readonly GlowEngine Engine;
            public Color[] Last{get{return Transport.Frames[Transport.Frames.Count-1];}}
            public Fixture(bool automatic,bool reverse) {
                Transport=new Recorder(Clock);
                AppSettings config=new AppSettings{DeviceId="LOGICAL-ZONES",DeviceIp="192.168.50.20",Monitor="GENERATED",ZoneCount=18,Rotation=15,Reverse=reverse,AreaMode="Full",Brightness=100,Saturation=100,SmoothMs=0,EdgePercent=10,AutoSync=automatic,StartWithWindows=false};
                Engine=new GlowEngine(config,new EngineServices{Transport=Transport,CreateCapture=delegate{return Source;},UtcNow=delegate{return Clock.Utc;},MonotonicSeconds=delegate{return Clock.Seconds;},SelectBounds=delegate(AppSettings ignored){return new Rectangle(0,0,180,90);},CompetitorRunning=delegate{return false;},Log=delegate(string ignored){},ListenSystemEvents=false});
            }
            public void Step(double elapsed){Clock.Seconds+=elapsed;Engine.Step();} public void Dispose(){Engine.Dispose();}
        }
    }
}
