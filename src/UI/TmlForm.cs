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

    private int _attachThrottle = 10; // attempt auto-attach on the first tick
    private TmlField? _fLife, _fLifeMax, _fMana, _fManaMax;

    // High-frequency writer: per-frame-recomputed values (move/mine speed) must be written
    // far faster than the 350ms UI tick to actually hold. ~2ms with 1ms timer resolution.
    private Thread? _writer;
    private volatile bool _writerRun = true;

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
    }

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
            CacheVitalFields();
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

    private void RebuildGrid()
    {
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
            else
            {
                row.Cells["active"].Value = r.Active;
                row.Cells["desc"].Value = r.Desc;
                row.Cells["type"].Value = TypeLabel(r);
                row.Cells["value"].Value = r.Kind == RowKind.Action ? "▶ click On" : "—";
                row.Cells["value"].ReadOnly = r.Kind != RowKind.Value;
            }
        }
        _grid.ResumeLayout();
    }

    private static string TypeLabel(CheatRow r) => r.Kind switch
    {
        RowKind.Buff => "buff",
        RowKind.Toggle => "bool",
        RowKind.Inject => "inject",
        RowKind.Fast => "fast",
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
        if (_grid.Columns[e.ColumnIndex].Name != "active") return;
        if (!_engine.Attached) { AppendLog("Attach first."); grow.Cells["active"].Value = false; return; }

        if (r.Kind == RowKind.Action) { DoAction(r); grow.Cells["active"].Value = false; return; }

        r.Active = !r.Active;
        grow.Cells["active"].Value = r.Active;
        lock (_engine.Sync) ApplyActiveChange(r, grow); // serialise with the writer thread
    }

    private void ApplyActiveChange(CheatRow r, DataGridViewRow grow)
    {
        switch (r.Kind)
        {
            case RowKind.Value:
                r.FrozenText = r.Active ? (Convert.ToString(grow.Cells["value"].Value) ?? "") : null;
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
                    else { r.Active = false; grow.Cells["active"].Value = false; AppendLog($"Could not inject {r.Desc} (reset instruction not found)"); }
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
        if (grow.Tag is not CheatRow r || r.Kind != RowKind.Value) return;
        if (_grid.Columns[e.ColumnIndex].Name == "value")
        {
            _grid.BeginEdit(true);
        }
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var grow = _grid.Rows[e.RowIndex];
        if (grow.Tag is not CheatRow r || r.Kind != RowKind.Value) return;
        if (_grid.Columns[e.ColumnIndex].Name != "value") return;
        var text = Convert.ToString(grow.Cells["value"].Value) ?? "";
        lock (_engine.Sync)
        {
            if (_engine.WriteField(r.Field!, text)) AppendLog($"Set {r.Desc} = {text}");
            else AppendLog($"Failed to write {r.Desc}");
        }
        if (r.Active) r.FrozenText = text; // keep freezing the new value
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
        }
        foreach (DataGridViewRow gr in _grid.Rows)
            if (gr.Tag is CheatRow rr && rr.Kind != RowKind.GroupHeader)
                gr.Cells["active"].Value = false;
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

    private string VitalsText()
    {
        string hp = _fLife != null && _fLifeMax != null ? $"HP {_engine.ReadField(_fLife)}/{_engine.ReadField(_fLifeMax)}" : "";
        string mp = _fMana != null && _fManaMax != null ? $"MP {_engine.ReadField(_fMana)}/{_engine.ReadField(_fManaMax)}" : "";
        return $"{hp}   {mp}";
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
        lock (_engine.Sync) _engine.Dispose(); // restores any injected patches
        base.OnFormClosed(e);
    }
}
