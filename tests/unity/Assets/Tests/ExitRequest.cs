using System;
using UnityEngine;

public sealed class ExitRequest : MonoBehaviour
{
    public Action OnDestroyed;

    void OnDestroy() => OnDestroyed?.Invoke();
}
