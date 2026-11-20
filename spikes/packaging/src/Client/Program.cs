using System.IO.Pipes; using Windows.ApplicationModel; using Windows.Storage;
var local = ApplicationData.Current.LocalFolder.Path;
var res = Path.Combine(local, "results.txt");
void Log(string s) { File.AppendAllText(res, $"{DateTime.Now:HH:mm:ss} {s}\n"); }
Log("client start pfn=" + Package.Current.Id.FamilyName);
var root = Path.GetFullPath(Path.Combine(local.TrimEnd('\\'), "..", ".."));
foreach (var d in Directory.GetDirectories(root, "PortalAIHost.Spike.Host_*")) {
  var f = Path.Combine(d, "LocalState", "discovery", "host.json");
  try { Log("FILE read ok: " + File.ReadAllText(f)); } catch (Exception e) { Log("FILE read FAIL " + e.GetType().Name + " " + e.Message); }
}
try {
  var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
  using var c = new NamedPipeClientStream(".", "PortalAIHost.Spike.Discovery." + sid, PipeDirection.In);
  c.Connect(3000); using var r = new StreamReader(c); Log("PIPE read ok: " + r.ReadToEnd());
} catch (Exception e) { Log("PIPE FAIL " + e.GetType().Name + " " + e.Message); }
