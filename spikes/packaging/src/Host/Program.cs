using System.Diagnostics; using System.IO.Pipes; using System.Net; using System.Net.Sockets;
using System.Runtime.InteropServices; using System.Text;
using Windows.ApplicationModel; using Windows.Storage;

var local = ApplicationData.Current.LocalFolder.Path;
var res = Path.Combine(local, "results.txt");
void Log(string s) { File.AppendAllText(res, $"{DateTime.Now:HH:mm:ss} {s}\n"); }
Directory.CreateDirectory(Path.Combine(local, "discovery"));
Log($"start pid={Environment.ProcessId} pfn={Package.Current.Id.FamilyName} local={local}");

// (1) startup task
try {
  var t = await StartupTask.GetAsync("SpikeHostTask");
  Log($"startuptask GetAsync state={t.State}");
  var st = await t.RequestEnableAsync();
  Log($"startuptask RequestEnableAsync result={st}; after state={t.State}");
} catch (Exception e) { Log("startuptask EXC " + e.GetType().Name + " " + e.Message); }

// (2) child + job object
var childPath = Path.Combine(AppContext.BaseDirectory, "child", "Child.exe");
var job = CreateJobObject(IntPtr.Zero, null);
var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
int len = Marshal.SizeOf(info); var p = Marshal.AllocHGlobal(len); Marshal.StructureToPtr(info, p, false);
Log("job set=" + SetInformationJobObject(job, 9, p, (uint)len));
int childPort = 0;
try {
  var psi = new ProcessStartInfo(childPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
  var c = Process.Start(psi)!;
  Log("job assign=" + AssignProcessToJobObject(job, c.Handle) + " childpid=" + c.Id);
  var line = c.StandardOutput.ReadLine(); Log("child says: " + line);
  childPort = int.Parse(line!.Split(' ')[0].Substring(5));
  using var tc = new TcpClient(); await tc.ConnectAsync(IPAddress.Loopback, childPort);
  Log("connect to child 127.0.0.1:" + childPort + " ok=" + tc.Connected);
} catch (Exception e) { Log("child EXC " + e.GetType().Name + " " + e.Message); }

// (3) discovery file + fallback pipe
var json = $"{{\"port\":{childPort},\"instance_id\":\"spike-{Environment.ProcessId}\",\"pfn\":\"{Package.Current.Id.FamilyName}\"}}";
File.WriteAllText(Path.Combine(local, "discovery", "host.json"), json);
Log("wrote host.json");
var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
_ = Task.Run(async () => { while (true) {
  using var s = new NamedPipeServerStream("PortalAIHost.Spike.Discovery." + sid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
  await s.WaitForConnectionAsync(); var b = Encoding.UTF8.GetBytes(json); await s.WriteAsync(b); s.Disconnect(); } });
Log("ready");
var stop = Path.Combine(local, "stop.txt");
for (int i = 0; i < 1200 && !File.Exists(stop); i++) Thread.Sleep(500);
Log("exiting");

[DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? n);
[DllImport("kernel32")] static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr i, uint l);
[DllImport("kernel32")] static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
[StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a,b,c,d,e,f; }
[StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinWs, MaxWs; public uint Active; public UIntPtr Affinity; public uint Prio, Sched; }
[StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS Io; public UIntPtr a,b,c,d; }
