using UnityEngine;
using UnityEngine.Scripting;
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
#endif

// Nothing references this assembly, so without it the linker drops it and the player never installs the quit hook.
[assembly: AlwaysLinkAssembly]

namespace UgoHttp.Unity
{
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    static class Lifetime
    {
#if UNITY_EDITOR
        static HashSet<UgoClient> editClients;

        static Lifetime()
        {
            AssemblyReloadEvents.beforeAssemblyReload += UgoClient.ShutdownForDomainUnload;
            EditorApplication.quitting += UgoClient.ShutdownForDomainUnload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                lock (UgoClient.DomainSync)
                {
                    editClients = new HashSet<UgoClient>();
                    foreach (var live in UgoClient.Live)
                        if (live.TryGetTarget(out UgoClient client)) editClients.Add(client);
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var clients = new List<UgoClient>();
                lock (UgoClient.DomainSync)
                {
                    foreach (var live in UgoClient.Live)
                        if (live.TryGetTarget(out UgoClient client) && (editClients == null || !editClients.Contains(client)))
                            clients.Add(client);
                }
                foreach (UgoClient client in clients) client.Reclaim();
                editClients = null;
            }
        }
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Install()
        {
            if (Application.isEditor) return;
            Application.quitting += UgoClient.ShutdownForDomainUnload;
        }
    }
}
