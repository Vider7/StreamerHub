using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool PeekNamedPipe(SafeHandle hPipe, byte[]? lpBuffer, uint nBufferSize, out uint lpBytesRead, out uint lpTotalBytesAvail, out uint lpBytesLeftThisMessage);

uint AvailableBytes(NamedPipeClientStream p)
{
    PeekNamedPipe(p.SafePipeHandle, null, 0, out _, out var avail, out _);
    return avail;
}

var pipeName = "repro-ipc";
var mpvDefault = File.Exists(@"dist\tools\mpv\mpv.exe") ? @"dist\tools\mpv\mpv.exe" : @"..\..\..\dist\tools\mpv\mpv.exe";
var mpv = args.Length > 0 ? args[0] : mpvDefault;

var psi = new ProcessStartInfo
{
    FileName = Path.GetFullPath(mpv),
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
psi.ArgumentList.Add("--idle");
psi.ArgumentList.Add("--no-video");
psi.ArgumentList.Add("--vo=null");
psi.ArgumentList.Add("--no-terminal");
psi.ArgumentList.Add("--really-quiet");
psi.ArgumentList.Add("--volume-max=100");
psi.ArgumentList.Add("--volume=25");
psi.ArgumentList.Add("--input-ipc-server=" + @"\\.\pipe\" + pipeName);
if (args.Length > 1 && args[1] == "nullaudio") psi.ArgumentList.Add("--ao=null");

var proc = Process.Start(psi)!;
proc.OutputDataReceived += (_, e) => Console.WriteLine("[mpv out] " + e.Data);
proc.ErrorDataReceived += (_, e) => Console.WriteLine("[mpv err] " + e.Data);
proc.BeginOutputReadLine();
proc.BeginErrorReadLine();

NamedPipeClientStream? pipe = null;
while (pipe == null)
{
    try
    {
        var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        p.Connect(250);
        p.ReadMode = PipeTransmissionMode.Byte;
        pipe = p;
    }
    catch
    {
        if (proc.HasExited) return 1;
        Thread.Sleep(100);
    }
}
Console.WriteLine("pipe: connected");

var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
var reader = new StreamReader(pipe, Encoding.UTF8);
var cmds = Channel.CreateUnbounded<string>();
var _tp = 0;

void Enqueue(string cmd) => cmds.Writer.TryWrite(cmd);

Enqueue("{\"command\":[\"observe_property\",1,\"time-pos\"],\"request_id\":1}");
Enqueue("{\"command\":[\"observe_property\",2,\"pause\"],\"request_id\":2}");
Enqueue("{\"command\":[\"observe_property\",3,\"duration\"],\"request_id\":3}");

var pump = new Thread(() =>
{
    try
    {
        while (true)
        {
            while (cmds.Reader.TryRead(out var msg))
            {
                Console.WriteLine("[cmd>] " + msg);
                try
                {
                    writer.WriteLine(msg);
                    Console.WriteLine("[cmd>] wrote " + DateTime.Now.ToString("HH:mm:ss.fff"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[cmd>] FAIL " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }
            }
            if (AvailableBytes(pipe) > 0)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                if (line.Length >= 2 && line[0] == '{')
                {
                    var l2 = line.Length > 130 ? line[..130] : line;
                    if (l2.Contains("pause") || l2.Contains("time-pos", StringComparison.OrdinalIgnoreCase) && (++_tp % 5 == 0))
                        Console.WriteLine("[ipc<] " + l2);
                    if (l2.Contains("request_id")) Console.WriteLine("[ipc<REPLY>] " + l2);
                }
            }
            else
            {
                Thread.Sleep(10);
            }
        }
    }
    catch (Exception ex) { Console.WriteLine("[pump] died: " + ex.GetType().Name + ": " + ex.Message); }
}) { IsBackground = true, Name = "pump" };
pump.Start();

await Task.Delay(500);
Enqueue("{\"command\":[\"loadfile\",\"https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3\",\"replace\"],\"request_id\":4}");
await Task.Delay(7000);
Console.WriteLine(">> sending pause @ " + DateTime.Now.ToString("HH:mm:ss.fff"));
Enqueue("{\"command\":[\"set_property\",\"pause\",true],\"request_id\":7}");
await Task.Delay(3000);
Console.WriteLine(">> query pause @ " + DateTime.Now.ToString("HH:mm:ss.fff"));
Enqueue("{\"command\":[\"get_property\",\"pause\"],\"request_id\":8}");
await Task.Delay(2000);
Console.WriteLine(">> query time-pos @ " + DateTime.Now.ToString("HH:mm:ss.fff"));
Enqueue("{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":9}");
await Task.Delay(1500);
Console.WriteLine(">> done");
try { proc.Kill(entireProcessTree: true); } catch { }
return 0;