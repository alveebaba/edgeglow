// Phase E bounded regressions: actual GlowEngine, LanAdapter, GoveeLan and receiver.
// Physical interfaces, socket IO, time and generated pixels only are injected.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace EdgeGlow
{
    internal static class PhaseEControlRegressionTests
    {
        const string Device="PHASE-E-G1", Address="192.168.50.20";
        static string directory, currentCase; static int passed, failed;
        static int Main(string[] args)
        {
            if(args.Length!=1)return 2;
            directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
            Run("D02_first_failure_then_success",delegate { Retry(false,false,false,false,false); });
            Run("D02_all_three_fail",delegate { Retry(true,false,false,false,false); });
            Run("D02_observed_conflict",delegate { Retry(false,true,false,false,false); });
            Run("D02_out_of_subnet",delegate { Retry(false,false,true,false,false); });
            Run("D02_no_prior_ambient",delegate { Retry(false,false,false,true,false); });
            Run("D02_explicit_cancel",delegate { Retry(false,false,false,false,true); });
            Run("D02_socket_fault_then_reconstructed", SocketRecovery);
            Run("D01_retired_receiver_cannot_revalidate", RetiredReceiver);
            Console.WriteLine("RESULT: "+passed+" passed; "+failed+" failed.");
            Console.WriteLine("Generated capture and in-memory sockets only; no LAN ports, desktop, GUI, live settings or registry.");
            return failed==0?0:1;
        }
        static void Run(string name,Action body) {
            currentCase=name;try{body();passed++;Console.WriteLine("PASS "+name);}
            catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex);}
        }
        static void Check(bool value,string why) { if(!value)throw new InvalidOperationException(why); }
        static bool Wait(Func<bool> condition,int ms) {
            Stopwatch clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<ms)Thread.Sleep(5);return condition();
        }
        static Fixture New(bool power) { return new Fixture(Path.Combine(directory,currentCase+".trace.tsv"),power); }
        static double BeginExpiry(Fixture f,bool noAmbient,bool cancel) {
            if(!noAmbient)f.Step(0);
            f.Clock.Set(17);f.Engine.TestZone(3);f.Step(17);
            Check(f.Trace.Count("colors","sent")>0,"fixture never displayed generated identification");
            f.Step(20.1);
            f.FailDisable=1;
            double first=cancel?21.1:25.1;
            if(cancel)f.Engine.CancelTest();
            f.Step(first);
            Check(f.Trace.Count("disable","failed")==1,"first actual release Send did not fail");
            Check(f.Engine.State.ReleasePending,"initial failure lost engine obligation");
            return first;
        }
        static void Retry(bool allFail,bool conflict,bool outside,bool noAmbient,bool cancel) {
            using(Fixture f=New(true)) {
                double first=BeginExpiry(f,noAmbient,cancel);
                int priorColors=f.Trace.Count("colors",null), priorEnable=f.Trace.Count("enable",null);
                if(allFail)f.FailDisable=20;
                if(conflict)f.Scan("OTHER-G1",Address);
                if(outside) { f.Subnet=60;f.Lan.MaintainTransport(true); }
                f.Step(first+.2);
                Check(f.Adapter.ReleaseCalls==1,"same-tick/early servicing bypassed one-second due spacing");
                f.Step(first+1.1);f.Step(first+2.2);f.Step(first+3.3);f.Step(first+30);
                bool guarded=conflict||outside;
                Check(f.Adapter.ReleaseCalls==(allFail||guarded?3:2),
                    "pending release attempts were not serviced independently of stale replies; count="+f.Adapter.ReleaseCalls);
                Check(f.Engine.State.ReleasePending==(allFail||guarded),"pending cleanup state disagrees with local outcomes");
                Check(f.Trace.Count("disable","sent")==(allFail||guarded?0:1),"unexpected release send completion");
                Check(f.Trace.Count("disable","failed")==(allFail?3:1),"unexpected actual failed-send count");
                Check(f.Trace.Count("colors",null)==priorColors&&f.Trace.Count("enable",null)==priorEnable,
                    "unreachable cleanup newly activated ambient or identification");
                List<double> times=f.Adapter.ReleaseTimes;
                for(int i=1;i<times.Count;i++)Check(times[i]-times[i-1]>=1,"release spacing was less than one second");
                if(guarded)Check(f.Trace.Count("release-authorized","rejected")==2,"guarded retries did not preserve identity/subnet rejection");
            }
        }
        static void SocketRecovery() {
            using(Fixture f=New(true)) {
                f.Step(0);f.Clock.Set(17);f.Engine.TestZone(2);f.Step(17);f.Step(20.1);
                f.FailDisable=1;f.ThrowSocketFailure=true;f.Step(25.1);
                Check(f.Engine.State.ReleasePending&&f.Trace.Count("disable","failed")==1,"socket-failure fixture did not retain release");
                int generation=f.Lan.Generation;
                f.Clock.Set(27.2);f.Lan.MaintainTransport(true);
                Check(f.Lan.Generation>generation,"outbound transport was not reconstructed before retry");
                Check(f.Lan.LastSeenUtc<f.Clock.UtcNow.AddSeconds(-20),"fixture invented inbound revalidation");
                f.Step(27.2);
                Check(f.Adapter.ReleaseCalls==2&&f.Trace.Count("disable","sent")==1&&!f.Engine.State.ReleasePending,
                    "reconstructed outbound-only transport did not service retained release");
            }
        }
        static void RetiredReceiver() {
            using(Fixture f=New(false)) {
                f.Step(0);
                Check(f.Trace.Count("turn1","sent")==1,"known-off activation did not create restoration obligation");
                f.Scan("OTHER-G1",Address);f.Engine.Paused=true;f.Step(.1);
                Check(f.Engine.State.ReleasePending&&f.Trace.ControlSentAfter(0)==3,
                    "conflict setup did not retain original session without extra control");
                MemorySocket old=f.ActiveReceiver;
                Packet held=new Packet(ScanBytes(Device,Address),Address,true);
                f.ExpectedPacketSocket=old;f.ParserCompleted.Reset();f.OldPublicationCount=0;f.ObservePackets=true;
                old.Queue(held);
                Check(held.Entered.WaitOne(2000),"old receiver never obtained held datagram");
                f.Route=2;f.BlockFactory=true;
                Exception rebuildError=null;
                Thread rebuild=new Thread(new ThreadStart(delegate{try{f.Lan.MaintainTransport(true);}catch(Exception ex){rebuildError=ex;}})){IsBackground=true};
                bool parsed=false;int beforeControls=f.Trace.ControlSentAfter(0);DateTime during=DateTime.MinValue;
                try {
                    rebuild.Start();Check(f.FactoryEntered.WaitOne(2000),"replacement factory did not enter retirement window");
                    held.Release.Set();
                    parsed=f.ParserCompleted.WaitOne(2000);
                    if(parsed)during=f.Lan.LastSeenUtc;
                } finally { f.FactoryRelease.Set();held.Release.Set();rebuild.Join(3000);f.BlockFactory=false; }
                Check(rebuildError==null&&!rebuild.IsAlive,"replacement did not complete safely");
                Check(parsed,"no parser-completion evidence for the held old-receiver packet");
                f.Step(1.2);
                int illegal=f.Trace.ControlSentAfter(0)-beforeControls;
                bool pending=f.Engine.State.ReleasePending;
                int oldPublished=f.OldPublicationCount;
                f.Trace.Add("D01-before-revalidation","observation",-1,"",Address,
                    "oldPublished="+oldPublished+"; lastSeenDuring="+during.ToString("o")+"; controlWrites="+illegal+"; pending="+pending);
                // Positive revalidation comes only from the newly published actual receiver.
                f.ExpectedPacketSocket=f.ActiveReceiver;f.ParserCompleted.Reset();
                Packet current=new Packet(ScanBytes(Device,Address),Address,false);f.ActiveReceiver.Queue(current);
                Check(f.ParserCompleted.WaitOne(2000),"new receiver matching scan was not processed");
                f.Step(2.3);
                bool positive=f.Trace.Count("disable","sent")==1&&f.Trace.Count("turn0","sent")==1&&!f.Engine.State.ReleasePending;
                Check(oldPublished==0&&during==DateTime.MinValue,
                    "retired receiver published/refreshed matching identity during null-receiver window");
                Check(illegal==0&&pending,"retired packet cleared conflict and authorized saved-endpoint controls");
                Check(positive,"legitimate active-receiver scan did not allow retained release");
            }
        }
        static byte[] ScanBytes(string id,string ip) { return Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"device\":\""+id+"\",\"sku\":\"H6609\",\"ip\":\""+ip+"\"}}}"); }
        sealed class TestClock {
            long ticks;public double Seconds {get{return Interlocked.Read(ref ticks)/(double)TimeSpan.TicksPerSecond;}}
            public DateTime UtcNow {get{return new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc).AddTicks(Interlocked.Read(ref ticks));}}
            public void Set(double seconds){Interlocked.Exchange(ref ticks,(long)(seconds*TimeSpan.TicksPerSecond));}
        }
        sealed class TraceLog {
            readonly object gate=new object();readonly string path;readonly TestClock clock;long sequence;
            readonly List<string[]> rows=new List<string[]>();
            public TraceLog(string file,TestClock time){path=file;clock=time;File.WriteAllText(path,"sequence\ttime\tthread\tclass\toutcome\tsocket\ttoken\tendpoint\tdetail\n");}
            public void Add(string kind,string outcome,int socket,string token,string endpoint,string detail){
                lock(gate){string[] r={(++sequence).ToString(),clock.Seconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                    Thread.CurrentThread.ManagedThreadId.ToString(),kind,outcome,socket.ToString(),token,endpoint,(detail??"").Replace("\r","").Replace("\n"," ")};
                    rows.Add(r);File.AppendAllText(path,String.Join("\t",r)+"\n");}
            }
            public int Count(string kind,string outcome){lock(gate){int n=0;foreach(string[] r in rows)if(r[3]==kind&&(outcome==null||r[4]==outcome))n++;return n;}}
            public int ControlSentAfter(double time){lock(gate){int n=0;foreach(string[] r in rows)if(double.Parse(r[1],System.Globalization.CultureInfo.InvariantCulture)>=time&&r[4]=="sent"&&
                (r[3]=="turn0"||r[3]=="turn1"||r[3]=="enable"||r[3]=="disable"||r[3]=="colors"))n++;return n;}}
        }
        sealed class GeneratedCapture : IEngineCapture {
            public bool HasNewFrame{get{return true;}}public string LastError{get{return "";}}
            public CaptureFrame Capture(Rectangle bounds){byte[] pixels=new byte[256*107*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=70;pixels[i+1]=100;pixels[i+2]=40;pixels[i+3]=255;}return new CaptureFrame(256,107,pixels);}
            public void Dispose(){}
        }
        sealed class ObservedAdapter : IEngineTransport {
            readonly EngineServices.LanAdapter actual;readonly Fixture f;
            public int ReleaseCalls;public readonly List<double> ReleaseTimes=new List<double>();
            public ObservedAdapter(Fixture owner){f=owner;actual=new EngineServices.LanAdapter(f.Lan);}
            public event Action<DeviceInfo> DeviceSeen{add{actual.DeviceSeen+=value;}remove{actual.DeviceSeen-=value;}}
            public event Action<string> Error{add{actual.Error+=value;}remove{actual.Error-=value;}}
            public DeviceInfo CurrentTarget{get{return actual.CurrentTarget;}}public EndpointToken CurrentEndpoint{get{return actual.CurrentEndpoint;}}
            public DateTime LastSeenUtc{get{return actual.LastSeenUtc;}}public int Generation{get{return actual.Generation;}}
            public void Start(){actual.Start();}public void Discover(){actual.Discover();}public void Poll(){actual.Poll();}public void SetTarget(DeviceInfo d){actual.SetTarget(d);}
            string Token(EndpointToken t){return t.Id+"@"+t.Ip+"#"+t.Generation;}
            public void Enable(EndpointToken t,Func<bool> allow){f.Trace.Add("enable-call","attempt",-1,Token(t),t.Ip,"");actual.Enable(t,allow);}
            public void SendColors(EndpointToken t,Color[] colors,bool flag,Func<bool> allow){f.Trace.Add("colors-call","attempt",-1,Token(t),t.Ip,"");actual.SendColors(t,colors,flag,allow);}
            public void Disable(EndpointToken t){
                ReleaseCalls++;ReleaseTimes.Add(f.Clock.Seconds);f.Trace.Add("release-call","attempt",-1,Token(t),t.Ip,"attempt="+ReleaseCalls);
                try{actual.Disable(t);f.Trace.Add("release-authorized","local-complete-unacknowledged",-1,Token(t),t.Ip,"");}
                catch(Exception ex){f.Trace.Add("release-authorized",ex is InvalidOperationException?"rejected":"failed",-1,Token(t),t.Ip,ex.Message);throw;}
            }
            public void Dispose(){actual.Dispose();}
        }
        sealed class Fixture : IDisposable {
            public readonly TestClock Clock=new TestClock();public readonly TraceLog Trace;public readonly GoveeLan Lan;public readonly GlowEngine Engine;public readonly ObservedAdapter Adapter;
            public int FailDisable,Subnet=50,Route=1;public bool ThrowSocketFailure,BlockFactory,ObservePackets;public int OldPublicationCount;
            int socketIds;public MemorySocket ActiveReceiver,ExpectedPacketSocket;
            public readonly ManualResetEvent FactoryEntered=new ManualResetEvent(false),FactoryRelease=new ManualResetEvent(false),ParserCompleted=new ManualResetEvent(false);
            public Fixture(string path,bool power){
                Trace=new TraceLog(path,Clock);
#if PHASE_D_BASELINE
                Lan=new GoveeLan(CreateSocket,Interfaces,delegate{return Clock.UtcNow;});
#else
                Lan=new GoveeLan(CreateSocket,Interfaces,delegate{return Clock.UtcNow;},ParserDone);
#endif
                Lan.DeviceSeen+=delegate(DeviceInfo info){
                    if(ObservePackets&&info.Id==Device){if(Object.ReferenceEquals(ExpectedPacketSocket,ActiveReceiver)==false)OldPublicationCount++;
                        Trace.Add("device-publication","accepted",ExpectedPacketSocket==null?-1:ExpectedPacketSocket.Id,"",info.Ip,"generation="+info.IdentityGeneration);
#if PHASE_D_BASELINE
                        ParserCompleted.Set();
#endif
                    }
                };
                Lan.Start();DeviceInfo d=Scan(Device,Address);Lan.SetTarget(d);
                byte[] status=Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":"+(power?"1":"0")+",\"brightness\":17}}}");
                Check(Lan.AcceptDatagram(status,new IPEndPoint(IPAddress.Parse(Address),4003),Lan.Generation)!=null,"initial real status rejected");
                Adapter=new ObservedAdapter(this);
                AppSettings settings=new AppSettings{DeviceId=Device,DeviceIp=Address,AutoSync=true,StartWithWindows=false,AreaMode="Full",SmoothMs=0,Brightness=100,Saturation=100,Fps=24,Monitor="GENERATED"};
                Engine=new GlowEngine(settings,new EngineServices{Transport=Adapter,CreateCapture=delegate{return new GeneratedCapture();},UtcNow=delegate{return Clock.UtcNow;},
                    MonotonicSeconds=delegate{return Clock.Seconds;},SelectBounds=delegate(AppSettings ignored){return new Rectangle(0,0,3440,1440);},
                    CompetitorRunning=delegate{return false;},ListenSystemEvents=false,Log=delegate(string s){Trace.Add("engine-log","observation",-1,"","",s);}});
            }
            void ParserDone(ILanDatagramSocket socket,int generation,DeviceInfo result){
                if(ObservePackets&&Object.ReferenceEquals(socket,ExpectedPacketSocket)){
                    Trace.Add("parser-completed",result==null?"rejected":"accepted",((MemorySocket)socket).Id,"epoch="+generation,Address,"");
                    ParserCompleted.Set();
                }
            }
            ILanDatagramSocket CreateSocket(bool reply){
                if(reply&&BlockFactory){ActiveReceiver=null;Trace.Add("factory-gate","retired",-1,"",Address,"no new receiver published");
                    FactoryEntered.Set();Check(FactoryRelease.WaitOne(4000),"factory release was not signaled");}
                MemorySocket s=new MemorySocket(this,Interlocked.Increment(ref socketIds),reply);
                if(reply)ActiveReceiver=s;return s;
            }
            LanInterface[] Interfaces(){return new LanInterface[]{new LanInterface{Id="synthetic",Address=IPAddress.Parse("192.168."+Subnet+".10"),
                Mask=new byte[]{255,255,255,0},Broadcast=IPAddress.Parse("192.168."+Subnet+".255"),Route="192.168."+Subnet+"."+Route}};}
            public DeviceInfo Scan(string id,string ip){Trace.Add("scan-input","injected",-1,"epoch="+Lan.Generation,ip,id);return Lan.AcceptDatagram(ScanBytes(id,ip),new IPEndPoint(IPAddress.Parse(ip),4003),Lan.Generation);}
            public void Step(double seconds){Clock.Set(seconds);Engine.Step();Trace.Add("engine-state","observation",-1,"epoch="+Lan.Generation,Address,
                "releasePending="+Engine.State.ReleasePending+"; sending="+Engine.State.Sending+"; "+Engine.State.OutputPolicy+"; "+Engine.State.Detail);}
            public void Dispose(){FactoryRelease.Set();Engine.Dispose();Trace.Add("fixture-dispose","observation",-1,"",Address,"complete");}
        }
        sealed class Packet {
            public readonly byte[] Bytes;public readonly string Source;public readonly bool Hold;
            public readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public Packet(byte[] bytes,string source,bool hold){Bytes=bytes;Source=source;Hold=hold;}
        }
        sealed class MemorySocket : ILanDatagramSocket {
            readonly Fixture f;readonly bool reply;readonly object gate=new object();readonly Queue<Packet> packets=new Queue<Packet>();
            readonly AutoResetEvent available=new AutoResetEvent(false);int disposed;public readonly int Id;
            public MemorySocket(Fixture owner,int id,bool receiver){f=owner;Id=id;reply=receiver;}
            public void Queue(Packet packet){lock(gate)packets.Enqueue(packet);available.Set();}
            public byte[] Receive(out IPEndPoint source){
                source=null;Packet p=null;lock(gate)if(packets.Count>0)p=packets.Dequeue();
                if(p!=null){f.Trace.Add("receive-obtained","held",Id,"",p.Source,"oldReceiver="+reply);p.Entered.Set();if(p.Hold)p.Release.WaitOne();
                    source=new IPEndPoint(IPAddress.Parse(p.Source),4003);f.Trace.Add("receive-return","packet",Id,"",p.Source,"disposed="+disposed);return p.Bytes;}
                if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("synthetic socket");
                available.WaitOne(20);throw new SocketException((int)SocketError.TimedOut);
            }
            public void Send(byte[] bytes,IPEndPoint target){
                if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("synthetic socket");
                string json=Encoding.UTF8.GetString(bytes),kind="other";
                if(json.Contains(GoveeLan.DisablePacket))kind="disable";
                else if(json.Contains(GoveeLan.EnablePacket))kind="enable";
                else if(json.Contains("\"cmd\":\"turn\""))kind=json.Contains("\"value\":1")?"turn1":"turn0";
                else if(json.Contains("\"cmd\":\"razer\""))kind="colors";
                else if(json.Contains("\"cmd\":\"scan\""))kind="scan";
                else if(json.Contains("\"cmd\":\"devStatus\""))kind="poll";
                if(kind=="disable"&&f.FailDisable>0){f.FailDisable--;f.Trace.Add(kind,"failed",Id,"",target.Address.ToString(),json);
                    if(f.ThrowSocketFailure)throw new SocketException((int)SocketError.NetworkDown);
                    throw new IOException("Injected transient local Send failure; outbound socket remains available");}
                f.Trace.Add(kind,"sent",Id,"",target.Address.ToString(),"local submission only; "+json);
            }
            public void Join(LanInterface local){}public void ConfigureMulticast(LanInterface local){}
            public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){available.Set();f.Trace.Add("socket-dispose","closed",Id,"","","");}}
        }
    }
}
