using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IphoneMover.Core;
using IphoneMover.Wpd;

namespace IphoneMover;

internal sealed class MainForm : Form
{
    // top
    private readonly ComboBox deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button findDevicesButton = new() { Text = "Find devices", AutoSize = true };
    private readonly Button loadFilesButton = new() { Text = "Reload files from phone", AutoSize = true };
    private readonly TrackBar delayBar = new()
    {
        Minimum = 0, Maximum = 500, TickFrequency = 50, SmallChange = 10, LargeChange = 50,
        Width = 220, AutoSize = false, Height = 30, TickStyle = TickStyle.BottomRight,
    };
    private readonly Label delayLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0), MinimumSize = new Size(150, 0) };

    // left: phone
    private readonly RadioButton folderViewRadio = new() { Text = "Folders", AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly RadioButton fileViewRadio = new() { Text = "Files", AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    private readonly Panel fileFilterPanel = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    private readonly ComboBox folderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, MaxDropDownItems = 30 };
    private readonly ListView folderList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HideSelection = false, GridLines = true,
    };
    private readonly ListView phoneList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HideSelection = false, GridLines = true,
    };
    private readonly Label phoneCountLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    // right: PC
    private readonly ComboBox driveCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox pathBox = new() { Dock = DockStyle.Fill };
    private readonly ListView pcList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, GridLines = true,
        MultiSelect = true,
    };
    private readonly ContextMenuStrip pcMenu = new();

    // bottom
    private readonly CheckBox deleteCheck = new()
    {
        Text = "Delete from iPhone after verified copy (MOVE)", Checked = true, AutoSize = true,
        Padding = new Padding(0, 4, 0, 0),
    };
    private readonly Button moveButton = new() { Text = "Move checked  →", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Button cancelButton = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly ProgressBar progressBar = new() { Width = 260, Height = 22, Maximum = 1000 };
    private readonly Label statusLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox logBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };
    private readonly System.Windows.Forms.Timer progressTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer countTimer = new() { Interval = 150 };

    // phone data
    private WpdDevice? device;
    private List<DeviceFile> phoneFiles = [];
    private readonly HashSet<string> movedIds = [];                                // deleted from the phone during this session
    private readonly Dictionary<string, FolderStat> folderStats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListViewItem> folderItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkedFolders = new(StringComparer.Ordinal);  // folder view
    private readonly Dictionary<string, ListViewItem> phoneItems = [];               // file rows shown now
    private readonly HashSet<string> checkedIds = [];                                // file view, in all folders
    private readonly Dictionary<string, (string Text, Color Color)> statusById = [];
    private bool countDirty;
    private bool filling;          // ignore ItemChecked / SelectedIndexChanged while the code fills lists
    private bool busy;             // a move or a reload runs: the user cannot check or uncheck rows
    private Button[] checkButtons = [];
    private int sortColumn = 1;
    private bool sortAscending = true;

    // move progress
    private CancellationTokenSource? cts;
    private long progressBytes, progressTotal;
    private int filesDone, filesTotal;
    private DateTime progressStart;

    private bool FolderView => folderViewRadio.Checked;

    public MainForm()
    {
        Text = "iPhone Mover — move photos and videos from iPhone to PC";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont!;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        BuildLayout();
        BuildPcMenu();
        WireEvents();
        LoadDrives();
        NavigateTo(StartFolder(), showErrors: false);

        filling = true;
        folderViewRadio.Checked = Settings.Current.FolderView;
        fileViewRadio.Checked = !Settings.Current.FolderView;
        filling = false;
        ApplyViewMode();
        SetBusy(false);
    }

    // ================================================================== layout

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        top.Controls.AddRange([new Label { Text = "iPhone:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            deviceCombo, findDevicesButton, loadFilesButton,
            new Label { Text = "Delay after each phone action:", AutoSize = true, Padding = new Padding(24, 6, 0, 0) },
            delayBar, delayLabel]);

        folderList.Columns.Add("Folder", 140);
        folderList.Columns.Add("Files", 70, HorizontalAlignment.Right);
        folderList.Columns.Add("Size", 90, HorizontalAlignment.Right);
        folderList.Columns.Add("From", 90);
        folderList.Columns.Add("To", 90);
        folderList.Columns.Add("Status", 260);

        phoneList.Columns.Add("Name", 170);
        phoneList.Columns.Add("Folder", 110);
        phoneList.Columns.Add("Size", 90, HorizontalAlignment.Right);
        phoneList.Columns.Add("Date", 130);
        phoneList.Columns.Add("Status", 330);

        // View: [Folders] [Files]  (files view: folder filter with ◀ ▶)
        var prevFolder = new Button { Text = "◀", Width = 32, Height = folderCombo.Height + 2 };
        var nextFolder = new Button { Text = "▶", Width = 32, Height = folderCombo.Height + 2 };
        prevFolder.Click += (_, _) => { if (folderCombo.SelectedIndex > 0) folderCombo.SelectedIndex--; };
        nextFolder.Click += (_, _) => { if (folderCombo.SelectedIndex < folderCombo.Items.Count - 1) folderCombo.SelectedIndex++; };
        var filterFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        filterFlow.Controls.AddRange([new Label { Text = "Folder:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) },
            folderCombo, prevFolder, nextFolder]);
        fileFilterPanel.Controls.Add(filterFlow);

        var viewRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        viewRow.Controls.AddRange([new Label { Text = "Show:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            folderViewRadio, fileViewRadio, fileFilterPanel]);

        var phoneTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        var selectAll = new Button { Text = "Check all shown", AutoSize = true };
        var selectNone = new Button { Text = "Uncheck all", AutoSize = true };
        var checkSelected = new Button { Text = "Check selected rows", AutoSize = true };
        selectAll.Click += (_, _) => SetShownChecked(true);
        selectNone.Click += (_, _) => UncheckAll();
        checkSelected.Click += (_, _) => CheckSelectedRows();
        phoneTools.Controls.AddRange([selectAll, selectNone, checkSelected, phoneCountLabel]);
        checkButtons = [selectAll, selectNone, checkSelected];

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(folderList);
        left.Controls.Add(phoneList);
        left.Controls.Add(phoneTools);
        left.Controls.Add(viewRow);
        left.Controls.Add(Header("On the iPhone"));

        pcList.Columns.Add("Name", 260);
        pcList.Columns.Add("Size", 90, HorizontalAlignment.Right);
        pcList.Columns.Add("Modified", 130);
        pcList.ContextMenuStrip = pcMenu;

        var upButton = new Button { Text = "Up", AutoSize = true };
        var newFolderButton = new Button { Text = "New folder", AutoSize = true };
        var refreshButton = new Button { Text = "Refresh", AutoSize = true };
        upButton.Click += (_, _) => NavigateUp();
        newFolderButton.Click += (_, _) => CreateFolder();
        refreshButton.Click += (_, _) => { LoadDrives(); NavigateTo(pathBox.Text); };
        var pcTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        pcTools.Controls.AddRange([driveCombo, upButton, newFolderButton, refreshButton]);

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.Controls.Add(new Label { Text = "Destination:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        pathRow.Controls.Add(pathBox, 1, 0);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(pcList);
        right.Controls.Add(pathRow);
        right.Controls.Add(pcTools);
        right.Controls.Add(Header("On this PC (each phone folder goes into the Destination folder)"));

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
        actions.Controls.AddRange([deleteCheck, moveButton, cancelButton, progressBar, statusLabel]);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 180 };
        bottom.Controls.Add(logBox);
        bottom.Controls.Add(actions);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);

        Shown += (_, _) => split.SplitterDistance = (int)(split.Width * 0.58);
    }

    private static Label Header(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold),
    };

    private void WireEvents()
    {
        findDevicesButton.Click += async (_, _) => await FindDevicesAsync(autoLoad: false);
        loadFilesButton.Click += async (_, _) => await LoadPhoneFilesAsync();
        moveButton.Click += async (_, _) => await MoveAsync();
        cancelButton.Click += (_, _) => cts?.Cancel();
        deviceCombo.SelectionChangeCommitted += (_, _) => RememberDevice();

        // Delay slider: while the mouse button is down only the label changes;
        // the new delay is used when the button is released. Keyboard and mouse wheel apply at once.
        delayBar.Value = Math.Clamp(Settings.Current.ActionDelayMs, delayBar.Minimum, delayBar.Maximum);
        ApplyDelay(log: false);
        delayBar.ValueChanged += (_, _) =>
        {
            if (MouseButtons == MouseButtons.None)
                ApplyDelay(log: true);
            else
                delayLabel.Text = $"{delayBar.Value} ms (release to apply)";
        };
        delayBar.MouseUp += (_, _) => ApplyDelay(log: true);

        folderViewRadio.CheckedChanged += (_, _) => { if (!filling) ApplyViewMode(); };
        folderList.ItemChecked += OnFolderChecked;
        // While busy, a click on a check box is undone (changes made by the code itself are allowed).
        folderList.ItemCheck += BlockUserCheckWhileBusy;
        phoneList.ItemCheck += BlockUserCheckWhileBusy;
        folderList.DoubleClick += (_, _) => OpenFolderInFileView();
        phoneList.ItemChecked += OnFileChecked;
        phoneList.ColumnClick += (_, e) => SortPhoneList(e.Column);
        folderCombo.SelectedIndexChanged += (_, _) =>
        {
            if (filling) return;
            Settings.Current.PhoneFolder = (folderCombo.SelectedItem as FolderFilter)?.Folder;
            FillFileList();
        };
        countTimer.Tick += (_, _) => { if (countDirty) UpdateCount(); };
        countTimer.Start();

        driveCombo.SelectionChangeCommitted += (_, _) => { if (driveCombo.SelectedItem is DriveItem d) NavigateTo(d.Root); };
        pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { NavigateTo(pathBox.Text); e.SuppressKeyPress = true; } };
        pcList.DoubleClick += (_, _) => OpenPcSelection();
        pcList.KeyDown += OnPcKeyDown;

        progressTimer.Tick += (_, _) => UpdateProgress();
        Load += async (_, _) => await FindDevicesAsync(autoLoad: true);
        FormClosing += OnFormClosing;
    }

    // ================================================================== devices

    private async Task FindDevicesAsync(bool autoLoad)
    {
        SetBusy(true, "Looking for devices...");
        DeviceInfo? pick = null;
        bool known = false;
        try
        {
            var devices = await Task.Run(WpdDevice.ListDevices);
            deviceCombo.Items.Clear();
            foreach (var d in devices.OrderByDescending(d => d.LooksLikeApple))
                deviceCombo.Items.Add(d);

            // Remembered device first (by id, then by name), else the first Apple device, else the first one.
            var s = Settings.Current;
            pick = devices.FirstOrDefault(d => d.Id == s.DeviceId)
                   ?? devices.FirstOrDefault(d => s.DeviceName is not null && d.FriendlyName == s.DeviceName);
            known = pick is not null;
            pick ??= devices.FirstOrDefault(d => d.LooksLikeApple) ?? devices.FirstOrDefault();
            if (pick is not null)
                deviceCombo.SelectedItem = pick;

            Log(devices.Count == 0
                ? "No portable device found. Connect the iPhone with USB, unlock it and tap \"Trust\". Then click \"Find devices\"."
                : $"Found {devices.Count} device(s): " + string.Join(", ", devices));
        }
        catch (Exception ex)
        {
            Log("Device search failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (autoLoad && pick is not null && (known || pick.LooksLikeApple))
            await LoadPhoneFilesAsync();
    }

    private void ApplyDelay(bool log)
    {
        int ms = delayBar.Value;
        delayLabel.Text = $"{ms} ms";
        if (WpdDevice.ActionDelayMs == ms && !log)
            return;
        bool changed = WpdDevice.ActionDelayMs != ms;
        WpdDevice.ActionDelayMs = ms;
        Settings.Current.ActionDelayMs = ms;
        if (log && changed)
            Log($"Delay after each phone action is now {ms} ms.");
    }

    private void RememberDevice()
    {
        if (deviceCombo.SelectedItem is DeviceInfo d)
        {
            Settings.Current.DeviceId = d.Id;
            Settings.Current.DeviceName = d.FriendlyName;
        }
    }

    private async Task LoadPhoneFilesAsync()
    {
        if (deviceCombo.SelectedItem is not DeviceInfo info)
        {
            MessageBox.Show(this, "Select a device first. Click \"Find devices\" if the list is empty.", Text);
            return;
        }

        cts = new CancellationTokenSource();
        SetBusy(true, "Reading the file list from the phone...");
        // Forget the progress of an earlier move.
        progressBar.Value = 0;
        filesDone = filesTotal = 0;
        progressBytes = progressTotal = 0;
        try
        {
            // Always a new session: an old one may be broken (phone was locked, cable moved).
            var old = device;
            device = null;
            if (old is not null)
                await Task.Run(old.Dispose);
            device = await Task.Run(() => WpdDevice.Open(info));
            RememberDevice();

            var progress = new Progress<string>(text => statusLabel.Text = "Reading " + text +
                " (after a lost connection this can take a few minutes; Stop is possible)");
            var dev = device;
            var token = cts.Token;
            var sw = Stopwatch.StartNew();
            phoneFiles = await Task.Run(() => dev.ListFiles(progress, token));
            movedIds.Clear();
            checkedIds.Clear();
            checkedFolders.Clear();
            statusById.Clear();
            RebuildPhoneViews(restoreFilter: Settings.Current.PhoneFolder);
            Log($"Phone has {phoneFiles.Count:N0} files, {FormatSize(phoneFiles.Sum(f => Math.Max(0, f.Size)))}, " +
                $"in {folderStats.Count} folders (read in {sw.Elapsed.TotalSeconds:0.0} s).");
            if (phoneFiles.Count == 0)
                Log("No files. Unlock the iPhone, tap \"Trust\", then click \"Reload files from phone\".");
        }
        catch (OperationCanceledException)
        {
            Log("Reading the file list was stopped.");
        }
        catch (Exception ex)
        {
            Log("Could not read the phone: " + ex.Message);
            Log("Unlock the iPhone. If that does not help, unplug the cable, plug it in again, tap \"Trust\", " +
                "then click \"Find devices\".");
            device?.Dispose();
            device = null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ================================================================== phone views

    private sealed class FolderStat(string folder)
    {
        public string Folder { get; } = folder;
        public int Count;
        public long Bytes;
        public DateTime? First, Last;
        public int Failed;
    }

    private sealed record FolderFilter(string? Folder, int Count, long Bytes)
    {
        public override string ToString() => $"{Folder ?? "All folders"}   ({Count:N0} files, {FormatSize(Bytes)})";
    }

    /// <summary>Removes moved files, then rebuilds folder stats, the folder list, the folder filter and the file list.</summary>
    private void RebuildPhoneViews(string? restoreFilter)
    {
        if (movedIds.Count > 0)
        {
            phoneFiles.RemoveAll(f => movedIds.Contains(f.ObjectId));
            movedIds.Clear();
        }

        folderStats.Clear();
        foreach (var f in phoneFiles)
        {
            if (!folderStats.TryGetValue(f.Folder, out var st))
                folderStats[f.Folder] = st = new FolderStat(f.Folder);
            st.Count++;
            st.Bytes += Math.Max(0, f.Size);
            var d = f.Created ?? f.Modified;
            if (d is not null)
            {
                if (st.First is null || d < st.First) st.First = d;
                if (st.Last is null || d > st.Last) st.Last = d;
            }
        }
        // Folders without files are not shown (they come only from files).
        checkedFolders.IntersectWith(folderStats.Keys);

        FillFolderList();
        FillFolderFilter(restoreFilter);
        FillFileList();
    }

    private void FillFolderList()
    {
        var items = folderStats.Values
            .OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var item = new ListViewItem([
                    FolderLabel(s.Folder), "", "",
                    s.First?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    s.Last?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    "",
                ]) { Tag = s.Folder, Checked = checkedFolders.Contains(s.Folder) };
                SetFolderRowCounts(item, s);
                return item;
            })
            .ToArray();

        folderItems.Clear();
        foreach (var i in items)
            folderItems[(string)i.Tag!] = i;

        filling = true;
        folderList.BeginUpdate();
        try
        {
            folderList.Items.Clear();
            folderList.Items.AddRange(items);
        }
        finally
        {
            folderList.EndUpdate();
            filling = false;
        }
        countDirty = true;
    }

    private static string FolderLabel(string folder)
    {
        string last = folder.Split('\\').LastOrDefault() ?? folder;
        return last.Length == 0 ? "(root)" : last;
    }

    private static void SetFolderRowCounts(ListViewItem item, FolderStat s)
    {
        item.SubItems[1].Text = s.Count.ToString("N0", CultureInfo.CurrentCulture);
        item.SubItems[2].Text = FormatSize(s.Bytes);
    }

    private void FillFolderFilter(string? restore)
    {
        filling = true;
        try
        {
            folderCombo.BeginUpdate();
            folderCombo.Items.Clear();
            folderCombo.Items.Add(new FolderFilter(null, phoneFiles.Count, folderStats.Values.Sum(s => s.Bytes)));
            foreach (var s in folderStats.Values.OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase))
                folderCombo.Items.Add(new FolderFilter(s.Folder, s.Count, s.Bytes));
            folderCombo.EndUpdate();

            int index = 0;
            if (restore is not null)
                for (int i = 1; i < folderCombo.Items.Count; i++)
                    if (((FolderFilter)folderCombo.Items[i]!).Folder == restore)
                        index = i;
            folderCombo.SelectedIndex = index;
        }
        finally
        {
            filling = false;
        }
    }

    /// <summary>File view: shows the files of the selected folder (or all files).</summary>
    private void FillFileList()
    {
        string? folder = (folderCombo.SelectedItem as FolderFilter)?.Folder;
        var shown = folder is null ? phoneFiles : phoneFiles.Where(f => f.Folder == folder).ToList();

        var items = new ListViewItem[shown.Count];
        phoneItems.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            var f = shown[i];
            var item = new ListViewItem([
                f.Name,
                FolderLabel(f.Folder),
                f.Size < 0 ? "?" : FormatSize(f.Size),
                (f.Created ?? f.Modified)?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                f.CanDelete ? "" : "read-only on phone",
            ]) { Tag = f, Checked = checkedIds.Contains(f.ObjectId) };
            if (statusById.TryGetValue(f.ObjectId, out var st))
            {
                item.SubItems[4].Text = st.Text;
                item.ForeColor = st.Color;
            }
            phoneItems[f.ObjectId] = item;
            items[i] = item;
        }

        // ItemChecked fires for every added row; ignore those events while filling.
        filling = true;
        phoneList.BeginUpdate();
        try
        {
            phoneList.ListViewItemSorter = null;
            phoneList.Items.Clear();
            phoneList.Items.AddRange(items);
            phoneList.ListViewItemSorter = new PhoneSorter(sortColumn, sortAscending);
        }
        finally
        {
            phoneList.EndUpdate();
            filling = false;
        }
        countDirty = true;
    }

    private void ApplyViewMode()
    {
        bool folders = FolderView;
        folderList.Visible = folders;
        phoneList.Visible = !folders;
        fileFilterPanel.Visible = !folders;
        moveButton.Text = folders ? "Move checked folders  →" : "Move checked files  →";
        Settings.Current.FolderView = folders;
        UpdateCount();
    }

    private void OpenFolderInFileView()
    {
        if (folderList.SelectedItems.Count != 1)
            return;
        string folder = (string)folderList.SelectedItems[0].Tag!;
        for (int i = 1; i < folderCombo.Items.Count; i++)
            if (((FolderFilter)folderCombo.Items[i]!).Folder == folder)
                folderCombo.SelectedIndex = i; // fires FillFileList
        fileViewRadio.Checked = true;
    }

    private void BlockUserCheckWhileBusy(object? sender, ItemCheckEventArgs e)
    {
        if (busy && !filling)
            e.NewValue = e.CurrentValue;
    }

    private void OnFolderChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (filling)
            return;
        string folder = (string)e.Item.Tag!;
        if (e.Item.Checked) checkedFolders.Add(folder); else checkedFolders.Remove(folder);
        countDirty = true;
    }

    private void OnFileChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (filling)
            return;
        var f = (DeviceFile)e.Item.Tag!;
        if (e.Item.Checked) checkedIds.Add(f.ObjectId); else checkedIds.Remove(f.ObjectId);
        countDirty = true;
    }

    private ListView ActiveList => FolderView ? folderList : phoneList;

    private void SetChecked(ListViewItem item, bool value)
    {
        if (item.Tag is string folder)
        {
            value &= folderStats.TryGetValue(folder, out var s) && s.Count > 0;
            if (value) checkedFolders.Add(folder); else checkedFolders.Remove(folder);
        }
        else if (item.Tag is DeviceFile f)
        {
            value &= !movedIds.Contains(f.ObjectId);
            if (value) checkedIds.Add(f.ObjectId); else checkedIds.Remove(f.ObjectId);
        }
        item.Checked = value;
    }

    private void ForEachRow(IEnumerable rows, Action<ListViewItem> action)
    {
        var list = ActiveList;
        filling = true;
        list.BeginUpdate();
        try
        {
            foreach (ListViewItem i in rows)
                action(i);
        }
        finally
        {
            list.EndUpdate();
            filling = false;
        }
        UpdateCount();
    }

    private void SetShownChecked(bool value) => ForEachRow(ActiveList.Items, i => SetChecked(i, value));

    private void CheckSelectedRows() => ForEachRow(ActiveList.SelectedItems, i => SetChecked(i, true));

    private void UncheckAll()
    {
        checkedIds.Clear();
        checkedFolders.Clear();
        ForEachRow(folderList.Items, i => i.Checked = false);
        ForEachRow(phoneList.Items, i => i.Checked = false);
    }

    /// <summary>The files to move, in folder order then name order.</summary>
    private List<DeviceFile> FilesToMove()
    {
        var files = FolderView
            ? phoneFiles.Where(f => checkedFolders.Contains(f.Folder))
            : phoneFiles.Where(f => checkedIds.Contains(f.ObjectId));
        return files
            .Where(f => !movedIds.Contains(f.ObjectId))
            .OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateCount()
    {
        countDirty = false;
        var files = FilesToMove();
        long bytes = files.Sum(f => Math.Max(0, f.Size));
        phoneCountLabel.Text = FolderView
            ? $"{checkedFolders.Count:N0} of {folderStats.Count:N0} folders checked ({files.Count:N0} files, {FormatSize(bytes)})"
            : $"{files.Count:N0} files checked in total ({FormatSize(bytes)}) — {phoneList.Items.Count:N0} shown";
    }

    private void SortPhoneList(int column)
    {
        sortAscending = column != sortColumn || !sortAscending;
        sortColumn = column;
        phoneList.ListViewItemSorter = new PhoneSorter(sortColumn, sortAscending);
    }

    private sealed class PhoneSorter(int column, bool ascending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (DeviceFile)((ListViewItem)x!).Tag!;
            var b = (DeviceFile)((ListViewItem)y!).Tag!;
            int r = column switch
            {
                0 => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
                2 => a.Size.CompareTo(b.Size),
                3 => Nullable.Compare(a.Created ?? a.Modified, b.Created ?? b.Modified),
                4 => StringComparer.OrdinalIgnoreCase.Compare(((ListViewItem)x!).SubItems[4].Text, ((ListViewItem)y!).SubItems[4].Text),
                _ => StringComparer.OrdinalIgnoreCase.Compare(a.Folder, b.Folder) is var f && f != 0
                    ? f : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            };
            return ascending ? r : -r;
        }
    }

    // ================================================================== PC side

    private sealed record DriveItem(string Root, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PcEntry(string Path, bool IsDirectory, bool IsParent);

    private void LoadDrives()
    {
        object? selected = driveCombo.SelectedItem;
        driveCombo.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                continue;
            string label = $"{d.Name.TrimEnd('\\')} {d.VolumeLabel} ({FormatSize(d.AvailableFreeSpace)} free)";
            driveCombo.Items.Add(new DriveItem(d.RootDirectory.FullName, label));
        }
        if (selected is DriveItem s)
            driveCombo.SelectedItem = driveCombo.Items.Cast<DriveItem>().FirstOrDefault(d => d.Root == s.Root);
    }

    /// <summary>The remembered folder, or its nearest existing parent, or the first drive.</summary>
    private string StartFolder()
    {
        string? path = Settings.Current.PcFolder;
        try
        {
            while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                path = Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            path = null;
        }
        if (!string.IsNullOrEmpty(path))
            return path;
        return driveCombo.Items.Count > 0
            ? ((DriveItem)driveCombo.Items[0]!).Root
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private void NavigateTo(string path, bool showErrors = true)
    {
        try
        {
            path = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(path))
            {
                if (showErrors)
                    MessageBox.Show(this, $"Folder not found:\n{path}", Text);
                return;
            }
            var dir = new DirectoryInfo(path);
            var items = new List<ListViewItem>();
            if (dir.Parent is not null)
                items.Add(new ListViewItem(["..", "", ""]) { Tag = new PcEntry(dir.Parent.FullName, true, true) });
            foreach (var sub in dir.EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0).OrderBy(d => d.Name))
                items.Add(new ListViewItem(["📁 " + sub.Name, "", sub.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)])
                    { Tag = new PcEntry(sub.FullName, true, false) });
            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name).Take(5000))
                items.Add(new ListViewItem([file.Name, FormatSize(file.Length), file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)])
                    { Tag = new PcEntry(file.FullName, false, false) });

            pcList.BeginUpdate();
            pcList.Items.Clear();
            pcList.Items.AddRange(items.ToArray());
            pcList.EndUpdate();

            pathBox.Text = path;
            Settings.Current.PcFolder = path;
            string root = Path.GetPathRoot(path) ?? "";
            driveCombo.SelectedItem = driveCombo.Items.Cast<DriveItem>().FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (showErrors)
                MessageBox.Show(this, $"Cannot open folder:\n{ex.Message}", Text);
        }
    }

    private void NavigateUp()
    {
        var parent = Directory.GetParent(pathBox.Text.TrimEnd('\\'));
        if (parent is not null)
            NavigateTo(parent.FullName);
    }

    private List<PcEntry> SelectedPcEntries() =>
        pcList.SelectedItems.Cast<ListViewItem>().Select(i => (PcEntry)i.Tag!).Where(e => !e.IsParent).ToList();

    private void BuildPcMenu()
    {
        var open = new ToolStripMenuItem("Open", null, (_, _) => OpenPcSelection());
        var explorer = new ToolStripMenuItem("Show in Explorer", null, (_, _) => ShowInExplorer());
        var newFolder = new ToolStripMenuItem("New folder", null, (_, _) => CreateFolder());
        var rename = new ToolStripMenuItem("Rename", null, (_, _) => RenamePcEntry()) { ShortcutKeyDisplayString = "F2" };
        var recycle = new ToolStripMenuItem("Delete (to Recycle Bin)", null, (_, _) => DeletePcEntries(permanent: false)) { ShortcutKeyDisplayString = "Del" };
        var delete = new ToolStripMenuItem("Delete permanently", null, (_, _) => DeletePcEntries(permanent: true)) { ShortcutKeyDisplayString = "Shift+Del" };
        var refresh = new ToolStripMenuItem("Refresh", null, (_, _) => NavigateTo(pathBox.Text)) { ShortcutKeyDisplayString = "F5" };
        pcMenu.Items.AddRange([open, explorer, new ToolStripSeparator(), newFolder, rename, new ToolStripSeparator(),
            recycle, delete, new ToolStripSeparator(), refresh]);

        pcMenu.Opening += (_, _) =>
        {
            var sel = SelectedPcEntries();
            bool busy = cts is not null;
            open.Enabled = pcList.SelectedItems.Count == 1;
            rename.Enabled = sel.Count == 1 && !busy;
            recycle.Enabled = delete.Enabled = sel.Count > 0 && !busy;
        };
    }

    private void OnPcKeyDown(object? sender, KeyEventArgs e)
    {
        bool busy = cts is not null;
        switch (e.KeyCode)
        {
            case Keys.Delete when !busy:
                DeletePcEntries(permanent: e.Shift);
                break;
            case Keys.F2 when !busy:
                RenamePcEntry();
                break;
            case Keys.F5:
                NavigateTo(pathBox.Text);
                break;
            case Keys.Enter:
                OpenPcSelection();
                break;
            case Keys.Back:
                NavigateUp();
                break;
            default:
                return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    private void OpenPcSelection()
    {
        if (pcList.SelectedItems.Count != 1 || pcList.SelectedItems[0].Tag is not PcEntry entry)
            return;
        if (entry.IsDirectory)
            NavigateTo(entry.Path);
        else
            StartShell(entry.Path, null);
    }

    private void ShowInExplorer()
    {
        var sel = SelectedPcEntries();
        if (sel.Count > 0)
            StartShell("explorer.exe", $"/select,\"{sel[0].Path}\"");
        else
            StartShell("explorer.exe", $"\"{pathBox.Text}\"");
    }

    private void StartShell(string file, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, Text);
        }
    }

    private void CreateFolder()
    {
        string? name = Prompt("New folder name:", "iPhone " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            string path = Path.Combine(pathBox.Text, MoveEngine.SafeName(name));
            Directory.CreateDirectory(path);
            NavigateTo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Text);
        }
    }

    private void RenamePcEntry()
    {
        var sel = SelectedPcEntries();
        if (sel.Count != 1)
            return;
        var entry = sel[0];
        string oldName = Path.GetFileName(entry.Path);
        string? name = Prompt("New name:", oldName);
        if (string.IsNullOrWhiteSpace(name) || name == oldName)
            return;
        try
        {
            string target = Path.Combine(Path.GetDirectoryName(entry.Path)!, MoveEngine.SafeName(name));
            if (entry.IsDirectory)
                Directory.Move(entry.Path, target);
            else
                File.Move(entry.Path, target);
            NavigateTo(pathBox.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Text);
        }
    }

    private void DeletePcEntries(bool permanent)
    {
        var sel = SelectedPcEntries();
        if (sel.Count == 0)
            return;
        string what = sel.Count == 1 ? $"\"{Path.GetFileName(sel[0].Path)}\"" : $"{sel.Count} items";
        string question = permanent
            ? $"Delete {what} PERMANENTLY?\nThis cannot be undone."
            : $"Move {what} to the Recycle Bin?";
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo,
                permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (var e in sel)
        {
            try
            {
                if (permanent)
                {
                    if (e.IsDirectory) Directory.Delete(e.Path, recursive: true);
                    else File.Delete(e.Path);
                }
                else
                {
                    RecycleBin.Send(e.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Cannot delete {e.Path}:\n{ex.Message}", Text);
                break;
            }
        }
        LoadDrives();
        NavigateTo(pathBox.Text);
    }

    private string? Prompt(string question, string value)
    {
        using var form = new Form
        {
            Text = Text, Width = 420, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
        };
        var box = new TextBox { Text = value, Left = 12, Top = 32, Width = 380 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 236, Top = 64, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 317, Top = 64, Width = 75 };
        form.Controls.AddRange([new Label { Text = question, Left = 12, Top = 10, AutoSize = true }, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    // ================================================================== move

    private async Task MoveAsync()
    {
        if (device is null)
        {
            MessageBox.Show(this, "Load the files from the phone first.", Text);
            return;
        }
        var selected = FilesToMove();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, FolderView ? "Check the folders you want to move (left list)." : "Check the files you want to move (left list).", Text);
            return;
        }
        string dest = pathBox.Text;
        if (!Directory.Exists(dest))
        {
            MessageBox.Show(this, "The destination folder does not exist.", Text);
            return;
        }

        // Files that belong to the same photo (Live Photo video, edited copy, .AAE) are moved together.
        var groups = AssetGrouping.Expand(selected, phoneFiles.Where(f => !movedIds.Contains(f.ObjectId)));
        var all = groups.SelectMany(g => g).ToList();
        int added = all.Count - selected.Count;
        long total = all.Sum(f => Math.Max(0, f.Size));
        bool delete = deleteCheck.Checked;
        var folders = all.Select(f => f.Folder).Distinct().ToList();

        string example = Path.Combine(dest, FolderLabel(folders[0]));
        string msg = $"{all.Count:N0} files ({FormatSize(total)}) from {folders.Count:N0} phone folder(s) will be copied to:\n" +
                     $"{dest}\n(one PC folder per phone folder, for example {example})\n\n";
        if (added > 0)
            msg += $"{added:N0} related files were added (Live Photo videos, edited versions, .AAE), " +
                   "because iOS keeps them together with the checked photos.\n\n";
        msg += delete
            ? "Each photo is copied, checked (size + file format) and then DELETED from the iPhone, one photo at a time.\n" +
              "You can stop at any time and continue later.\n" +
              "Deleted files may not go to \"Recently Deleted\" on the phone.\n\nContinue?"
            : "Files stay on the iPhone (copy only).\n\nContinue?";
        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.OKCancel,
                delete ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
            return;

        cts = new CancellationTokenSource();
        progressBytes = 0;
        progressTotal = Math.Max(1, total);
        filesDone = 0;
        filesTotal = all.Count;
        progressStart = DateTime.Now;
        SetBusy(true, "Moving...");
        progressTimer.Start();
        Log($"--- {(delete ? "MOVE" : "COPY")} {all.Count:N0} files from {folders.Count:N0} folder(s) to {dest} " +
            $"(delay {WpdDevice.ActionDelayMs} ms after each phone action)");

        var report = new Progress<FileReport>(OnFileReport);
        var engine = new MoveEngine(device, dest, delete, report, n => Interlocked.Exchange(ref progressBytes, n),
            AskUser, line => BeginInvoke(() => Log(line)));
        var token = cts.Token;
        KeepAwake(true); // a long move must not be stopped by PC sleep
        try
        {
            var summary = await Task.Run(() => engine.Run(groups, token));
            Log($"Done. Moved: {summary.Moved:N0}, copied only: {summary.CopiedOnly:N0}, failed: {summary.Failed:N0}. " +
                $"A log file ({MoveEngine.LogFileName}) is in each PC folder.");
            if (summary.Failed > 0)
                MessageBox.Show(this, $"{summary.Failed:N0} file(s) failed. They are still on the phone.\nSee the Status column and the log.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            Log("Stopped. Files that were not finished are still on the phone. Click Move again to continue.");
        }
        catch (Exception ex)
        {
            Log("Stopped with error: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            KeepAwake(false);
            progressTimer.Stop();
            UpdateProgress();
            SetBusy(false);
            LoadDrives();
            NavigateTo(pathBox.Text);
            // Hide emptied folders and moved files. In file view the result rows stay until the next refresh.
            if (FolderView)
                RebuildPhoneViews((folderCombo.SelectedItem as FolderFilter)?.Folder);
            UpdateCount();
        }
    }

    private void OnFileReport(FileReport r)
    {
        string text = r.State switch
        {
            FileState.Copying => "copying...",
            FileState.Moved => "✔ " + r.Detail,
            FileState.Copied => "◐ " + r.Detail,
            _ => "✖ " + r.Detail,
        };
        Color color = r.State switch
        {
            FileState.Moved => SystemColors.GrayText,
            FileState.Copied => Color.DarkGoldenrod,
            FileState.Failed => Color.Firebrick,
            _ => SystemColors.WindowText,
        };
        statusById[r.File.ObjectId] = (text, color);
        if (r.State != FileState.Copying)
            filesDone++;

        if (r.State == FileState.Moved)
        {
            movedIds.Add(r.File.ObjectId); // gone from the phone
            checkedIds.Remove(r.File.ObjectId);
            countDirty = true;
        }
        if (r.State == FileState.Failed)
            Log($"{r.File.DevicePath}: {r.Detail}");

        UpdateFolderRow(r);

        if (!phoneItems.TryGetValue(r.File.ObjectId, out var item))
            return;
        item.SubItems[4].Text = text;
        item.ForeColor = color;
        if (r.State == FileState.Copying && phoneList.Visible)
            item.EnsureVisible();
        if (r.State == FileState.Moved)
        {
            filling = true;
            item.Checked = false;
            filling = false;
        }
    }

    /// <summary>
    /// Called from the move thread when the move must pause (low disk space, phone connection lost).
    /// Blocks until the user answers. Retry = true.
    /// </summary>
    private bool AskUser(string message) => (bool)Invoke(() =>
    {
        string firstLine = message.Split('\n')[0];
        Log("PAUSED: " + firstLine);
        statusLabel.Text = "PAUSED — waiting for you";
        var answer = MessageBox.Show(this, message, Text + " — paused", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
        LoadDrives();
        Log(answer == DialogResult.Retry ? "Retry." : "Cancel.");
        return answer == DialogResult.Retry;
    });

    private void UpdateFolderRow(FileReport r)
    {
        if (!folderStats.TryGetValue(r.File.Folder, out var st) || !folderItems.TryGetValue(r.File.Folder, out var row))
            return;
        if (r.State == FileState.Moved)
        {
            st.Count--;
            st.Bytes -= Math.Max(0, r.File.Size);
            SetFolderRowCounts(row, st);
        }
        if (r.State == FileState.Failed)
            st.Failed++;

        string failed = st.Failed > 0 ? $", {st.Failed} failed" : "";
        if (st.Count == 0)
        {
            row.SubItems[5].Text = "✔ all moved";
            row.ForeColor = SystemColors.GrayText;
            filling = true;
            row.Checked = false;
            filling = false;
            checkedFolders.Remove(st.Folder);
        }
        else
        {
            row.SubItems[5].Text = r.State == FileState.Copying ? $"moving... {st.Count:N0} left{failed}" : $"{st.Count:N0} left{failed}";
            row.ForeColor = st.Failed > 0 ? Color.Firebrick : SystemColors.WindowText;
            if (r.State == FileState.Copying && folderList.Visible)
                row.EnsureVisible();
        }
    }

    private void UpdateProgress()
    {
        long done = Interlocked.Read(ref progressBytes);
        progressBar.Value = (int)Math.Clamp(done * 1000 / Math.Max(1, progressTotal), 0, 1000);
        double secs = Math.Max(0.1, (DateTime.Now - progressStart).TotalSeconds);
        double speed = done / secs;
        string eta = speed > 1 && done > 0
            ? TimeSpan.FromSeconds((progressTotal - done) / speed).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : "?";
        statusLabel.Text = $"{filesDone:N0} / {filesTotal:N0} files — {FormatSize(done)} of {FormatSize(progressTotal)} — " +
                           $"{FormatSize((long)speed)}/s — left: {eta}";
    }

    // ================================================================== misc

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>Stops Windows from going to sleep while a move runs (the screen may still turn off).</summary>
    private static void KeepAwake(bool on)
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        this.busy = busy;
        foreach (var b in checkButtons)
            b.Enabled = !busy;
        findDevicesButton.Enabled = !busy;
        loadFilesButton.Enabled = !busy;
        deviceCombo.Enabled = !busy;
        folderCombo.Enabled = !busy;
        folderViewRadio.Enabled = fileViewRadio.Enabled = !busy;
        moveButton.Enabled = !busy;
        deleteCheck.Enabled = !busy;
        cancelButton.Enabled = busy && cts is not null;
        UseWaitCursor = busy;
        if (status is not null)
            statusLabel.Text = status;
        else if (!busy)
            statusLabel.Text = "Ready";
        if (!busy)
        {
            cts?.Dispose();
            cts = null;
        }
    }

    private void Log(string line)
    {
        logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (cts is not null)
        {
            if (MessageBox.Show(this, "Work is still running. Stop it and close?\n(You can continue the move next time.)",
                    Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cts.Cancel();
        }
        Settings.Save();
        // Do not let a phone that does not answer block the window: wait at most 2 seconds.
        var d = device;
        device = null;
        if (d is not null)
            Task.Run(d.Dispose).Wait(TimeSpan.FromSeconds(2));
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.0} {units[u]}";
    }
}
