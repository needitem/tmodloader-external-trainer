using System.Runtime.InteropServices;
using System.Threading;
using TerrariaTrainer.Cheats;
using TerrariaTrainer.Tml;

namespace TerrariaTrainer.UI;

/// <summary>Cheat-Engine-style trainer UI for tModLoader (64-bit), backed by ClrMD.</summary>
public sealed class TmlForm : Form
{
    private readonly TmlEngine _engine = new();
    private readonly TmlBuffs _buffs = new();
    private List<CheatRow> _rows = new();

    private readonly Button _btnAttach = new() { Text = "Attach", Width = 80, Height = 26 };
    private readonly Button _btnRescan = new() { Text = "Re-scan", Width = 80, Height = 26, Enabled = false };
    private readonly Label _status = new() { Text = "Searching for tModLoader…", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 6, 0, 0) };
    private readonly Label _vitals = new() { Text = "", AutoSize = true, ForeColor = Color.SteelBlue, Padding = new Padding(12, 6, 0, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    private readonly CheckBox _autoAttach = new() { Text = "auto", Checked = true, AutoSize = true, Padding = new Padding(6, 5, 0, 0) };
    private readonly TextBox _search = new() { Width = 220, PlaceholderText = "search…" };
    private readonly Button _btnDisableAll = new() { Text = "Disable All", Width = 100, Height = 26 };

    // Recipe condition checks patched to "return true" for Craft Anything.
    private static readonly string[] CraftKeys =
    {
        "Recipe.PlayerMeetsEnvironmentConditions", "Recipe.PlayerMeetsTileRequirements",
        "Recipe.CollectedEnoughItemsToCraftRecipeNew", "RecipeLoader.RecipeAvailable",
    };

    private int _attachThrottle = 10; // attempt auto-attach on the first tick
    private string _lastScopedStatus = "";
    private string _lastSpawnStatus = "";
    private string _lastRareStatus = "";
    private TmlField? _fLife, _fLifeMax, _fMana, _fManaMax;

    // High-frequency writer: per-frame-recomputed values (move/mine speed) must be written
    // far faster than the 350ms UI tick to actually hold. ~2ms with 1ms timer resolution.
    private Thread? _writer;
    private volatile bool _writerRun = true;

    // Re-applies method-entry patches across .NET tiered-JIT relocations (every ~2.5s).
    private readonly StickyPatcher _sticky;
    private Thread? _stickyThread;
    // "100% drop, only for me" — targets the MP host server process when present.
    private readonly ScopedDropPatcher _scopedDrop;
    // Spawn-rate boost (rare mobs appear far more often) — also targets the MP server.
    private readonly SpawnBoostPatcher _spawnBoost;
    // Force every natural spawn to a chosen rare mob — also targets the MP server.
    private readonly RareSpawnPatcher _rareSpawn;
    private List<(int type, int stars, string name)> _rareNpcs = new();
    private int _rareSelType; // last-picked rare mob type (fallback for the toggle)

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new();
    private readonly DataGridView _invGrid = new();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(245, 245, 245) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 350 };

    private static readonly Color HeaderBg = Color.FromArgb(60, 63, 75);
    private static readonly Color HeaderFg = Color.Gainsboro;

    public TmlForm()
    {
        Text = "tModLoader Trainer";
        Width = 880; Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _sticky = new StickyPatcher(_engine);
        _scopedDrop = new ScopedDropPatcher(_engine);
        _spawnBoost = new SpawnBoostPatcher(_engine);
        _rareSpawn = new RareSpawnPatcher(_engine);
        _engine.Log += AppendLog;
        _btnAttach.Click += (_, _) => DoAttach();
        _btnRescan.Click += (_, _) => DoRescan();
        _btnDisableAll.Click += (_, _) => DisableAll();
        _search.TextChanged += (_, _) => RebuildGrid();
        _timer.Tick += (_, _) => Tick();

        BuildLayout();
        BuildGrid();
        _timer.Start();

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "freeze-writer" };
        _writer.Start();

        _stickyThread = new Thread(StickyLoop) { IsBackground = true, Name = "sticky-repatch" };
        _stickyThread.Start();
    }

    /// <summary>Periodically re-asserts method patches so the .NET tiered JIT can't bypass them.</summary>
    private void StickyLoop()
    {
        while (_writerRun)
        {
            try { if (_engine.Attached && _sticky.AnyActive) _sticky.TickOnce(); } catch { }
            try { if (_scopedDrop.Enabled) _scopedDrop.Tick(); } catch { }
            try { if (_spawnBoost.Enabled) _spawnBoost.Tick(); } catch { }
            try { if (_rareSpawn.Enabled) _rareSpawn.Tick(); } catch { }
            Thread.Sleep(_sticky.AnyActive || _scopedDrop.Enabled || _spawnBoost.Enabled || _rareSpawn.Enabled ? 2500 : 1000);
        }
    }

    // x64 return stubs.
    private static byte[] RetZero => new byte[] { 0x31, 0xC0, 0xC3 };                          // xor eax,eax; ret
    private static byte[] RetUZero => new byte[] { 0x31, 0xC0, 0x0F, 0x57, 0xC0, 0xC3 };        // xor eax; xorps xmm0; ret (int/float/double)
    private static byte[] RetTrue => new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 };          // mov eax,1; ret
    private static byte[] RetFloat(float v) { var c = new byte[] { 0xB8, 0, 0, 0, 0, 0x66, 0x0F, 0x6E, 0xC0, 0xC3 }; BitConverter.GetBytes(v).CopyTo(c, 1); return c; }
    private static byte[] RetInt(int v) { var c = new byte[] { 0xB8, 0, 0, 0, 0, 0xC3 }; BitConverter.GetBytes(v).CopyTo(c, 1); return c; } // mov eax,imm32; ret

    /// <summary>Continuously asserts active cheats at ~2ms so per-frame-recomputed values hold.</summary>
    private void WriterLoop()
    {
        timeBeginPeriod(1);
        try
        {
            while (_writerRun)
            {
                try
                {
                    if (_engine.Attached)
                        lock (_engine.Sync) AssertActiveWrites();
                }
                catch { /* transient (process gone, list swap) */ }
                Thread.Sleep(2);
            }
        }
        finally { timeEndPeriod(1); }
    }

    private void AssertActiveWrites()
    {
        if (_engine.PlayerBase() == IntPtr.Zero) return;
        var rows = _rows; // snapshot reference (rebuilds swap the field, never mutate in place)
        foreach (var r in rows)
        {
            if (!r.Active) continue;
            switch (r.Kind)
            {
                case RowKind.Value: if (r.FrozenText != null) _engine.WriteField(r.Field!, r.FrozenText); break;
                case RowKind.Toggle: _engine.WriteField(r.Field!, "true"); break;
                case RowKind.Inject: _engine.WriteField(r.Field!, r.InjectValue); break;
                case RowKind.Fast: _engine.WriteField(r.Field!, r.InjectValue); break;
                case RowKind.Tools: _engine.AssertCachedTools(int.TryParse(r.InjectValue, out var t) ? t : 1, 4); break;
                case RowKind.Craft: _engine.SetCraftAnywhere(); break;
                case RowKind.BuffClear: if (int.TryParse(r.InjectValue, out var bid)) _engine.ClearBuff(bid); break;
                case RowKind.InfAmmo: _engine.TopAmmo(); break;
            }
        }
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 6, 0, 0), BackColor = Color.White };
        top.Controls.Add(_btnAttach);
        top.Controls.Add(_btnRescan);
        top.Controls.Add(_autoAttach);
        top.Controls.Add(_status);
        top.Controls.Add(_vitals);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 5, 0, 0) };
        bar.Controls.Add(new Label { Text = "Find:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        bar.Controls.Add(_search);
        bar.Controls.Add(_btnDisableAll);

        var cheatsTab = new TabPage("Cheats");
        _grid.Dock = DockStyle.Fill;
        cheatsTab.Controls.Add(_grid);

        var invTab = new TabPage("Inventory");
        _invGrid.Dock = DockStyle.Fill;
        var invNote = new Label
        {
            Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray,
            Text = "  Item = name · Count = quantity (double-click to edit) · Modifier = prefix · ID/Pfx# = raw numbers. Changing ID may need a world reload.",
        };
        invTab.Controls.Add(_invGrid);
        invTab.Controls.Add(invNote);
        invNote.BringToFront();

        _tabs.TabPages.Add(cheatsTab);
        _tabs.TabPages.Add(invTab);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_tabs);
        var logHeader = new Label { Text = "  Log", Dock = DockStyle.Top, Height = 18, ForeColor = Color.DimGray };
        split.Panel2.Controls.Add(_log);
        split.Panel2.Controls.Add(logHeader);
        logHeader.BringToFront();

        Controls.Add(split);
        Controls.Add(bar);
        Controls.Add(top);
        Load += (_, _) => split.SplitterDistance = (int)(ClientSize.Height * 0.78);
    }

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.EnableHeadersVisualStyles = false;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Name = "active", Width = 42 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "desc", ReadOnly = true, Width = 380, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "type", ReadOnly = true, Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "value", Width = 150 });

        _grid.CellClick += Grid_CellClick;
        _grid.CellDoubleClick += Grid_CellDoubleClick;
        _grid.CellEndEdit += Grid_CellEndEdit;
        // commit combo-box (rare-spawn picker) selections immediately
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewComboBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += Grid_CellValueChanged;
        // The rare-spawn combo cell lives in a text column; WinForms surfaces transient value
        // mismatches as a modal dialog. Suppress it — the combo still works correctly.
        _grid.DataError += (_, e) => { e.ThrowException = false; e.Cancel = true; };

        BuildInvGrid();
    }

    private void BuildInvGrid()
    {
        _invGrid.AutoGenerateColumns = false;
        _invGrid.AllowUserToAddRows = false;
        _invGrid.AllowUserToResizeRows = false;
        _invGrid.RowHeadersVisible = false;
        _invGrid.MultiSelect = false;
        _invGrid.BackgroundColor = Color.White;
        _invGrid.BorderStyle = BorderStyle.None;
        _invGrid.EnableHeadersVisualStyles = false;
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", Name = "slot", ReadOnly = true, Width = 90 });
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Item", Name = "name", ReadOnly = true, Width = 210, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Count", Name = "stack", Width = 70, ToolTipText = "Stack — quantity. Double-click to edit." });
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Max", Name = "maxStack", ReadOnly = true, Width = 60, ToolTipText = "Max stack for this item." });
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Modifier", Name = "modifier", ReadOnly = true, Width = 110, ToolTipText = "Prefix name (e.g. Legendary)." });
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", Name = "type", Width = 70, ToolTipText = "Item type ID. Editable — changing it may need a world reload." });
        _invGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pfx#", Name = "prefix", Width = 55, ToolTipText = "Prefix number. Editable." });
        _invGrid.CellEndEdit += InvGrid_CellEndEdit;
    }

    private static string SlotLabel(int i) => i switch
    {
        < 10 => $"Hotbar {i}",
        < 50 => $"Inv {i}",
        < 54 => $"Coin {i - 50}",
        < 58 => $"Ammo {i - 54}",
        _ => $"Slot {i}",
    };

    private void BuildInvRows()
    {
        _invGrid.Rows.Clear();
        int len = _engine.InventoryLength();
        if (len <= 0) return;
        for (int i = 0; i < Math.Min(len, 58); i++)
        {
            int idx = _invGrid.Rows.Add(SlotLabel(i), "", "", "", "", "", "");
            _invGrid.Rows[idx].Tag = i;
        }
    }

    private void InvGrid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var row = _invGrid.Rows[e.RowIndex];
        if (row.Tag is not int slot) return;
        string col = _invGrid.Columns[e.ColumnIndex].Name;
        if (col is not ("type" or "stack" or "prefix")) return;
        if (!int.TryParse(Convert.ToString(row.Cells[col].Value), out int val)) return;
        lock (_engine.Sync)
        {
            if (_engine.SetItemInt(slot, col, val)) AppendLog($"Slot {slot}: {col} = {val}");
            else AppendLog($"Slot {slot}: failed to set {col}");
        }
    }

    private void RefreshInvGrid()
    {
        if (_invGrid.Rows.Count == 0) BuildInvRows();
        var editing = _invGrid.IsCurrentCellInEditMode ? _invGrid.CurrentCell : null;
        foreach (DataGridViewRow row in _invGrid.Rows)
        {
            if (row.Tag is not int slot) continue;
            int type = _engine.ItemInt(slot, "type");
            bool empty = _engine.InventoryItem(slot) == IntPtr.Zero || type == 0;
            int prefix = empty ? 0 : _engine.ItemInt(slot, "prefix");
            SetIfNotEditing(row.Cells["name"], empty ? "(empty)" : _engine.ItemName(type), editing);
            SetIfNotEditing(row.Cells["type"], empty ? "0" : type.ToString(), editing);
            SetIfNotEditing(row.Cells["stack"], empty ? "" : _engine.ItemInt(slot, "stack").ToString(), editing);
            SetIfNotEditing(row.Cells["maxStack"], empty ? "" : _engine.ItemInt(slot, "maxStack").ToString(), editing);
            SetIfNotEditing(row.Cells["prefix"], empty ? "" : prefix.ToString(), editing);
            SetIfNotEditing(row.Cells["modifier"], prefix > 0 ? _engine.PrefixName(prefix) : "", editing);
        }
    }

    private static void SetIfNotEditing(DataGridViewCell cell, string val, DataGridViewCell? editing)
    {
        if (!ReferenceEquals(cell, editing)) cell.Value = val;
    }

    // ---- attach ----

    private void DoAttach(bool silent = false)
    {
        try
        {
            _btnAttach.Enabled = false;
            AppendLog("Attaching + ClrMD discovery (brief pause is normal)…");
            lock (_engine.Sync)
            {
                _engine.Attach();
                _rows = CheatTable.Build(_engine.Model!, _buffs.Buffs, _engine.Injector);
            }
            _sticky.Clear(); // fresh process: drop any stale patched-address bookkeeping
            _scopedDrop.Clear();
            _spawnBoost.Clear();
            _rareSpawn.Clear();
            try { _rareNpcs = TmlDiscovery.EnumerateRareNpcs(_engine.Proc!.Id, 2); AppendLog($"Loaded {_rareNpcs.Count} rare mobs for the spawn picker."); } catch { _rareNpcs = new(); }
            CacheVitalFields();
            lock (_engine.Sync) LoadAndApplyConfig(); // restore previously-enabled cheats
            RebuildGrid();
            _invGrid.Rows.Clear(); // rebuilt lazily for the new session
            _btnRescan.Enabled = true;
            _status.Text = "Attached";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception ex)
        {
            AppendLog("ATTACH ERROR: " + ex.Message);
            if (!silent) MessageBox.Show(ex.Message, "Attach failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _status.Text = "Attach failed"; _status.ForeColor = Color.Firebrick;
        }
        finally { _btnAttach.Enabled = true; }
    }

    private void CacheVitalFields()
    {
        TmlField? Find(string n) => _engine.Model!.PlayerFields.FirstOrDefault(f => f.Name == n);
        _fLife = Find("statLife"); _fLifeMax = Find("statLifeMax2");
        _fMana = Find("statMana"); _fManaMax = Find("statManaMax2");
    }

    /// <summary>Re-run ClrMD discovery and rebuild the table, preserving active toggles/freezes.</summary>
    private void DoRescan()
    {
        if (!_engine.Attached) return;
        // snapshot active state by description
        var prev = _rows.Where(r => r.Kind is RowKind.Value or RowKind.Toggle or RowKind.Fast or RowKind.Inject)
            .ToDictionary(r => r.Desc, r => (r.Active, r.FrozenText));
        lock (_engine.Sync)
        {
            if (!_engine.Rediscover()) return;
            _rows = CheatTable.Build(_engine.Model!, _buffs.Buffs, _engine.Injector);
        }
        foreach (var r in _rows)
            if (prev.TryGetValue(r.Desc, out var s)) { r.Active = s.Active; r.FrozenText = s.FrozenText; }
        CacheVitalFields();
        RebuildGrid();
        AppendLog("Re-scan complete (offsets refreshed).");
    }

    // ---- config persistence (remember enabled cheats across restarts) ----

    private static string ConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "trainer_config.json");

    /// <summary>Persist every currently-active row (by description) so it can be restored next launch.</summary>
    private void SaveConfig()
    {
        try
        {
            var data = new Dictionary<string, string>();
            foreach (var r in _rows)
            {
                if (r.Kind is RowKind.GroupHeader or RowKind.Action || !r.Active) continue;
                data[r.Desc] = r.Kind == RowKind.Value ? (r.FrozenText ?? "")
                    : r.Kind is RowKind.DropMult or RowKind.PatchInt or RowKind.SpawnBoost or RowKind.RareSpawn ? r.InjectValue
                    : "on";
            }
            System.IO.File.WriteAllText(ConfigPath,
                System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Re-enable + re-install every cheat saved in the config (called after attach/build).</summary>
    private void LoadAndApplyConfig()
    {
        Dictionary<string, string>? data = null;
        try
        {
            if (System.IO.File.Exists(ConfigPath))
                data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(ConfigPath));
        }
        catch { return; }
        if (data == null || data.Count == 0) return;
        int applied = 0;
        foreach (var r in _rows)
        {
            if (r.Kind is RowKind.GroupHeader or RowKind.Action) continue;
            if (!data.TryGetValue(r.Desc, out var saved)) continue;
            r.Active = true;
            if (r.Kind == RowKind.Value) r.FrozenText = saved;
            else if (r.Kind is RowKind.DropMult or RowKind.PatchInt or RowKind.SpawnBoost or RowKind.RareSpawn && saved.Length > 0 && int.TryParse(saved, out _)) r.InjectValue = saved;
            try { ApplyActiveChange(r, null); applied++; }
            catch { r.Active = false; }
        }
        if (applied > 0) AppendLog($"Restored {applied} saved cheat(s) from trainer_config.json.");
    }

    // ---- grid build ----

    private IEnumerable<CheatRow> Filtered()
    {
        string find = _search.Text.Trim();
        if (find.Length == 0) return _rows;
        // keep group headers whose group matches, plus any row matching text
        return _rows.Where(r => r.Kind == RowKind.GroupHeader
            ? _rows.Any(x => x.Group == r.Group && x.Kind != RowKind.GroupHeader && x.Desc.Contains(find, StringComparison.OrdinalIgnoreCase))
            : r.Desc.Contains(find, StringComparison.OrdinalIgnoreCase));
    }

    private static string RareDisplay(int type, int stars, string name) => $"★{stars} {name} ({type})";
    private static int ParseRareType(string? display)
    {
        if (string.IsNullOrEmpty(display)) return 0;
        int open = display.LastIndexOf('('), close = display.LastIndexOf(')');
        if (open < 0 || close <= open) return 0;
        return int.TryParse(display.Substring(open + 1, close - open - 1), out var t) ? t : 0;
    }
    private bool _building;

    private void RebuildGrid()
    {
        _building = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var r in Filtered())
        {
            int i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Tag = r;
            if (r.Kind == RowKind.GroupHeader)
            {
                row.Cells["desc"].Value = r.Desc;
                row.DefaultCellStyle.BackColor = HeaderBg;
                row.DefaultCellStyle.ForeColor = HeaderFg;
                row.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
                row.Cells["active"].ReadOnly = true;
                ((DataGridViewCheckBoxCell)row.Cells["active"]).Value = null;
                ((DataGridViewCheckBoxCell)row.Cells["active"]).FlatStyle = FlatStyle.Flat;
                row.Cells["active"].Style.SelectionBackColor = HeaderBg;
            }
            else if (r.Kind == RowKind.RareSpawn)
            {
                row.Cells["active"].Value = r.Active;
                row.Cells["desc"].Value = r.Desc;
                row.Cells["type"].Value = TypeLabel(r);
                int sel = int.TryParse(r.InjectValue, out var t) ? t : 0;
                if (sel <= 0 && _rareSelType > 0) { sel = _rareSelType; r.InjectValue = sel.ToString(); } // survive re-attach
                row.Cells["value"].Value = sel > 0 ? RareName(sel) : "▾ click to pick";
                row.Cells["value"].Style.ForeColor = Color.FromArgb(40, 90, 200);
                row.Cells["value"].ReadOnly = true;
            }
            else
            {
                row.Cells["active"].Value = r.Active;
                row.Cells["desc"].Value = r.Desc;
                row.Cells["type"].Value = TypeLabel(r);
                row.Cells["value"].Value = r.Kind == RowKind.Action ? "▶ click On"
                    : r.Kind == RowKind.Value ? (r.FrozenText ?? "—")
                    : r.Kind is RowKind.DropMult or RowKind.PatchInt or RowKind.SpawnBoost ? r.InjectValue
                    : "—";
                row.Cells["value"].ReadOnly = r.Kind is not (RowKind.Value or RowKind.DropMult or RowKind.PatchInt or RowKind.SpawnBoost);
            }
        }
        _grid.ResumeLayout();
        _building = false;
    }

    private static string TypeLabel(CheatRow r) => r.Kind switch
    {
        RowKind.Buff => "buff",
        RowKind.Toggle => "bool",
        RowKind.Inject => "inject",
        RowKind.Fast => "fast",
        RowKind.UseHook => "hook",
        RowKind.Tools => "tools",
        RowKind.Craft => "craft",
        RowKind.Patch => "patch",
        RowKind.PatchSet => "patch*",
        RowKind.DropMult => "drop×",
        RowKind.PatchInt => "value",
        RowKind.Crate => "fishing",
        RowKind.ScopedDrop => "drop(me)",
        RowKind.SpawnBoost => "spawn",
        RowKind.RareSpawn => "rare▾",
        RowKind.BuffClear => "no-debuff",
        RowKind.InfAmmo => "ammo",
        RowKind.Vanity => "vanity",
        RowKind.Action => "",
        RowKind.Value => r.Field!.Kind switch
        {
            FieldKind.Single or FieldKind.Double => "float",
            FieldKind.Boolean => "bool",
            _ => "int",
        },
        _ => "",
    };

    // ---- interaction ----

    private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var grow = _grid.Rows[e.RowIndex];
        if (grow.Tag is not CheatRow r || r.Kind == RowKind.GroupHeader) return;
        if (r.Kind == RowKind.RareSpawn && _grid.Columns[e.ColumnIndex].Name == "value") { OpenRarePicker(r, grow); return; }
        if (_grid.Columns[e.ColumnIndex].Name != "active") return;
        if (!_engine.Attached) { AppendLog("Attach first."); if (grow != null) grow.Cells["active"].Value = false; return; }

        if (r.Kind == RowKind.Action) { DoAction(r); if (grow != null) grow.Cells["active"].Value = false; return; }

        r.Active = !r.Active;
        grow.Cells["active"].Value = r.Active;
        lock (_engine.Sync) ApplyActiveChange(r, grow); // serialise with the writer thread
        grow.Cells["active"].Value = r.Active; // reflect auto-disable on failure
        SaveConfig();
    }

    private void ApplyActiveChange(CheatRow r, DataGridViewRow? grow)
    {
        switch (r.Kind)
        {
            case RowKind.Value:
                // grow==null => restoring from config: keep r.FrozenText already loaded.
                if (grow != null) r.FrozenText = r.Active ? (Convert.ToString(grow.Cells["value"].Value) ?? "") : null;
                AppendLog($"{(r.Active ? "Froze" : "Unfroze")} {r.Desc}" + (r.Active ? $" = {r.FrozenText}" : ""));
                break;
            case RowKind.Toggle:
                AppendLog($"{(r.Active ? "Enabled" : "Disabled")} {r.Desc}");
                if (!r.Active) _engine.WriteField(r.Field!, "false");
                break;
            case RowKind.Buff:
                r.Buff!.Enabled = r.Active;
                if (!r.Active) _buffs.OnDisableBuff(_engine, r.Buff!);
                AppendLog($"{(r.Active ? "Enabled" : "Disabled")} {r.Desc}");
                break;
            case RowKind.Inject:
                if (r.Active)
                {
                    if (_engine.Injector!.Patch(r.Field!.Name)) { _engine.WriteField(r.Field!, r.InjectValue); AppendLog($"Injected (NOP reset) + enabled {r.Desc}"); }
                    else { r.Active = false; if (grow != null) grow.Cells["active"].Value = false; AppendLog($"Could not inject {r.Desc} (reset instruction not found)"); }
                }
                else
                {
                    _engine.Injector!.Restore(r.Field!.Name); // game's reset resumes, reverting the value
                    AppendLog($"Restored (un-injected) {r.Desc}");
                }
                break;
            case RowKind.Fast:
                // writer thread asserts r.InjectValue while active; the game restores the
                // value on its own when we stop writing.
                AppendLog($"{(r.Active ? "Enabled" : "Disabled")} {r.Desc}");
                break;
            case RowKind.Tools:
                if (r.Active)
                {
                    int ut = int.TryParse(r.InjectValue, out var t) ? t : 1;
                    AppendLog($"Fast tools on: {_engine.ApplyFastTools(ut, 4)} tool(s) sped up.");
                }
                else { _engine.RestoreFastTools(); AppendLog("Fast tools off (restored)."); }
                break;
            case RowKind.Craft:
                if (r.Active)
                {
                    int done = 0;
                    foreach (var k in CraftKeys) if (_engine.Injector!.PatchReturnTrue(k)) done++;
                    AppendLog($"Craft Anything ON ({done}/{CraftKeys.Length} checks bypassed)");
                }
                else
                {
                    foreach (var k in CraftKeys) _engine.Injector!.UnhookEntry(k);
                    AppendLog("Craft Anything OFF");
                }
                break;
            case RowKind.Vanity:
                if (r.Active)
                {
                    _engine.RefreshMethodAddress("UpdateEquips"); _engine.RefreshMethodAddress("ApplyEquipFunctional");
                    if (_engine.Injector!.HookVanityAccessories()) AppendLog($"ON: {r.Desc} (vanity/social accessory slots now functional)");
                    else { r.Active = false; if (grow != null) grow.Cells["active"].Value = false; AppendLog($"Failed: {r.Desc}"); }
                }
                else { _engine.Injector!.UnhookVanityAccessories(); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.DropMult:
                if (r.Active)
                {
                    int f = int.TryParse(r.InjectValue, out var fv) ? fv : 5;
                    _engine.RefreshMethodAddress("CommonCode.DropItem"); // follow tiered-JIT relocation
                    if (_engine.Injector!.HookDropMultiplier(f)) AppendLog($"ON: {r.Desc} (loot stacks x{f})");
                    else { r.Active = false; if (grow != null) grow.Cells["active"].Value = false; AppendLog($"Failed: {r.Desc}"); }
                }
                else { _engine.Injector!.UnhookDropMultiplier(); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.Crate:
                if (r.Active)
                {
                    _engine.RefreshMethodAddress("Projectile.FishingCheck_RollItemDrop"); // follow tiered-JIT relocation
                    if (_engine.Injector!.HookAlwaysCrate()) AppendLog($"ON: {r.Desc}");
                    else { r.Active = false; if (grow != null) grow.Cells["active"].Value = false; AppendLog($"Failed: {r.Desc}"); }
                }
                else { _engine.Injector!.UnhookAlwaysCrate(); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.ScopedDrop:
                if (r.Active) { _scopedDrop.Enable(); AppendLog($"ON: {r.Desc} (auto-detects MP server, scoped to your character)"); }
                else { _scopedDrop.Disable(); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.SpawnBoost:
                if (r.Active)
                {
                    _spawnBoost.SetValues(int.TryParse(r.InjectValue, out var sr) ? sr : 30, 40);
                    _spawnBoost.Enable();
                    AppendLog($"ON: {r.Desc} (spawnRate {r.InjectValue}, lower = more spawns; works on MP server)");
                }
                else { _spawnBoost.Disable(); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.RareSpawn:
                if (r.Active)
                {
                    int rt = int.TryParse(r.InjectValue, out var rv) ? rv : 0;
                    if (rt <= 0) rt = _rareSelType; // fallback to the last picked mob
                    if (rt <= 0) { r.Active = false; AppendLog("Pick a rare mob first (click the Value cell), then enable."); break; }
                    r.InjectValue = rt.ToString();
                    _rareSelType = rt;
                    _rareSpawn.SetType(rt);
                    _rareSpawn.Enable();
                    AppendLog($"ON: {r.Desc} → {RareName(rt)} (every natural spawn becomes this; works on MP server)");
                }
                else { _rareSpawn.Disable(); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.BuffClear:
                // the high-frequency writer removes the buff each tick while active.
                AppendLog($"{(r.Active ? "Enabled" : "Disabled")} {r.Desc}");
                break;
            case RowKind.InfAmmo:
                // the high-frequency writer restores any consumed ammo while active.
                if (!r.Active) _engine.ResetAmmoFreeze();
                AppendLog($"{(r.Active ? "Enabled" : "Disabled")} {r.Desc}");
                break;
            case RowKind.PatchInt:
                if (r.Active) { _sticky.Register(r.PatchMethod, RetInt(int.TryParse(r.InjectValue, out var pv) ? pv : 0)); AppendLog($"ON: {r.Desc} = {r.InjectValue}"); }
                else { _sticky.Unregister(r.PatchMethod); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.PatchSet:
                if (r.Active)
                {
                    foreach (var spec in r.PatchMethod.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pr = spec.Split(':'); var k = pr[0]; var ret = pr.Length > 1 ? pr[1] : "zero";
                        _sticky.Register(k, ret == "true" ? RetTrue : RetUZero);
                    }
                    AppendLog($"ON: {r.Desc} (auto-reasserted vs JIT)");
                }
                else
                {
                    foreach (var spec in r.PatchMethod.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        _sticky.Unregister(spec.Split(':')[0]);
                    AppendLog($"OFF: {r.Desc}");
                }
                break;
            case RowKind.Patch:
                if (r.Active)
                {
                    var stub = r.InjectValue.StartsWith("f")
                        ? RetFloat(float.Parse(r.InjectValue[1..], System.Globalization.CultureInfo.InvariantCulture))
                        : int.TryParse(r.InjectValue, out var iv) ? RetInt(iv) : RetTrue;
                    _sticky.Register(r.PatchMethod, stub);
                    AppendLog($"ON: {r.Desc} (auto-reasserted vs JIT)");
                }
                else { _sticky.Unregister(r.PatchMethod); AppendLog($"OFF: {r.Desc}"); }
                break;
            case RowKind.UseHook:
                if (r.Active)
                {
                    float v = float.Parse(r.InjectValue, System.Globalization.CultureInfo.InvariantCulture);
                    if (_engine.Injector!.HookUseSites(r.Field!.Name, v, r.Methods)) AppendLog($"Hooked use-site: {r.Desc}");
                    else { r.Active = false; if (grow != null) grow.Cells["active"].Value = false; AppendLog($"Use-site hook failed for {r.Desc}"); }
                }
                else
                {
                    _engine.Injector!.UnhookUseSites(r.Field!.Name);
                    AppendLog($"Unhooked {r.Desc}");
                }
                break;
        }
    }

    private void DoAction(CheatRow r)
    {
        if (r.Desc.StartsWith("Max Stack"))
            lock (_engine.Sync) AppendLog($"Max-stacked {_engine.MaxStackInventory()} item(s).");
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var grow = _grid.Rows[e.RowIndex];
        if (grow.Tag is not CheatRow r || r.Kind is not (RowKind.Value or RowKind.DropMult or RowKind.PatchInt or RowKind.SpawnBoost)) return;
        if (_grid.Columns[e.ColumnIndex].Name == "value")
        {
            _grid.BeginEdit(true);
        }
    }

    private void Grid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_building || e.RowIndex < 0) return;
        var grow = _grid.Rows[e.RowIndex];
        if (grow.Tag is not CheatRow r || r.Kind != RowKind.RareSpawn) return;
        if (_grid.Columns[e.ColumnIndex].Name != "value") return;
        int type = ParseRareType(Convert.ToString(grow.Cells["value"].Value));
        if (type <= 0) return; // ignore the placeholder/transient values
        _rareSelType = type;
        r.InjectValue = type.ToString();
        if (r.Active) _rareSpawn.SetType(type);
        SaveConfig();
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var grow = _grid.Rows[e.RowIndex];
        if (grow.Tag is not CheatRow r) return;
        if (_grid.Columns[e.ColumnIndex].Name != "value") return;
        var text = Convert.ToString(grow.Cells["value"].Value) ?? "";

        if (r.Kind == RowKind.DropMult)
        {
            if (!int.TryParse(text.Trim(), out var f) || f < 1) { f = 1; grow.Cells["value"].Value = "1"; }
            r.InjectValue = f.ToString();
            if (r.Active) lock (_engine.Sync) { _engine.Injector!.HookDropMultiplier(f); AppendLog($"Drop multiplier set to x{f}"); }
            SaveConfig();
            return;
        }
        if (r.Kind == RowKind.PatchInt)
        {
            if (!int.TryParse(text.Trim(), out var v) || v < 0) { v = 0; grow.Cells["value"].Value = "0"; }
            r.InjectValue = v.ToString();
            if (r.Active) { _sticky.Unregister(r.PatchMethod); _sticky.Register(r.PatchMethod, RetInt(v)); AppendLog($"{r.Desc} set to {v}"); }
            SaveConfig();
            return;
        }
        if (r.Kind == RowKind.SpawnBoost)
        {
            if (!int.TryParse(text.Trim(), out var sv) || sv < 1) { sv = 1; grow.Cells["value"].Value = "1"; }
            r.InjectValue = sv.ToString();
            if (r.Active) { _spawnBoost.SetValues(sv, 40); AppendLog($"Spawn rate set to {sv} (lower = more spawns)"); }
            SaveConfig();
            return;
        }

        if (r.Kind != RowKind.Value) return;
        lock (_engine.Sync)
        {
            if (_engine.WriteField(r.Field!, text)) AppendLog($"Set {r.Desc} = {text}");
            else AppendLog($"Failed to write {r.Desc}");
        }
        if (r.Active) { r.FrozenText = text; SaveConfig(); } // keep freezing the new value
    }

    private void DisableAll()
    {
        lock (_engine.Sync)
        foreach (var r in _rows)
        {
            if (r.Kind == RowKind.GroupHeader || !r.Active) continue;
            r.Active = false;
            if (r.Kind == RowKind.Buff) { r.Buff!.Enabled = false; _buffs.OnDisableBuff(_engine, r.Buff!); }
            else if (r.Kind == RowKind.Value) r.FrozenText = null;
            else if (r.Kind == RowKind.Inject) { _engine.Injector?.Restore(r.Field!.Name); }
            else if (r.Kind == RowKind.UseHook) { _engine.Injector?.UnhookUseSites(r.Field!.Name); }
            else if (r.Kind == RowKind.Tools) { _engine.RestoreFastTools(); }
            else if (r.Kind == RowKind.Craft) { foreach (var k in CraftKeys) _engine.Injector?.UnhookEntry(k); }
            else if (r.Kind is RowKind.Patch or RowKind.PatchInt) { _sticky.Unregister(r.PatchMethod); }
            else if (r.Kind == RowKind.Vanity) { _engine.Injector?.UnhookVanityAccessories(); }
            else if (r.Kind == RowKind.PatchSet) { foreach (var spec in r.PatchMethod.Split(';', StringSplitOptions.RemoveEmptyEntries)) _sticky.Unregister(spec.Split(':')[0]); }
            else if (r.Kind == RowKind.DropMult) { _engine.Injector?.UnhookDropMultiplier(); }
            else if (r.Kind == RowKind.Crate) { _engine.Injector?.UnhookAlwaysCrate(); }
            else if (r.Kind == RowKind.ScopedDrop) { _scopedDrop.Disable(); }
            else if (r.Kind == RowKind.SpawnBoost) { _spawnBoost.Disable(); }
            else if (r.Kind == RowKind.RareSpawn) { _rareSpawn.Disable(); }
        }
        foreach (DataGridViewRow gr in _grid.Rows)
            if (gr.Tag is CheatRow rr && rr.Kind != RowKind.GroupHeader)
                gr.Cells["active"].Value = false;
        SaveConfig();
        AppendLog("Disabled all active cheats.");
    }

    // ---- tick ----

    private void Tick()
    {
        if (!_engine.Attached)
        {
            if (_status.Text == "Attached")
            {
                _status.Text = "tModLoader closed"; _status.ForeColor = Color.Firebrick;
                _vitals.Text = ""; _btnRescan.Enabled = false;
            }
            // auto (re)attach every ~3.5s if enabled
            if (_autoAttach.Checked && ++_attachThrottle >= 10)
            {
                _attachThrottle = 0;
                if (TmlDiscovery.FindProcess() != null) DoAttach(silent: true);
            }
            return;
        }
        // Writes are handled by the high-frequency writer thread; here we only display.
        // Lock so we don't share the engine's scratch buffer with that thread.
        lock (_engine.Sync)
        {
            if (_engine.PlayerBase() == IntPtr.Zero) { _vitals.Text = "(load into a world)"; return; }
            _vitals.Text = VitalsText();
            _buffs.Tick(_engine); // buffs persist ~1h, slow tick is fine

            if (_scopedDrop.Enabled && _scopedDrop.Status != _lastScopedStatus)
            { _lastScopedStatus = _scopedDrop.Status; AppendLog($"[100% drop] {_lastScopedStatus}"); }

            if (_spawnBoost.Enabled && _spawnBoost.Status != _lastSpawnStatus)
            { _lastSpawnStatus = _spawnBoost.Status; AppendLog($"[spawn boost] {_lastSpawnStatus}"); }

            if (_rareSpawn.Enabled && _rareSpawn.Status != _lastRareStatus)
            { _lastRareStatus = _rareSpawn.Status; AppendLog($"[rare spawn] {_lastRareStatus}"); }

            // Fast-tools is asserted by the high-frequency writer (the held item is recomputed
            // every frame by Calamity, so a slow write loses). The slow tick re-snapshots
            // all tools so non-held ones are covered and disable can restore them.
            foreach (var r in _rows)
                if (r.Kind == RowKind.Tools && r.Active)
                    _engine.ApplyFastTools(int.TryParse(r.InjectValue, out var t) ? t : 1, 4);

            if (_tabs.SelectedIndex == 1) { RefreshInvGrid(); return; }
            if (_tabs.SelectedIndex != 0) return;

            var editing = _grid.IsCurrentCellInEditMode ? _grid.CurrentCell : null;
            foreach (DataGridViewRow gr in _grid.Rows)
            {
                if (gr.Tag is not CheatRow r || r.Kind is RowKind.GroupHeader or RowKind.Action) continue;
                var cell = gr.Cells["value"];
                if (r.Kind == RowKind.Value)
                {
                    if (r.Active && r.FrozenText != null)
                    {
                        if (!ReferenceEquals(cell, editing)) cell.Value = r.FrozenText;
                    }
                    else if (!ReferenceEquals(cell, editing)) cell.Value = _engine.ReadField(r.Field!);
                }
                else // Toggle / Inject / Fast / Buff
                {
                    cell.Value = r.Active ? "ON" : "off";
                }
            }
        }
    }

    private string RareName(int type)
    {
        foreach (var x in _rareNpcs) if (x.type == type) return $"{x.name} ({type})";
        return $"type {type}";
    }

    /// <summary>Searchable modal picker for the 600+ rare mobs (a grid combo-cell proved unreliable).</summary>
    private void OpenRarePicker(CheatRow r, DataGridViewRow grow)
    {
        if (_rareNpcs.Count == 0) { AppendLog("Attach to a world first to load the rare-mob list."); return; }
        using var dlg = new Form
        {
            Text = "Pick a rare mob (sorted by rarity ★)", Width = 440, Height = 540,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Font = Font,
        };
        var search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "type to filter by name or id…" };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        var ok = new Button { Text = "Select", Dock = DockStyle.Bottom, Height = 34 };
        void Fill(string f)
        {
            list.BeginUpdate(); list.Items.Clear();
            foreach (var x in _rareNpcs)
                if (f.Length == 0 || x.name.Contains(f, StringComparison.OrdinalIgnoreCase) || x.type.ToString() == f)
                    list.Items.Add(RareDisplay(x.type, x.stars, x.name));
            list.EndUpdate();
        }
        Fill("");
        int cur = int.TryParse(r.InjectValue, out var ct) ? ct : 0;
        if (cur > 0) for (int i = 0; i < list.Items.Count; i++) if (ParseRareType(list.Items[i].ToString()) == cur) { list.SelectedIndex = i; break; }
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        list.DoubleClick += (_, _) => { if (list.SelectedItem != null) ok.PerformClick(); };
        ok.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        dlg.Controls.Add(list); dlg.Controls.Add(search); dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        if (dlg.ShowDialog(this) == DialogResult.OK && list.SelectedItem is { } sel)
        {
            int type = ParseRareType(sel.ToString());
            if (type > 0)
            {
                _rareSelType = type;
                r.InjectValue = type.ToString();
                grow.Cells["value"].Value = RareName(type);
                _rareSpawn.SetType(type);
                AppendLog($"Rare spawn target: {RareName(type)}" + (r.Active ? " (live)" : " — tick On to start"));
                SaveConfig();
            }
        }
    }

    private string VitalsText()
    {
        string hp = _fLife != null && _fLifeMax != null ? $"HP {_engine.ReadField(_fLife)}/{_engine.ReadField(_fLifeMax)}" : "";
        string mp = _fMana != null && _fManaMax != null ? $"MP {_engine.ReadField(_fMana)}/{_engine.ReadField(_fManaMax)}" : "";
        string biome = "";
        try { var b = _engine.CurrentBiomeText(); if (!string.IsNullOrEmpty(b)) biome = $"   🌍 {b}"; } catch { }
        return $"{hp}   {mp}{biome}";
    }

    private void AppendLog(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(msg)); return; }
        _log.AppendText(msg + Environment.NewLine);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _writerRun = false;
        _writer?.Join(200);
        try { _scopedDrop.Disable(); } catch { }
        try { _spawnBoost.Disable(); } catch { } // restore vanilla spawn defaults on the target
        try { _rareSpawn.Disable(); } catch { }  // remove the NewNPC cave on the target
        lock (_engine.Sync) _engine.Dispose(); // restores any injected patches
        base.OnFormClosed(e);
    }
}
