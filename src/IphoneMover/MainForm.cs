using System.Collections;
using System.Globalization;
using IphoneMover.Core;
using IphoneMover.Wpd;

namespace IphoneMover;

internal sealed class MainForm : Form
{
    // top
    private readonly ComboBox deviceCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button findDevicesButton = new() { Text = "Find devices", AutoSize = true };
    private readonly Button loadFilesButton = new() { Text = "Load files from phone", AutoSize = true };

    // left: phone
    private readonly ListView phoneList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HideSelection = false, GridLines = true,
    };
    private readonly Label phoneCountLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly ComboBox folderCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, MaxDropDownItems = 30 };

    // right: PC
    private readonly ComboBox driveCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox pathBox = new() { Dock = DockStyle.Fill };
    private readonly ListView pcList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, GridLines = true,
    };

    // bottom
    private readonly CheckBox deleteCheck = new()
    {
        Text = "Delete from iPhone after verified copy (MOVE)", Checked = true, AutoSize = true,
        Padding = new Padding(0, 4, 0, 0),
    };
    private readonly Button moveButton = new() { Text = "Move checked files  →", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Button cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly ProgressBar progressBar = new() { Width = 260, Height = 22, Maximum = 1000 };
    private readonly Label statusLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox logBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };
    private readonly System.Windows.Forms.Timer progressTimer = new() { Interval = 250 };

    private WpdDevice? device;
    private List<DeviceFile> phoneFiles = [];
    private readonly Dictionary<string, ListViewItem> phoneItems = [];           // rows shown now
    private readonly HashSet<string> checkedIds = [];                              // checked files, in all folders
    private readonly Dictionary<string, (string Text, Color Color)> statusById = []; // survives folder switches
    private readonly System.Windows.Forms.Timer countTimer = new() { Interval = 150 };
    private bool countDirty;
    private bool fillingList;
    private bool fillingFolders;
    private CancellationTokenSource? cts;
    private long progressBytes, progressTotal;
    private DateTime progressStart;
    private int sortColumn = 1;
    private bool sortAscending = true;

    public MainForm()
    {
        Text = "iPhone Mover — move photos and videos from iPhone to PC";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont!;

        BuildLayout();
        WireEvents();
        LoadDrives();
        NavigateTo(Settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        SetBusy(false);
    }

    // ================================================================== layout

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        top.Controls.AddRange([new Label { Text = "iPhone:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            deviceCombo, findDevicesButton, loadFilesButton]);

        phoneList.Columns.Add("Name", 170);
        phoneList.Columns.Add("Folder", 110);
        phoneList.Columns.Add("Size", 90, HorizontalAlignment.Right);
        phoneList.Columns.Add("Date", 130);
        phoneList.Columns.Add("Status", 330);

        var phoneTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        var selectAll = new Button { Text = "Check all shown", AutoSize = true };
        var selectNone = new Button { Text = "Uncheck all", AutoSize = true };
        var checkSelected = new Button { Text = "Check selected rows", AutoSize = true };
        selectAll.Click += (_, _) => SetShownChecked(true);
        selectNone.Click += (_, _) => UncheckAll();
        checkSelected.Click += (_, _) => CheckSelectedRows();
        phoneTools.Controls.AddRange([selectAll, selectNone, checkSelected, phoneCountLabel]);

        // Chunk by chunk: show one phone folder at a time (iOS makes one folder per month, e.g. 202606__).
        var folderRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        var prevFolder = new Button { Text = "◀", Width = 32, Height = folderCombo.Height + 2 };
        var nextFolder = new Button { Text = "▶", Width = 32, Height = folderCombo.Height + 2 };
        prevFolder.Click += (_, _) => { if (folderCombo.SelectedIndex > 0) folderCombo.SelectedIndex--; };
        nextFolder.Click += (_, _) => { if (folderCombo.SelectedIndex < folderCombo.Items.Count - 1) folderCombo.SelectedIndex++; };
        folderRow.Controls.AddRange([new Label { Text = "Folder:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            folderCombo, prevFolder, nextFolder]);

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(phoneList);
        left.Controls.Add(phoneTools);
        left.Controls.Add(folderRow);
        left.Controls.Add(Header("On the iPhone"));

        pcList.Columns.Add("Name", 260);
        pcList.Columns.Add("Size", 90, HorizontalAlignment.Right);
        pcList.Columns.Add("Modified", 130);

        var upButton = new Button { Text = "Up", AutoSize = true };
        var newFolderButton = new Button { Text = "New folder", AutoSize = true };
        var refreshButton = new Button { Text = "Refresh", AutoSize = true };
        upButton.Click += (_, _) => NavigateUp();
        newFolderButton.Click += (_, _) => CreateFolder();
        refreshButton.Click += (_, _) => NavigateTo(pathBox.Text);
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
        right.Controls.Add(Header("On this PC (files go into the Destination folder)"));

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
        findDevicesButton.Click += async (_, _) => await FindDevicesAsync();
        loadFilesButton.Click += async (_, _) => await LoadPhoneFilesAsync();
        moveButton.Click += async (_, _) => await MoveAsync();
        cancelButton.Click += (_, _) => cts?.Cancel();
        phoneList.ItemChecked += OnItemChecked;
        folderCombo.SelectedIndexChanged += (_, _) => { if (!fillingFolders) FillPhoneList(); };
        countTimer.Tick += (_, _) => { if (countDirty) UpdateCount(); };
        countTimer.Start();
        phoneList.ColumnClick += (_, e) => SortPhoneList(e.Column);
        driveCombo.SelectionChangeCommitted += (_, _) => { if (driveCombo.SelectedItem is DriveItem d) NavigateTo(d.Root); };
        pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { NavigateTo(pathBox.Text); e.SuppressKeyPress = true; } };
        pcList.DoubleClick += (_, _) => { if (pcList.SelectedItems.Count == 1 && pcList.SelectedItems[0].Tag is string dir) NavigateTo(dir); };
        progressTimer.Tick += (_, _) => UpdateProgress();
        Load += async (_, _) => await FindDevicesAsync();
        FormClosing += OnFormClosing;
    }

    // ================================================================== phone side

    private async Task FindDevicesAsync()
    {
        SetBusy(true, "Looking for devices...");
        try
        {
            var devices = await Task.Run(WpdDevice.ListDevices);
            deviceCombo.Items.Clear();
            foreach (var d in devices.OrderByDescending(d => d.LooksLikeApple))
                deviceCombo.Items.Add(d);
            if (deviceCombo.Items.Count > 0)
                deviceCombo.SelectedIndex = 0;
            Log(devices.Count == 0
                ? "No portable device found. Connect the iPhone with USB, unlock it and tap \"Trust\"."
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
        try
        {
            if (device is null || device.Info.Id != info.Id)
            {
                device?.Dispose();
                device = null;
                device = await Task.Run(() => WpdDevice.Open(info));
            }

            var progress = new Progress<string>(folder => statusLabel.Text = "Reading " + folder);
            var dev = device;
            var token = cts.Token;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            phoneFiles = await Task.Run(() => dev.ListFiles(progress, token));
            checkedIds.Clear();
            statusById.Clear();
            FillFolderCombo();
            Log($"Phone has {phoneFiles.Count:N0} files, {FormatSize(phoneFiles.Sum(f => Math.Max(0, f.Size)))}, " +
                $"in {folderCombo.Items.Count - 1} folders (read in {sw.Elapsed.TotalSeconds:0.0} s). " +
                "Pick a folder to work chunk by chunk.");
            if (phoneFiles.Count == 0)
                Log("No files. Unlock the iPhone, tap \"Trust\", then click \"Load files\" again.");
        }
        catch (OperationCanceledException)
        {
            Log("Reading the file list was cancelled.");
        }
        catch (Exception ex)
        {
            Log("Could not read the phone: " + ex.Message);
            device?.Dispose();
            device = null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private sealed record FolderItem(string? Folder, int Count, long Bytes)
    {
        public override string ToString() =>
            $"{Folder ?? "All folders"}   ({Count:N0} files, {FormatSize(Bytes)})";
    }

    /// <summary>
    /// Fills the folder filter and keeps the current folder selected when it still exists.
    /// With <paramref name="refillList"/> = false the rows stay as they are (to keep the results of a move visible).
    /// </summary>
    private void FillFolderCombo(bool refillList = true)
    {
        string? current = (folderCombo.SelectedItem as FolderItem)?.Folder;
        bool wasAll = folderCombo.SelectedItem is FolderItem { Folder: null };

        fillingFolders = true;
        try
        {
            folderCombo.BeginUpdate();
            folderCombo.Items.Clear();
            folderCombo.Items.Add(new FolderItem(null, phoneFiles.Count, phoneFiles.Sum(f => Math.Max(0, f.Size))));
            foreach (var g in phoneFiles.GroupBy(f => f.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                folderCombo.Items.Add(new FolderItem(g.Key, g.Count(), g.Sum(f => Math.Max(0, f.Size))));
            folderCombo.EndUpdate();

            int index = wasAll ? 0 : -1;
            for (int i = 1; i < folderCombo.Items.Count && index < 0; i++)
                if (((FolderItem)folderCombo.Items[i]!).Folder == current)
                    index = i;
            if (index < 0)
            {
                index = folderCombo.Items.Count > 1 ? 1 : 0; // start with the first (oldest) folder
                refillList = true;
            }
            folderCombo.SelectedIndex = index;
        }
        finally
        {
            fillingFolders = false;
        }
        if (refillList)
            FillPhoneList();
    }

    /// <summary>Shows the files of the selected folder (or all files).</summary>
    private void FillPhoneList()
    {
        string? folder = (folderCombo.SelectedItem as FolderItem)?.Folder;
        var shown = folder is null ? phoneFiles : phoneFiles.Where(f => f.Folder == folder).ToList();

        var items = new ListViewItem[shown.Count];
        phoneItems.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            var f = shown[i];
            var item = new ListViewItem([
                f.Name,
                f.Folder,
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
        fillingList = true;
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
            fillingList = false;
        }
        UpdateCount();
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (fillingList)
            return;
        var f = (DeviceFile)e.Item.Tag!;
        if (e.Item.Checked)
            checkedIds.Add(f.ObjectId);
        else
            checkedIds.Remove(f.ObjectId);
        countDirty = true;
    }

    private void SetShownChecked(bool value)
    {
        fillingList = true;
        phoneList.BeginUpdate();
        try
        {
            foreach (ListViewItem i in phoneList.Items)
            {
                var f = (DeviceFile)i.Tag!;
                bool v = value && phoneFiles.Count > 0 && i.ForeColor != SystemColors.GrayText;
                i.Checked = v;
                if (v) checkedIds.Add(f.ObjectId); else checkedIds.Remove(f.ObjectId);
            }
        }
        finally
        {
            phoneList.EndUpdate();
            fillingList = false;
        }
        UpdateCount();
    }

    private void UncheckAll()
    {
        checkedIds.Clear();
        SetShownChecked(false);
    }

    private void CheckSelectedRows()
    {
        fillingList = true;
        phoneList.BeginUpdate();
        try
        {
            foreach (ListViewItem i in phoneList.SelectedItems)
            {
                i.Checked = true;
                checkedIds.Add(((DeviceFile)i.Tag!).ObjectId);
            }
        }
        finally
        {
            phoneList.EndUpdate();
            fillingList = false;
        }
        UpdateCount();
    }

    private List<DeviceFile> CheckedFiles() => phoneFiles.Where(f => checkedIds.Contains(f.ObjectId)).ToList();

    private void UpdateCount()
    {
        countDirty = false;
        var files = CheckedFiles();
        long bytes = files.Sum(f => Math.Max(0, f.Size));
        phoneCountLabel.Text = $"{files.Count:N0} checked in total ({FormatSize(bytes)}) — {phoneList.Items.Count:N0} shown";
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

    private void LoadDrives()
    {
        driveCombo.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                continue;
            string label = $"{d.Name.TrimEnd('\\')} {d.VolumeLabel} ({FormatSize(d.AvailableFreeSpace)} free)";
            driveCombo.Items.Add(new DriveItem(d.RootDirectory.FullName, label));
        }
    }

    private void NavigateTo(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, $"Folder not found:\n{path}", Text);
                return;
            }
            var dir = new DirectoryInfo(path);

            pcList.BeginUpdate();
            pcList.Items.Clear();
            if (dir.Parent is not null)
                pcList.Items.Add(new ListViewItem(["..", "", ""]) { Tag = dir.Parent.FullName });
            foreach (var sub in dir.EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0).OrderBy(d => d.Name))
                pcList.Items.Add(new ListViewItem(["📁 " + sub.Name, "", sub.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]) { Tag = sub.FullName });
            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name).Take(5000))
                pcList.Items.Add(new ListViewItem([file.Name, FormatSize(file.Length), file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]));
            pcList.EndUpdate();

            pathBox.Text = path;
            Settings.LastFolder = path;
            string root = Path.GetPathRoot(path) ?? "";
            driveCombo.SelectedItem = driveCombo.Items.Cast<DriveItem>().FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            pcList.EndUpdate();
            MessageBox.Show(this, $"Cannot open folder:\n{ex.Message}", Text);
        }
    }

    private void NavigateUp()
    {
        var parent = Directory.GetParent(pathBox.Text.TrimEnd('\\'));
        if (parent is not null)
            NavigateTo(parent.FullName);
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
        var selected = CheckedFiles();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Check the files you want to move (left list).", Text);
            return;
        }
        string dest = pathBox.Text;
        if (!Directory.Exists(dest))
        {
            MessageBox.Show(this, "The destination folder does not exist.", Text);
            return;
        }

        // Files that belong to the same photo (Live Photo video, edited copy, .AAE) are moved together.
        var groups = AssetGrouping.Expand(selected, phoneFiles);
        var all = groups.SelectMany(g => g).ToList();
        int added = all.Count - selected.Count;
        long total = all.Sum(f => Math.Max(0, f.Size));
        bool delete = deleteCheck.Checked;

        int folders = all.Select(f => f.Folder).Distinct().Count();
        string msg = $"{all.Count:N0} files ({FormatSize(total)}) from {folders:N0} phone folder(s) will be copied to:\n{dest}\n\n";
        if (added > 0)
            msg += $"{added:N0} related files were added (Live Photo videos, edited versions, .AAE), " +
                   "because iOS keeps them together with the checked photos.\n\n";
        msg += delete
            ? "After each photo is copied and checked (size + file format), it will be DELETED from the iPhone.\n" +
              "Deleted files may not go to \"Recently Deleted\" on the phone.\n\nContinue?"
            : "Files stay on the iPhone (copy only).\n\nContinue?";
        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.OKCancel,
                delete ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
            return;

        cts = new CancellationTokenSource();
        progressBytes = 0;
        progressTotal = Math.Max(1, total);
        progressStart = DateTime.Now;
        SetBusy(true, "Moving...");
        progressTimer.Start();
        Log($"--- {(delete ? "MOVE" : "COPY")} {all.Count:N0} files to {dest}");

        var report = new Progress<FileReport>(OnFileReport);
        var engine = new MoveEngine(device, dest, delete, report, n => Interlocked.Exchange(ref progressBytes, n));
        var token = cts.Token;
        try
        {
            var summary = await Task.Run(() => engine.Run(groups, token));
            Log($"Done. Moved: {summary.Moved:N0}, copied only: {summary.CopiedOnly:N0}, failed: {summary.Failed:N0}. " +
                $"Log file: {Path.Combine(dest, MoveEngine.LogFileName)}");
            if (summary.Failed > 0)
                MessageBox.Show(this, $"{summary.Failed:N0} file(s) failed. They are still on the phone.\nSee the Status column and the log.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled. Files that were not finished are still on the phone.");
        }
        catch (Exception ex)
        {
            Log("Stopped with error: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            progressTimer.Stop();
            UpdateProgress();
            SetBusy(false);
            NavigateTo(pathBox.Text);
            FillFolderCombo(refillList: false); // new file counts; keep the result rows visible
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

        if (r.State == FileState.Moved)
        {
            phoneFiles.Remove(r.File); // gone from the phone
            checkedIds.Remove(r.File.ObjectId);
            countDirty = true;
        }
        if (r.State == FileState.Failed)
            Log($"{r.File.DevicePath}: {r.Detail}");

        if (!phoneItems.TryGetValue(r.File.ObjectId, out var item))
            return;
        item.SubItems[4].Text = text;
        item.ForeColor = color;
        if (r.State == FileState.Copying)
            item.EnsureVisible();
        if (r.State == FileState.Moved)
        {
            fillingList = true;
            item.Checked = false;
            fillingList = false;
        }
    }

    private void UpdateProgress()
    {
        long done = Interlocked.Read(ref progressBytes);
        progressBar.Value = (int)Math.Clamp(done * 1000 / Math.Max(1, progressTotal), 0, 1000);
        double secs = Math.Max(0.1, (DateTime.Now - progressStart).TotalSeconds);
        statusLabel.Text = $"{FormatSize(done)} of {FormatSize(progressTotal)} — {FormatSize((long)(done / secs))}/s";
    }

    // ================================================================== misc

    private void SetBusy(bool busy, string? status = null)
    {
        findDevicesButton.Enabled = !busy;
        loadFilesButton.Enabled = !busy;
        deviceCombo.Enabled = !busy;
        folderCombo.Enabled = !busy;
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
            if (MessageBox.Show(this, "Work is still running. Cancel it and close?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cts.Cancel();
        }
        Settings.Save();
        device?.Dispose();
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
