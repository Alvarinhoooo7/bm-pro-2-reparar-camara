using System; using System.IO;
class P { static int Main(string[] a){
  string img = Environment.GetEnvironmentVariable("FAKE_XFEL_IMG");
  if (a.Length==0) return 1;
  if (img==null || !File.Exists(img)) { Console.WriteLine("ERROR: No FEL device found"); return 1; }
  Func<string,long> H = s => Convert.ToInt64(s.StartsWith("0x")?s.Substring(2):s,16);
  switch(a[0]){
   case "version": Console.WriteLine("AWUSBFEX soc=0x00001890(V821) 0x00000001 ver=0x0001 0x44 0x08 scratchpad=0x00067e00"); return 0;
   case "sid": Console.WriteLine("1890a0c0:0c004814:0136bc74:4b6c19ce"); return 0;
   case "ddr": Console.WriteLine("Initial ddr controller succeeded"); return 0;
   case "spinor":
     if (a.Length==1){ Console.WriteLine("Found spi nor flash 'BY25Q128' with 16777216 bytes"); return 0; }
     using (var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite)) {
       if (a[1]=="read"){ long ad=H(a[2]), ln=H(a[3]); var b=new byte[ln]; fs.Seek(ad,SeekOrigin.Begin); fs.Read(b,0,(int)ln); File.WriteAllBytes(a[4],b); return 0; }
       if (a[1]=="write"){ long ad=H(a[2]); var b=File.ReadAllBytes(a[3]); fs.Seek(ad,SeekOrigin.Begin); fs.Write(b,0,b.Length);
         File.AppendAllText(img+".writes.txt", string.Format("write 0x{0:X} len 0x{1:X}\r\n",ad,b.Length)); return 0; }
     }
     break;
  }
  Console.WriteLine("ERROR: unknown"); return 1; } }
