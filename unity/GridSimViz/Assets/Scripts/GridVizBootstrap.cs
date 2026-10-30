using UnityEngine;

/// <summary>
/// Spawns the <see cref="GridViz"/> manager automatically when the game starts, so
/// you can just press Play on an empty scene  -  no scene wiring required.
/// </summary>
public static class GridVizBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (Object.FindObjectOfType<GridViz>() != null) return;
        var go = new GameObject("GridViz");
        go.AddComponent<GridViz>();
    }
}
