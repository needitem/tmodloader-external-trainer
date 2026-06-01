using System.ComponentModel;
using TerrariaTrainer.Cheats;
using TerrariaTrainer.Core;

namespace TerrariaTrainer.UI;

public sealed class MainForm : Form
{
    private readonly TrainerEngine _engine = new();
    private readonly CheatManager _cheats = new();
    private readonly List<ValueEntry> _entries = ValueEntry.LoadAll();

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _buffGrid = new();
    private readonly TextBox _buffSearch = new() { Width = 200, PlaceholderText = "search buffs..." };

    private readonly Button _btnAttach = new() { Text = "1. Attach", Width = 110 };
    private readonly Button _btnHook = new() { Text = "2. Install Hook", Width = 130, Enabled = false };
    private readonly Label _status = new() { Text = "Not attached", AutoSize = true, ForeColor = Color.Firebrick };
    private readonly ComboBox _groupFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly TextBox _search = new() { Width = 180, PlaceholderText = "search..." };
    private readonly DataGridView _grid = new();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };

    // Per-entry frozen value (string as typed); null = not frozen.
    private readonly Dictionary<string, string?> _frozen = new();

    public MainForm()
    {
        Text = "Terraria Trainer (external)";
        Width = 920;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        _engine.Log += AppendLog;
        _btnAttach.Click += (_, _) => DoAttach();
        _btnHook.Click += (_, _) => DoHook();
        _groupFilter.SelectedIndexChanged += (_, _) => RebuildRows();
        _search.TextChanged += (_, _) => RebuildRows();
        _buffSearch.TextChanged += (_, _) => RebuildBuffRows();
        _timer.Tick += (_, _) => Tick();

        BuildLayout();
        BuildGrid();
        BuildBuffGrid();
        PopulateGroups();
        RebuildRows();
        RebuildBuffRows();
        _timer.Start();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 6, 0) };
        top.Controls.Add(_btnAttach);
        top.Controls.Add(_btnHook);
        top.Controls.Add(new Label { Text = "Status:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        top.Controls.Add(_status);

        // ---- Values tab ----
        var valuesTab = new TabPage("Values (158)");
        var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 4, 6, 0) };
        filterBar.Controls.Add(new Label { Text = "Group:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        filterBar.Controls.Add(_groupFilter);
        filterBar.Controls.Add(new Label { Text = "Find:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        filterBar.Controls.Add(_search);
        _grid.Dock = DockStyle.Fill;
        valuesTab.Controls.Add(_grid);
        valuesTab.Controls.Add(filterBar);

        // ---- Buffs tab ----
        var buffsTab = new TabPage($"Buffs ({_cheats.Buffs.Count})");
        var buffBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 4, 6, 0) };
        buffBar.Controls.Add(new Label { Text = "Find:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        buffBar.Controls.Add(_buffSearch);
        var btnMaxStack = new Button { Text = "Max Stack Inventory", Width = 160 };
        btnMaxStack.Click += (_, _) => DoMaxStack();
        buffBar.Controls.Add(btnMaxStack);
        var btnDisableAll = new Button { Text = "Disable All Buffs", Width = 130 };
        btnDisableAll.Click += (_, _) => { _cheats.DisableAll(_engine); RebuildBuffRows(); };
        buffBar.Controls.Add(btnDisableAll);
        _buffGrid.Dock = DockStyle.Fill;
        buffsTab.Controls.Add(_buffGrid);
        buffsTab.Controls.Add(buffBar);

        _tabs.TabPages.Add(valuesTab);
        _tabs.TabPages.Add(buffsTab);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_tabs);
        var logHeader = new Label { Text = "Log", Dock = DockStyle.Top, Height = 18, Font = new Font(Font, FontStyle.Bold) };
        split.Panel2.Controls.Add(_log);
        split.Panel2.Controls.Add(logHeader);
        logHeader.BringToFront();

        Controls.Add(split);
        Controls.Add(top);
        split.SplitterDistance = 520;
    }

    private void BuildBuffGrid()
    {
        _buffGrid.AutoGenerateColumns = false;
        _buffGrid.AllowUserToAddRows = false;
        _buffGrid.AllowUserToDeleteRows = false;
        _buffGrid.RowHeadersVisible = false;
        _buffGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;

        _buffGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Name = "on", Width = 40 });
        _buffGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cheat", Name = "name", ReadOnly = true, Width = 360 });
        _buffGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Buff", Name = "buff", ReadOnly = true, Width = 50 });
        _buffGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mode", Name = "mode", ReadOnly = true, Width = 70 });
        _buffGrid.CellClick += BuffGrid_CellClick;
    }

    private void RebuildBuffRows()
    {
        string find = _buffSearch.Text.Trim();
        _buffGrid.Rows.Clear();
        foreach (var b in _cheats.Buffs)
        {
            if (find.Length > 0 && !b.Name.Contains(find, StringComparison.OrdinalIgnoreCase)) continue;
            int idx = _buffGrid.Rows.Add(b.Enabled, b.Name, b.Mode == "maxstack" ? "-" : b.Buff.ToString(), b.Mode);
            _buffGrid.Rows[idx].Tag = b;
        }
    }

    private void BuffGrid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = _buffGrid.Rows[e.RowIndex];
        if (row.Tag is not BuffCheat b) return;
        if (_buffGrid.Columns[e.ColumnIndex].Name != "on") return;

        if (b.Mode == "maxstack") { DoMaxStack(); row.Cells["on"].Value = false; return; }
        if (!_engine.HookInstalled) { AppendLog("Install the hook first."); return; }

        _buffGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        bool on = !b.Enabled;
        b.Enabled = on;
        row.Cells["on"].Value = on;
        if (!on) b.OnDisable(_engine);
        AppendLog($"{(on ? "Enabled" : "Disabled")}: {b.Name}");
    }

    private void DoMaxStack()
    {
        if (!_engine.HookInstalled) { AppendLog("Install the hook first."); return; }
        int n = BuffCheat.MaxStackInventory(_engine);
        AppendLog($"Max-stacked {n} inventory item(s).");
    }

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "name", ReadOnly = true, Width = 300 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", Name = "type", ReadOnly = true, Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "value", Width = 160 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Freeze", Name = "freeze", Width = 55 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Set", Name = "set", Text = "Set", UseColumnTextForButtonValue = true, Width = 50 });

        _grid.CellEndEdit += Grid_CellEndEdit;
        _grid.CellClick += Grid_CellClick;
    }

    private void PopulateGroups()
    {
        _groupFilter.Items.Add("(All)");
        foreach (var g in _entries.Where(e => e.Supported).Select(e => e.Group).Distinct())
            _groupFilter.Items.Add(string.IsNullOrEmpty(g) ? "(ungrouped)" : g);
        _groupFilter.SelectedIndex = 0;
    }

    private IEnumerable<ValueEntry> FilteredEntries()
    {
        string group = _groupFilter.SelectedItem?.ToString() ?? "(All)";
        string find = _search.Text.Trim();
        return _entries.Where(e => e.Supported)
            .Where(e => group == "(All)"
                        || (group == "(ungrouped)" && string.IsNullOrEmpty(e.Group))
                        || e.Group == group)
            .Where(e => find.Length == 0 || e.Desc.Contains(find, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildRows()
    {
        _grid.Rows.Clear();
        foreach (var e in FilteredEntries())
        {
            int idx = _grid.Rows.Add(e.Desc, ShortType(e.Type), "—", _frozen.ContainsKey(e.Id) && _frozen[e.Id] != null, "Set");
            _grid.Rows[idx].Tag = e;
        }
    }

    private static string ShortType(string t) => t switch
    {
        "4 Bytes" => "i32", "2 Bytes" => "i16", "Byte" => "u8",
        "Float" => "f32", "Double" => "f64", "String" => "str", _ => t
    };

    private void DoAttach()
    {
        try
        {
            _engine.Attach();
            _status.Text = $"Attached (PID {_engine.Mem!.Pid})";
            _status.ForeColor = Color.DarkGoldenrod;
            _btnHook.Enabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("ATTACH ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Attach failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoHook()
    {
        try
        {
            _btnHook.Enabled = false;
            AppendLog("Installing get_LocalPlayer hook (scanning)...");
            _engine.InstallHook();
            _status.Text = "Hooked — player captured";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception ex)
        {
            AppendLog("HOOK ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Hook failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _btnHook.Enabled = true;
        }
    }

    private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not ValueEntry entry) return;

        if (_grid.Columns[e.ColumnIndex].Name == "set")
        {
            CommitValue(entry, row);
        }
        else if (_grid.Columns[e.ColumnIndex].Name == "freeze")
        {
            // toggle handled after commit; update frozen dict
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            bool frozen = Convert.ToBoolean(row.Cells["freeze"].Value);
            _frozen[entry.Id] = frozen ? Convert.ToString(row.Cells["value"].Value) : null;
        }
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not ValueEntry entry) return;
        if (_grid.Columns[e.ColumnIndex].Name == "value")
        {
            CommitValue(entry, row);
            if (_frozen.TryGetValue(entry.Id, out var f) && f != null)
                _frozen[entry.Id] = Convert.ToString(row.Cells["value"].Value);
        }
    }

    private void CommitValue(ValueEntry entry, DataGridViewRow row)
    {
        if (!_engine.HookInstalled) { AppendLog("Install the hook first."); return; }
        var text = Convert.ToString(row.Cells["value"].Value) ?? "";
        if (_engine.WriteValue(entry, text))
            AppendLog($"Set {entry.Desc} = {text}");
        else
            AppendLog($"Failed to write {entry.Desc} (= '{text}')");
    }

    private void Tick()
    {
        if (!_engine.Attached)
        {
            if (_status.Text.StartsWith("Attached") || _status.Text.StartsWith("Hooked"))
            {
                _status.Text = "Terraria closed";
                _status.ForeColor = Color.Firebrick;
            }
            return;
        }
        if (!_engine.HookInstalled) return;

        // Assert all enabled buff/toggle cheats.
        _cheats.Tick(_engine);

        // Only refresh the value grid when its tab is visible (avoid wasted reads).
        if (_tabs.SelectedIndex != 0) return;

        var editingCell = _grid.IsCurrentCellInEditMode ? _grid.CurrentCell : null;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not ValueEntry entry) continue;

            // Freeze: keep writing the stored value.
            if (_frozen.TryGetValue(entry.Id, out var frozenVal) && frozenVal != null)
            {
                _engine.WriteValue(entry, frozenVal);
                if (!ReferenceEquals(row.Cells["value"], editingCell))
                    row.Cells["value"].Value = frozenVal;
                continue;
            }

            // Don't clobber the cell the user is editing.
            if (editingCell != null && ReferenceEquals(row.Cells["value"], editingCell)) continue;
            row.Cells["value"].Value = _engine.ReadDisplay(entry);
        }
    }

    private void AppendLog(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(msg)); return; }
        _log.AppendText(msg + Environment.NewLine);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _engine.Dispose();
        base.OnFormClosed(e);
    }
}
