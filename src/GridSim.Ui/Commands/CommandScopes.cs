namespace GridSim.Ui.Commands;

/// <summary>
/// The session modes a command is offered in (UI programme, command system). GridSim has two: Author (edit, check and
/// save a case) and Run (load a saved case and simulate it, read only). A command lists every mode it belongs to; the
/// menu bar, the shortcuts and the context menus for a mode show only the commands that list it.
/// </summary>
[Flags]
public enum CommandModes
{
    /// <summary>No mode. Never valid on a registered command: the registry refuses it.</summary>
    None = 0,

    /// <summary>Author mode: offline case setup (desktop only; mobile has Run mode only).</summary>
    Author = 1,

    /// <summary>Run mode: the saved case is simulated and is read only.</summary>
    Run = 2,

    /// <summary>Both modes, for commands such as Exit or About that do not depend on the mode.</summary>
    Both = Author | Run,
}

/// <summary>
/// The kinds of head a command is offered on. Desktop is Windows, macOS and Linux; mobile is Android, iPhone and iPad
/// (the exploratory sandbox, Run mode only).
/// </summary>
[Flags]
public enum CommandPlatforms
{
    /// <summary>No platform. Never valid on a registered command: the registry refuses it.</summary>
    None = 0,

    /// <summary>The desktop app (Windows, macOS, Linux).</summary>
    Desktop = 1,

    /// <summary>The mobile heads (Android, iOS). Only Run-mode commands may be offered here.</summary>
    Mobile = 2,

    /// <summary>Every head.</summary>
    All = Desktop | Mobile,
}

/// <summary>
/// What a right-click (or long press) landed on, for context menus. A command lists the kinds it acts on; a context
/// menu for one kind holds every command that lists it. None means the command is not offered on any context menu.
/// No target exists in the shell yet; the map and the tables add them as they are ported.
/// </summary>
[Flags]
public enum CommandTargets
{
    /// <summary>Not offered on a context menu.</summary>
    None = 0,

    /// <summary>A bus (a node of the network).</summary>
    Bus = 1,

    /// <summary>A branch: a line, cable or transformer between two buses.</summary>
    Branch = 2,

    /// <summary>A generator, including converter-connected plant.</summary>
    Generator = 4,

    /// <summary>A demand (load) record.</summary>
    Demand = 8,

    /// <summary>A boundary (interconnector or cut-set) record.</summary>
    Boundary = 16,

    /// <summary>A substation as drawn on the map (one or more buses).</summary>
    Substation = 32,

    /// <summary>Empty map background (no element under the pointer).</summary>
    Map = 64,

    // UI programme Phase 4.1 (ASSETS plan section 3.5: "CommandTargets gains (additive flags): Transformer, Oltc,
    // Protection, AssetClass, AssetBinding, AssetClassNode ..."): the record kinds of the ASSETS tree in Author mode.
    // 128 is deliberately left unassigned: CommandRegistryTests pins (CommandTargets)128 as an unknown target that the
    // registry refuses, so the new flags start at 256. OverlayRecord joins with overlay editing (Phase 5b).

    /// <summary>A two-winding transformer record (Author mode tree).</summary>
    Transformer = 256,

    /// <summary>An on-load tap changer record (Author mode tree).</summary>
    Oltc = 512,

    /// <summary>A placed protection record (Author mode tree).</summary>
    Protection = 1024,

    /// <summary>An asset (dynamics) class record (Author mode tree).</summary>
    AssetClass = 2048,

    /// <summary>An asset binding rule record (Author mode tree).</summary>
    AssetBinding = 4096,

    /// <summary>A class node of the ASSETS tree (a whole collection of records: Buses, Generation, ...); Add acts on it.</summary>
    AssetClassNode = 8192,

    // UI programme Phase 5b (2026-10-20): the asset overlay in Author mode. OverlayRecord is the plan's (section 3.5);
    // OverlayAnchor is where "Add overlay record..." also acts: a model asset an overlay record can be attached to (a bus,
    // line, transformer, generator or demand record of a case with an overlay file) and the "Asset overlay" class node.
    // It is a target of its own, offered beside the node's own kind, so the context menu of each model record kind is
    // exactly as before for a case without an overlay (and the registry still declares every command once).

    /// <summary>An asset overlay record (Author mode tree).</summary>
    OverlayRecord = 16384,

    /// <summary>A node an overlay record can be added at: a model asset it attaches to, or the overlay class node.</summary>
    OverlayAnchor = 32768,

    // UI programme Phase 4.2 (2026-10-20): map editing in AUTHOR mode. A bus or a branch right-clicked on the NETWORK map
    // in Author mode has a menu of its own (Delete, Show in Assets), apart from the ASSETS tree's record menus, so the
    // tree's menus for Bus, Branch and Transformer records are exactly as before and the map's menu never offers a
    // tree-only command (or the reverse).

    /// <summary>A bus or a branch on the NETWORK map in Author mode (the map's own context menu there).</summary>
    MapElement = 65536,

    // UI programme Phase 4.4 (2026-10-21): the templates of a v2 case in AUTHOR mode. Each is a node kind of the ASSETS
    // tree's TEMPLATES subtree with a menu of its own, so the context menu of every record kind (a generator's included)
    // is exactly as before.

    /// <summary>The TEMPLATES class node (Add template... acts on it).</summary>
    TemplateClassNode = 131072,

    /// <summary>A template node (Duplicate, Delete, Bind to template...).</summary>
    Template = 262144,

    /// <summary>An override node under its template (Reset override).</summary>
    TemplateOverride = 524288,

    // UI programme Phase 5f follow-up (2026-10-24): a transformer3W overlay record also has the star-legs target, offered
    // after its OverlayRecord commands, so "Derive star legs..." is on its context menu while the menu of every other
    // overlay record (and every model record) is exactly as before.

    /// <summary>A transformer3W overlay record (Derive star legs acts on it).</summary>
    StarTransformer = 1048576,
}
