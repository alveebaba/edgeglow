// Generated image / fake transport tests only. Never start Program.Main or native capture.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace EdgeGlow
{
    internal static class ZoneLayoutTests
    {
        private static int passed, failed, checks;
        public static int Main(string[] args)
        {
            Run("18-zone clockwise coordinate ramp and full side thirds", Orientation);
            Run("active picture excludes outer black bars and keeps legacy overload", ActivePicture);
            Run("sector arithmetic mean does not bleed adjacent sectors", ArithmeticMean);
            Run("15 percent band includes deeper picture than 8 percent", ThickerBand);
            Run("18-zone packet independent byte oracle and ten-zone compatibility", Packet);
            Run("zone settings validation and rotation modulus", Settings);
            Run("actual engine identifies slots 17 and 18 then expires at eight seconds", IdentificationExpiry);
            Run("actual engine explicit cancellation and pause clear identification", IdentificationCancel);
            Run("actual engine sends eighteen ambient zones and rotates all eighteen", AmbientAndRotation);
            Run("actual engine rejects changed layout before any settings mutation", ImmutableLayout);
            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + checks + " assertions.");
            Console.WriteLine("Generated images, injected clock and fake transport only. No native DLL, desktop, sockets, GUI, settings or registry.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action body)
        { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException(message); }
        private static void Throws<T>(Action body, string message) where T : Exception
        { bool caught = false; try { body(); } catch (T) { caught = true; } Check(caught, message); }
        private static void Equal(Color actual, Color expected, string message, int tolerance)
        { Check(Math.Abs(actual.R-expected.R)<=tolerance && Math.Abs(actual.G-expected.G)<=tolerance && Math.Abs(actual.B-expected.B)<=tolerance,
            message + " expected=" + expected + " actual=" + actual); }
        private static CaptureFrame Frame(int width, int height, Color color)
        { CaptureFrame f = new CaptureFrame(width,height,new byte[width*height*4]); Fill(f,new Rectangle(0,0,width,height),color); return f; }
        private static void Fill(CaptureFrame f, Rectangle area, Color color)
        {
            for (int y=area.Top;y<area.Bottom;y++) for (int x=area.Left;x<area.Right;x++) {
                int n=(y*f.Width+x)*4; f.Bgra[n]=color.B;f.Bgra[n+1]=color.G;f.Bgra[n+2]=color.R;f.Bgra[n+3]=255;
            }
        }
        private static CaptureFrame Ramp()
        {
            CaptureFrame f=Frame(256,107,Color.Black);
            for(int y=0;y<107;y++)for(int x=0;x<256;x++){
                int n=(y*256+x)*4;f.Bgra[n]=40;f.Bgra[n+1]=(byte)(2*y);f.Bgra[n+2]=(byte)x;
            }
            return f;
        }
        // Literal geometry is the independent oracle for a 256x107 image at 8%.
        // R=x, G=2*y makes the exact mean analytically known without resampling.
        private static Color[] RampOracle(bool cropped)
        {
            Rectangle[] rectangles = cropped ? new Rectangle[] {
                new Rectangle(33,0,31,9),new Rectangle(64,0,32,9),new Rectangle(96,0,32,9),
                new Rectangle(128,0,31,9),new Rectangle(159,0,32,9),new Rectangle(191,0,32,9),
                new Rectangle(208,0,15,35),new Rectangle(208,35,15,36),new Rectangle(208,71,15,36),
                new Rectangle(191,98,32,9),new Rectangle(159,98,32,9),new Rectangle(128,98,31,9),
                new Rectangle(96,98,32,9),new Rectangle(64,98,32,9),new Rectangle(33,98,31,9),
                new Rectangle(33,71,15,36),new Rectangle(33,35,15,36),new Rectangle(33,0,15,35)
            } : new Rectangle[] {
                new Rectangle(0,0,42,9),new Rectangle(42,0,43,9),new Rectangle(85,0,43,9),
                new Rectangle(128,0,42,9),new Rectangle(170,0,43,9),new Rectangle(213,0,43,9),
                new Rectangle(236,0,20,35),new Rectangle(236,35,20,36),new Rectangle(236,71,20,36),
                new Rectangle(213,98,43,9),new Rectangle(170,98,43,9),new Rectangle(128,98,42,9),
                new Rectangle(85,98,43,9),new Rectangle(42,98,43,9),new Rectangle(0,98,42,9),
                new Rectangle(0,71,20,36),new Rectangle(0,35,20,36),new Rectangle(0,0,20,35)
            };
            Color[] expected=new Color[18];
            for(int i=0;i<18;i++) { Rectangle r=rectangles[i];expected[i]=Color.FromArgb((int)Math.Round(r.Left+(r.Width-1)/2.0),2*r.Top+r.Height-1,40); }
            return expected;
        }
        private static void Orientation()
        {
            Color[] actual=ColorProcessor.Sample(Ramp(),new Rectangle(33,0,190,107),.08,1,1,18), expected=RampOracle(true);
            Check(actual.Length==18,"18-zone sample length");
            for(int i=0;i<18;i++)Equal(actual[i],expected[i],"clockwise zone "+i,1);
            Check(actual[0].R<actual[5].R && actual[6].G<actual[8].G && actual[9].R>actual[14].R && actual[15].G>actual[17].G,"clockwise traversal orientation");
            Check(actual[6].G<80 && actual[8].G>140,"right thirds must cover full side, including top/bottom");
            Throws<ArgumentException>(delegate { ColorProcessor.Sample(Ramp(),new Rectangle(0,0,256,107),.08,1,1,17); },"unsupported sample count accepted");
            CaptureFrame small=Frame(6,4,Color.Gold);
            foreach(Color color in ColorProcessor.Sample(small,new Rectangle(0,0,6,4),.08,1,1,18))Equal(color,Color.Gold,"six-pixel width remains valid",0);
            Throws<ArgumentException>(delegate { ColorProcessor.Sample(small,new Rectangle(0,0,5,4),.08,1,1,18); },"five-pixel 18-zone width accepted");
        }
        private static void ActivePicture()
        {
            Color color=Color.FromArgb(210,70,25);CaptureFrame frame=Frame(256,107,Color.Black);Rectangle active=new Rectangle(33,0,190,107);Fill(frame,active,color);
            Color[] expanded=ColorProcessor.Sample(frame,active,.08,1,1,18);
            foreach(Color sample in expanded)Equal(sample,color,"black pillarboxes contaminated active edge",0);
            Color[] legacy=ColorProcessor.Sample(frame,active,.08,1,1),explicitTen=ColorProcessor.Sample(frame,active,.08,1,1,10);
            Check(legacy.Length==10 && explicitTen.Length==10,"legacy overload changed zone count");
            for(int i=0;i<10;i++){Equal(legacy[i],color,"legacy active picture",0);Equal(legacy[i],explicitTen[i],"legacy overload changed geometry",0);}
        }
        private static void ArithmeticMean()
        {
            CaptureFrame frame=Frame(180,90,Color.Lime);
            Fill(frame,new Rectangle(0,0,15,9),Color.FromArgb(240,0,0));Fill(frame,new Rectangle(15,0,15,9),Color.FromArgb(0,0,80));
            Color[] colors=ColorProcessor.Sample(frame,new Rectangle(0,0,180,90),.1,1,1,18);
            Equal(colors[0],Color.FromArgb(120,0,40),"literal equal-pixel mean across two colors",0);
            Equal(colors[1],Color.Lime,"neighbor sector bleed",0);
        }
        private static void ThickerBand()
        {
            CaptureFrame frame=Frame(180,100,Color.Lime);
            Fill(frame,new Rectangle(0,0,180,8),Color.FromArgb(180,0,0));Fill(frame,new Rectangle(0,8,180,7),Color.FromArgb(0,0,120));
            Color[] narrow=ColorProcessor.Sample(frame,new Rectangle(0,0,180,100),.08,1,1,18);
            Color[] thick=ColorProcessor.Sample(frame,new Rectangle(0,0,180,100),.15,1,1,18);
            for(int i=0;i<6;i++){Equal(narrow[i],Color.FromArgb(180,0,0),"8 percent outer band",0);Equal(thick[i],Color.FromArgb(96,0,56),"15 percent deeper band mean",0);}
        }
        private static void Packet()
        {
            Color[] colors=new Color[18];for(int i=0;i<18;i++)colors[i]=Color.FromArgb(i*11+3,i*7+1,255-i*5);
            foreach(bool flag in new bool[]{false,true}){
                byte[] actual=GoveeLan.BuildFrame(colors,flag),expected=new byte[61];
                expected[0]=0xBB;expected[1]=0;expected[2]=0x38;expected[3]=0xB0;expected[4]=(byte)(flag?1:0);expected[5]=0x12;
                for(int i=0;i<18;i++){expected[6+i*3]=(byte)(i*11+3);expected[7+i*3]=(byte)(i*7+1);expected[8+i*3]=(byte)(255-i*5);}
                byte checksum=0;for(int i=0;i<60;i++)checksum^=expected[i];expected[60]=checksum;
                Check(actual.Length==61,"18-zone frame size");for(int i=0;i<61;i++)Check(actual[i]==expected[i],"packet byte "+i+" flag="+flag);
                byte all=0;foreach(byte value in actual)all^=value;Check(all==0,"whole-frame XOR invariant");
            }
            Check(GoveeLan.BuildFrame(new Color[10],false).Length==37,"legacy packet size changed");
            foreach(int length in new int[]{0,11,17,19}){int n=length;Throws<ArgumentException>(delegate{GoveeLan.BuildFrame(new Color[n],false);},"invalid frame count accepted "+n);}
        }
        private static void Settings()
        {
            Check(new AppSettings().ZoneCount==18,"public settings default to the validated eighteen-zone layout");
            AppSettings s=new AppSettings{ZoneCount=18,Rotation=-1};s.Validate();Check(s.ZoneCount==18&&s.Rotation==17,"negative 18-zone rotation");
            s.Rotation=37;s.Validate();Check(s.Rotation==1,"18-zone rotation modulus");
            Check(s.Copy().ZoneCount==18,"settings copy lost count");
            foreach(int count in new int[]{-18,0,11,17,19}){s=new AppSettings{ZoneCount=count,Rotation=19};s.Validate();Check(s.ZoneCount==10&&s.Rotation==9,"invalid count fallback/modulus "+count);}
            s=new AppSettings{ZoneCount=10,Rotation=-1};s.Validate();Check(s.Rotation==9,"legacy rotation modulus");
        }
        private static AppSettings Config(bool automatic,int count)
        { return new AppSettings{DeviceId="ZONE-TEST",DeviceIp="192.168.50.20",Monitor="GENERATED",AreaMode="Full",ZoneCount=count,AutoSync=automatic,StartWithWindows=false,Brightness=100,Saturation=100,SmoothMs=0,Fps=24,EdgePercent=8}; }
        private static void Pattern(Color[] values,int index)
        {
            Check(values!=null&&values.Length==18,"identification frame not eighteen zones");
            for(int i=0;i<18;i++)Equal(values[i],i==index?Color.FromArgb(100,35,5):Color.Black,"identification slot "+i,0);
        }
        private static void IdentificationExpiry()
        {
            using(Fixture f=new Fixture(Config(false,18))){
                Check(f.Engine.State.Colors.Length==18,"initial engine state count");
                f.ForbidCapture=true;f.Engine.TestZone(16);f.Step(0);Pattern(f.Last,16);
                f.Clock.Seconds=.1;f.Engine.TestZone(17);f.Step(0);Pattern(f.Last,17);
                f.Step(7.99);Check(f.Transport.Releases==0,"replacement identification expired before its eight seconds");Pattern(f.Last,17);
                int sends=f.Transport.Colors.Count;f.Step(.02);
                Check(f.Transport.Releases==1&&f.Transport.Colors.Count==sends,"expired identification was not released or was resent");
                Check(f.Engine.State.Colors.Length==18,"paused engine state count");
                Check(f.CaptureCreates==0&&f.Source.Reads==0,"paused identification used desktop capture");
            }
        }
        private static void IdentificationCancel()
        {
            using(Fixture f=new Fixture(Config(false,18))){
                f.ForbidCapture=true;f.Engine.TestZone(17);f.Step(0);Pattern(f.Last,17);int sends=f.Transport.Colors.Count;
                f.Engine.CancelTest();f.Step(.1);Check(f.Transport.Releases==1&&f.Transport.Colors.Count==sends,"explicit cancellation failed");
                // A released session retains the existing one-second activation cooldown.
                f.Step(1.1);f.Engine.TestZone(16);f.Step(0);Pattern(f.Last,16);sends=f.Transport.Colors.Count;
                f.Engine.Paused=true;f.Step(.1);Check(f.Transport.Releases==2&&f.Transport.Colors.Count==sends,"pause did not clear identification");
                Check(f.CaptureCreates==0&&f.Source.Reads==0,"cancel/pause used desktop capture");
            }
        }
        private static void AmbientAndRotation()
        {
            using(Fixture f=new Fixture(Config(true,18))){
                f.Step(0);Check(f.Last.Length==18&&f.Engine.State.Colors.Length==18,"ambient/state count");
                foreach(Color c in f.Last)Equal(c,Color.FromArgb(32,96,160),"18-zone ambient color",0);
                Check(f.Engine.State.FrameWidth==256&&f.Engine.State.FrameHeight==107,"capture dimensions changed");
                AppSettings rotated=Config(true,18);rotated.Rotation=17;rotated.Reverse=true;f.Source.Frame=Ramp();f.Engine.Update(rotated);f.Step(.1);
                Color[] expected=RampOracle(false);for(int i=0;i<18;i++)Equal(f.Last[i],expected[((18-i)+17)%18],"18-zone rotation/reversal "+i,1);
                int reads=f.Source.Reads;f.Engine.Paused=true;f.Step(.1);Check(f.Source.Reads==reads&&f.Transport.Releases==1,"ambient pause captured or failed release");
            }
        }
        private static void ImmutableLayout()
        {
            using(Fixture f=new Fixture(Config(true,18))){
                f.Step(0);AppSettings changed=Config(true,10);changed.DeviceId="WRONG-TARGET";changed.Brightness=1;
                Throws<InvalidOperationException>(delegate{f.Engine.Update(changed);},"runtime layout mutation accepted");
                f.Step(1.1);Check(f.Last.Length==18&&f.Engine.State.Sending,"rejected update altered layout/target");
                foreach(Color c in f.Last)Equal(c,Color.FromArgb(32,96,160),"rejected update altered brightness",0);
                changed=Config(true,17);Throws<InvalidOperationException>(delegate{f.Engine.Update(changed);},"invalid count normalized to different layout accepted");
                AppSettings same=Config(true,18);same.Brightness=50;f.Engine.Update(same);f.Step(.1);foreach(Color c in f.Last)Equal(c,Color.FromArgb(16,48,80),"same-layout update rejected or ignored",0);
            }
            using(Fixture f=new Fixture(Config(false,10))){AppSettings normalized=Config(false,19);f.Engine.Update(normalized);Check(f.Engine.Paused,"validated same ten-zone layout altered paused state");}
        }
        private sealed class TestClock
        { public double Seconds; public DateTime Utc {get{return new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(Seconds);}} }
        private sealed class GeneratedCapture : IEngineCapture
        {
            public CaptureFrame Frame=ZoneLayoutTests.Frame(256,107,Color.FromArgb(32,96,160));public int Reads;
            public bool HasNewFrame {get{return true;}} public string LastError {get{return "";}}
            public CaptureFrame Capture(Rectangle bounds){Reads++;return Frame;}public void Dispose(){}
        }
        private sealed class FakeTransport : IEngineTransport
        {
            private readonly TestClock clock;private DeviceInfo target;
            public int Releases;public readonly List<Color[]> Colors=new List<Color[]>();
            public FakeTransport(TestClock time){clock=time;target=new DeviceInfo{Id="ZONE-TEST",Ip="192.168.50.20",Sku="H6609",LastSeenUtc=clock.Utc,LastStatusUtc=clock.Utc,PowerOn=true,Brightness=42,IdentityGeneration=1};}
            public event Action<DeviceInfo> DeviceSeen;public event Action<string> Error{add{}remove{}}
            public DeviceInfo CurrentTarget{get{return target.Copy();}}public EndpointToken CurrentEndpoint{get{return new EndpointToken(target,1);}}
            public DateTime LastSeenUtc{get{return target.LastSeenUtc;}}public int Generation{get{return 1;}}
            public void Start(){}public void Discover(){target.LastSeenUtc=clock.Utc;Action<DeviceInfo> h=DeviceSeen;if(h!=null)h(target.Copy());}
            public void Poll(){target.LastSeenUtc=clock.Utc;}public void SetTarget(DeviceInfo d){target=d.Copy();}
            public void Enable(EndpointToken endpoint,Func<bool> authorize){if(authorize!=null&&!authorize())throw new OperationCanceledException();}
            public void Disable(EndpointToken endpoint){Releases++;}
            public void SendColors(EndpointToken endpoint,Color[] colors,bool flag,Func<bool> authorize){if(authorize!=null&&!authorize())throw new OperationCanceledException();GoveeLan.BuildFrame(colors,flag);Colors.Add((Color[])colors.Clone());}
            public void Dispose(){}
        }
        private sealed class Fixture : IDisposable
        {
            public readonly TestClock Clock=new TestClock();public readonly GeneratedCapture Source=new GeneratedCapture();public readonly FakeTransport Transport;public readonly GlowEngine Engine;
            public bool ForbidCapture;public int CaptureCreates;public Color[] Last{get{return Transport.Colors.Count==0?null:Transport.Colors[Transport.Colors.Count-1];}}
            public Fixture(AppSettings config){Transport=new FakeTransport(Clock);Engine=new GlowEngine(config,new EngineServices{Transport=Transport,
                CreateCapture=delegate{CaptureCreates++;if(ForbidCapture)throw new InvalidOperationException("Capture forbidden");return Source;},
                UtcNow=delegate{return Clock.Utc;},MonotonicSeconds=delegate{return Clock.Seconds;},SelectBounds=delegate(AppSettings ignored){return new Rectangle(0,0,3440,1440);},
                CompetitorRunning=delegate{return false;},Log=delegate(string ignored){},ListenSystemEvents=false});}
            public void Step(double elapsed){Clock.Seconds+=elapsed;Engine.Step();}public void Dispose(){Engine.Dispose();}
        }
    }
}
