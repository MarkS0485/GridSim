using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Loads a GridSim viz export (StreamingAssets/gridviz.json) and draws the grid:
/// buses as nodes coloured by voltage, branches as lines, animated across the
/// day's frames. Interactive  -  drag to pan, scroll to zoom, click a bus to inspect
/// it, on-screen controls to play/pause/scrub/change speed.
///
/// Self-contained: it builds its own camera and geometry at runtime, so no scene
/// authoring is needed (see GridVizBootstrap). Pipeline-agnostic material handling
/// so it works under Built-in or URP.
/// </summary>
public class GridViz : MonoBehaviour
{
    public string fileName = "gridviz.json";
    public float span = 22f;        // world size the 0..1 layout maps into

    private VizScene _scene;
    private Camera _cam;
    private readonly List<GameObject> _nodes = new();
    private readonly List<Renderer> _nodeRenderers = new();
    private readonly List<LineRenderer> _lines = new();
    private Dictionary<int, int> _idToIndex = new();

    private int _frame;
    private float _acc;
    private bool _playing = true;
    private float _framesPerSecond = 3f;
    private int _selected = -1;

    private Vector3 _dragOrigin;
    private bool _dragging;

    // True when the export carries the per-frame "converged" flag. An older export has no such key, so its frames'
    // validity is unknown: they are drawn as before, with an on-screen warning to re-export.
    private bool _hasConvergedFlag;

    private void Start()
    {
        if (!Load()) return;
        SetupCamera();
        BuildNodes();
        BuildBranches();
        if (_scene.frames.Length > 0) ApplyFrame(0);
    }

    /// <summary>
    /// Read and check the export. Every failure is logged with the reason and stops the viewer (it never draws a
    /// half-read file): a missing file, unparseable JSON, no buses, or no frames. The checks run BEFORE anything is
    /// indexed; the bus count used to be read before the null test, and frame 0 was applied without checking that
    /// any frame existed.
    /// </summary>
    private bool Load()
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"GridViz: {path} not found. Run `gridsim export <...>/Assets/StreamingAssets/gridviz.json`.");
            return false;
        }
        string json = File.ReadAllText(path);
        try { _scene = JsonUtility.FromJson<VizScene>(json); }
        catch (System.ArgumentException ex)
        {
            Debug.LogError($"GridViz: {path} is not a readable viz export: {ex.Message}");
            _scene = null;
            return false;
        }
        if (_scene == null || _scene.buses == null || _scene.buses.Length == 0)
        {
            Debug.LogError($"GridViz: {path} has no buses; nothing to draw.");
            _scene = null;
            return false;
        }
        if (_scene.frames == null || _scene.frames.Length == 0)
        {
            Debug.LogError($"GridViz: {path} has no frames; re-export with --frames.");
            _scene = null;
            return false;
        }
        _scene.branches ??= new VizBranch[0];
        _hasConvergedFlag = json.Contains("\"converged\"");
        if (!_hasConvergedFlag)
            Debug.LogWarning("GridViz: this export has no per-frame convergence flag (it predates it); frame validity is unknown. Re-export with gridsim export.");
        _idToIndex = new Dictionary<int, int>();
        for (int i = 0; i < _scene.buses.Length; i++) _idToIndex[_scene.buses[i].id] = i;
        return true;
    }

    /// <summary>True when the frame is known NOT to be a valid network state (its power flow did not converge).</summary>
    private bool FrameInvalid(VizFrame fr) => _hasConvergedFlag && !fr.converged;

    private void SetupCamera()
    {
        _cam = Camera.main;
        if (_cam == null)
        {
            var go = new GameObject("GridViz Camera");
            _cam = go.AddComponent<Camera>();
            go.tag = "MainCamera";
        }
        _cam.orthographic = true;
        _cam.orthographicSize = span * 0.6f;
        _cam.transform.position = new Vector3(0, 0, -10);
        _cam.transform.rotation = Quaternion.identity;
        _cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
        _cam.clearFlags = CameraClearFlags.SolidColor;
    }

    private Vector3 WorldOf(VizBus b) => new((b.x - 0.5f) * span, (b.y - 0.5f) * span, 0f);

    private void BuildNodes()
    {
        foreach (VizBus b in _scene.buses)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"Bus {b.id} {b.name}";
            go.transform.SetParent(transform);
            go.transform.position = WorldOf(b);
            float r = b.kv >= 200 ? 0.7f : 0.45f;      // 400 kV bigger than 132 kV
            go.transform.localScale = Vector3.one * r;
            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = MakeMaterial(Color.gray);
            _nodes.Add(go);
            _nodeRenderers.Add(rend);
        }
    }

    private void BuildBranches()
    {
        var lineMat = MakeMaterial(new Color(0.4f, 0.45f, 0.5f));
        var txMat = MakeMaterial(new Color(0.85f, 0.55f, 0.2f));
        foreach (VizBranch br in _scene.branches)
        {
            if (!_idToIndex.TryGetValue(br.from, out int fi) || !_idToIndex.TryGetValue(br.to, out int ti)) continue;
            var go = new GameObject($"Branch {br.name}");
            go.transform.SetParent(transform);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = br.transformer ? txMat : lineMat;
            lr.widthMultiplier = br.transformer ? 0.12f : 0.08f;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.numCapVertices = 2;
            lr.SetPosition(0, WorldOf(_scene.buses[fi]) + Vector3.forward * 0.5f);
            lr.SetPosition(1, WorldOf(_scene.buses[ti]) + Vector3.forward * 0.5f);
            _lines.Add(lr);
        }
    }

    private void ApplyFrame(int f)
    {
        VizFrame fr = _scene.frames[f];
        // Fail loud: a frame whose power flow did not converge is not a network state, so its voltages are never
        // coloured as one. Every node is drawn grey and the panel says why (see OnGUI).
        if (FrameInvalid(fr) || fr.vm == null)
        {
            for (int i = 0; i < _nodeRenderers.Count; i++) SetColor(_nodeRenderers[i], InvalidGrey);
            return;
        }
        for (int i = 0; i < _nodeRenderers.Count && i < fr.vm.Length; i++)
            SetColor(_nodeRenderers[i], HeatColor.ForVoltage(fr.vm[i]));
    }

    private static readonly Color InvalidGrey = new(0.35f, 0.35f, 0.38f);

    private void Update()
    {
        if (_scene == null || _scene.frames == null || _scene.frames.Length == 0) return;

        if (_playing)
        {
            _acc += Time.deltaTime * _framesPerSecond;
            while (_acc >= 1f)
            {
                _acc -= 1f;
                _frame = (_frame + 1) % _scene.frames.Length;
                ApplyFrame(_frame);
            }
        }

        HandleMouse();
    }

    private void HandleMouse()
    {
        // Zoom.
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
            _cam.orthographicSize = Mathf.Clamp(_cam.orthographicSize * (1f - scroll * 0.1f), 2f, span * 2f);

        // Pan with right/middle button.
        if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
        {
            _dragOrigin = _cam.ScreenToWorldPoint(Input.mousePosition);
            _dragging = true;
        }
        if (_dragging && (Input.GetMouseButton(1) || Input.GetMouseButton(2)))
        {
            Vector3 diff = _dragOrigin - _cam.ScreenToWorldPoint(Input.mousePosition);
            _cam.transform.position += new Vector3(diff.x, diff.y, 0);
        }
        if (Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2)) _dragging = false;

        // Select with left click.
        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                int idx = _nodes.IndexOf(hit.collider.gameObject);
                _selected = idx;
            }
            else _selected = -1;
        }
    }

    private void OnGUI()
    {
        if (_scene == null || _scene.frames == null || _scene.frames.Length == 0) return;
        VizFrame fr = _scene.frames[_frame];

        GUI.Box(new Rect(8, 8, 320, 150), GUIContent.none);
        GUILayout.BeginArea(new Rect(16, 14, 304, 140));
        GUILayout.Label($"<b>{_scene.name}</b>   t = {fr.hour:00.0}h");
        GUILayout.Label($"Load {fr.loadGw:F2} GW   Gen {fr.genGw:F2} GW   Loss {fr.lossMw:F0} MW   {fr.freqHz:F2} Hz");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_playing ? " Pause" : " Play", GUILayout.Width(90))) _playing = !_playing;
        GUILayout.Label($"speed {_framesPerSecond:F1} fps", GUILayout.Width(90));
        if (GUILayout.Button("-", GUILayout.Width(28))) _framesPerSecond = Mathf.Max(0.5f, _framesPerSecond / 1.5f);
        if (GUILayout.Button("+", GUILayout.Width(28))) _framesPerSecond = Mathf.Min(60f, _framesPerSecond * 1.5f);
        GUILayout.EndHorizontal();

        float nf = GUILayout.HorizontalSlider(_frame, 0, _scene.frames.Length - 1);
        int ni = Mathf.RoundToInt(nf);
        if (ni != _frame) { _frame = ni; _playing = false; ApplyFrame(_frame); }
        GUILayout.Label(FrameInvalid(fr) ? $"frame {_frame + 1}/{_scene.frames.Length}  NOT CONVERGED: not a network state"
                        : _hasConvergedFlag ? $"frame {_frame + 1}/{_scene.frames.Length}"
                        : $"frame {_frame + 1}/{_scene.frames.Length}  (export has no convergence flag)");
        GUILayout.EndArea();

        if (_selected >= 0 && _selected < _scene.buses.Length)
        {
            VizBus b = _scene.buses[_selected];
            GUI.Box(new Rect(8, 168, 320, 120), GUIContent.none);
            GUILayout.BeginArea(new Rect(16, 174, 304, 110));
            GUILayout.Label($"<b>{b.name}</b>  ({b.kv:F0} kV  {b.type})");
            if (FrameInvalid(fr))
                GUILayout.Label("power flow did not converge on this frame: no valid values");
            else
            {
                GUILayout.Label($"Vm {Read(fr.vm, _selected):F4} p.u.   Va {Read(fr.vaDeg, _selected):F2} deg");
                GUILayout.Label($"P {Read(fr.pInjMw, _selected):F1} MW   Q {Read(fr.qInjMvar, _selected):F1} MVAr");
            }
            GUILayout.EndArea();
        }

        GUI.Label(new Rect(8, Screen.height - 24, 700, 20),
            "drag (right/middle) to pan  scroll to zoom  left-click a bus to inspect");
    }

    // A per-bus value, or NaN (shown as "NaN") when the frame's array is missing or short: never an index error.
    private static float Read(float[] a, int i) => a != null && i >= 0 && i < a.Length ? a[i] : float.NaN;

    // ---- pipeline-agnostic material helpers ----

    private static Material MakeMaterial(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
                 ?? Shader.Find("Unlit/Color")
                 ?? Shader.Find("Sprites/Default")
                 ?? Shader.Find("Standard");
        var m = new Material(sh);
        SetMatColor(m, c);
        return m;
    }

    private static void SetColor(Renderer r, Color c) => SetMatColor(r.material, c);

    private static void SetMatColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        m.color = c;
    }
}
