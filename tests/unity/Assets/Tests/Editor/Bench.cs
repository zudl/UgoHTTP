using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using UgoHttp;
using UnityEngine;

static class Bench
{
    [Serializable] class Endpoints { public string http; }
    [Serializable] class Ready { public bool ready; public int pid; public Endpoints endpoints; }

    static bool Windows => Application.platform == RuntimePlatform.WindowsEditor;

    public static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
    public static string ProjectTemp => Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp"));

    public static UgoWebSocketRequest WebSocket(string httpUrl, string mode) =>
        new UgoWebSocketRequest("ws" + httpUrl.Substring(4) + "/ws/" + mode) { Headers = new[] { new UgoHeader("Authorization", "Bearer bench-token") } };

    // stderr goes to a file: a domain reload drops this domain's pipe readers while the bench keeps running.
    public static int Start(string name, out string url)
    {
        Directory.CreateDirectory(ProjectTemp);
        string err = Path.Combine(ProjectTemp, name + "-bench.err");
        ProcessStartInfo start = Windows
            ? new ProcessStartInfo(Path.Combine(Root, "tests/bench/.venv/Scripts/python.exe"),
                "\"" + Path.Combine(Root, "tests/bench/server.py") + "\" --stderr \"" + err + "\"")
            : new ProcessStartInfo("/bin/sh", "-c \"exec \\\"$0\\\" 2>\\\"$1\\\"\" \"" + Path.Combine(Root, "scripts/bench") + "\" \"" + err + "\"");
        start.WorkingDirectory = Root;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        using (Process bench = Process.Start(start))
        {
            try
            {
                Task<string> line = bench.StandardOutput.ReadLineAsync();
                Assert.That(line.Wait(30000), Is.True, "Bench did not publish readiness.");
                Ready ready = JsonUtility.FromJson<Ready>(line.Result);
                Assert.That(ready != null && ready.ready, Is.True, line.Result);
                url = ready.endpoints.http;
                // On Windows the venv's python.exe is a launcher and the server its child: stop the server.
                return Windows ? ready.pid : bench.Id;
            }
            catch
            {
                Stop(bench.Id);
                UnityEngine.Debug.Log(File.ReadAllText(err));
                throw;
            }
        }
    }

    public static void Stop(int pid)
    {
        Process bench;
        try { bench = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (bench)
        {
            if (Windows) SignalStop(pid);
            else using (Process stop = Process.Start(new ProcessStartInfo("/bin/kill", "-TERM " + pid) { UseShellExecute = false })) stop.WaitForExit();
            if (!bench.WaitForExit(10000)) { bench.Kill(); bench.WaitForExit(); }
        }
    }

    // Windows has no SIGTERM; the bench waits on this named event (tests/bench/server.py).
    static void SignalStop(int pid)
    {
        IntPtr handle = OpenEventW(0x0002, false, "ugohttp-bench-stop-" + pid);
        if (handle == IntPtr.Zero) return;
        SetEvent(handle);
        CloseHandle(handle);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32")] static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
}
