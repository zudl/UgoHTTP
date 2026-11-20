using System.Text;
using System.Threading;
using UnityEngine;

// On-screen readout of the checks' own state, rebuilt on the main thread once a second without touching the check threads' locks.
// A TextMesh, not OnGUI: IMGUI allocates every frame. The one string a second is main-thread garbage.
sealed class PlayerStatus : MonoBehaviour
{
    const int LineWidth = 40;
    readonly StringBuilder builder = new StringBuilder(512);
    TextMesh mesh;
    Camera view;
    float next;
    bool fitted;

    public static void Show()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        var host = new GameObject("UgoStatus");
        DontDestroyOnLoad(host);
        var view = host.AddComponent<Camera>();
        view.clearFlags = CameraClearFlags.SolidColor;
        view.backgroundColor = Color.black;
        view.orthographic = true;
        view.orthographicSize = 5;
        host.transform.position = new Vector3(0, 0, -10);
        var text = new GameObject("UgoStatusText");
        text.transform.SetParent(host.transform, false);
        text.transform.localPosition = new Vector3(0, 0, 10);
        var mesh = text.AddComponent<TextMesh>();
        mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.GetComponent<MeshRenderer>().sharedMaterial = mesh.font.material;
        mesh.fontSize = 48;
        mesh.anchor = TextAnchor.UpperLeft;
        mesh.color = Color.white;
        mesh.text = new string('M', LineWidth);
        var status = host.AddComponent<PlayerStatus>();
        status.mesh = mesh;
        status.view = view;
    }

    void Update()
    {
        if (!fitted)
        {
            // Scale once so that LineWidth characters span 90 % of the screen width, whatever the device.
            float width = mesh.GetComponent<MeshRenderer>().bounds.size.x;
            if (width <= 0) return;
            float screen = 2 * view.orthographicSize * view.aspect;
            mesh.transform.localScale = Vector3.one * (0.9f * screen / width);
            mesh.transform.localPosition = new Vector3(-0.45f * screen, 0.8f * view.orthographicSize, 10);
            fitted = true;
        }
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 1;
        StringBuilder b = builder.Clear();
        b.Append("UgoHTTP player: ").Append(PlayerChecks.Mode).Append('\n');
        if (Soak.DurationSeconds > 0)
        {
            long started = Volatile.Read(ref Soak.StartedMicroseconds);
            long elapsed = started == 0 ? 0 : (UgoHttp.UgoClient.MonotonicMicroseconds - started) / 1_000_000;
            b.Append("elapsed ").Append(elapsed / 60).Append(" min, left ")
                .Append(System.Math.Max(0, Soak.DurationSeconds - elapsed) / 60).Append(" min\n");
            foreach (Soak.Socket s in Soak.Sockets)
                b.Append(s.Form).Append(": sent ").Append(Volatile.Read(ref s.Sent))
                    .Append(", recv ").Append(Volatile.Read(ref s.Pushes) + Volatile.Read(ref s.Echoes))
                    .Append("\n  mismatches ").Append(Volatile.Read(ref s.Mismatches)).Append('\n');
            b.Append("RSS ").Append(Volatile.Read(ref Soak.LastResidentBytes) >> 20).Append(" MiB\n");
        }
        else if (PlayerChecks.Mode == "checks")
            b.Append("check ").Append(PlayerChecks.Current).Append("\npassed ").Append(Volatile.Read(ref PlayerChecks.Passed))
                .Append(", failed ").Append(Volatile.Read(ref PlayerChecks.Failed)).Append('\n');
        int verdict = PlayerChecks.Verdict;
        b.Append(verdict == 0 ? "running" : verdict == 1 ? "PASS" : "FAIL");
        mesh.text = b.ToString();
    }
}
