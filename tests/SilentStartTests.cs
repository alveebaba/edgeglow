using System;
using System.Drawing;
using System.Windows.Forms;
namespace EdgeGlow {
 internal static class SilentStartTests {
  [STAThread] public static int Main(){
   try{
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    using(var form=new MainWindow(true,null,true)){
     form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
     int shown=0;form.Shown+=delegate{shown++;};
     form.Show();Application.DoEvents();
     if(form.Visible)throw new Exception("Hidden startup made form visible.");
     if(!form.IsHandleCreated)throw new Exception("Hidden startup did not create a message-pump handle.");
     if(shown!=0)throw new Exception("Shown event occurred during hidden startup.");
     form.Show();Application.DoEvents();
     if(form.Visible || shown!=0)throw new Exception("Repeated implicit Show revealed hidden-start form.");
    }
    Console.WriteLine("PASS: hidden startup has a live handle, remains invisible, fires no Shown event, and repeated implicit Show remains hidden.");
    Console.WriteLine("Preview-only form: no engine, LAN, capture, settings writes or startup registration. No form was intentionally made visible.");
    return 0;
   }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  }
 }
}
