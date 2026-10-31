// FrameUpload: copy videos/pictures from this PC to the Steam Frame's ~/Videos
// over ssh (the headset has no SMB share), using the "frame" host entry in
// %USERPROFILE%\.ssh\config.
// v2: Windows' built-in ssh.exe (OpenSSH for Windows 9.5) tops out at about
// 12-15 MB/s on its own, while the link does 55 MB/s on one TCP stream and
// ~70 on several. So v2 prefers Git for Windows' ssh.exe when it is installed
// and sends big files as several byte ranges at once, each over its own ssh
// connection, written into place on the headset with dd.
// v3: a view of the headset's ~/Videos (browse, pick the destination folder,
// make folders, delete) and a "Connect to Frame" button that finds the
// headset when the "frame" entry stops working (mDNS for frame.local drops
// out at times): it tries the entry, then the last IP that worked, then
// frame.local, and asks for an IP. Copies go to whichever address connected.
// v4: the headset view sorts by a clicked column and shows folder sizes (du).
// v5: rename. v6: the view browses the whole headset, not just ~/Videos (a
// path box, Places, hidden files), copies go to the folder on show, and
// Download... copies headset files and folders back to the PC. Delete and
// rename stay inside the home folder and removable drives.
// Build: build.cmd (uses the .NET Framework csc.exe that ships with Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Options
{
    public string Host = "frame", Sub = "", Dir, Ssh, Log;
    public int Streams;   // 0: the default for the ssh in use
    public bool Exit;     // close when the batch is done (scripted runs)
    public readonly List<string> Files = new List<string>();
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Optional: --host=NAME --sub=FOLDER (under ~/Videos) --dir=PATH (any
        // headset folder, ~/... or /...) --streams=N --ssh=PATH\ssh.exe
        // --log=FILE --exit FILE... (files passed in, e.g. by dropping them on
        // the exe icon, start copying right away).
        var o = new Options();
        foreach (var a in args)
        {
            if (a.StartsWith("--host=")) o.Host = a.Substring(7);
            else if (a.StartsWith("--sub=")) o.Sub = a.Substring(6);
            else if (a.StartsWith("--dir=")) o.Dir = a.Substring(6);
            else if (a.StartsWith("--streams=")) int.TryParse(a.Substring(10), out o.Streams);
            else if (a.StartsWith("--ssh=")) o.Ssh = a.Substring(6);
            else if (a.StartsWith("--log=")) o.Log = a.Substring(6);
            else if (a == "--exit") o.Exit = true;
            else o.Files.Add(a);
        }
        var form = new UploadForm(o);
        Application.Run(form);
        return form.FailCount;
    }
}

class UploadForm : Form
{
    // Files below this go as one stream: a range connection costs a handshake.
    const long ParallelMin = 128L << 20;
    // Each range is at least this big.
    const long RangeMin = 32L << 20;

    readonly Options opt;
    readonly string sshExe;
    readonly bool gitSsh;
    readonly int streams;
    readonly TextBox pathBox = new TextBox();
    readonly ComboBox places = new ComboBox();
    readonly CheckBox hiddenBox = new CheckBox();
    readonly Button downloadButton = new Button();
    readonly ListBox list = new ListBox();
    readonly ProgressBar fileBar = new ProgressBar();
    readonly ProgressBar totalBar = new ProgressBar();
    readonly Label status = new Label();
    readonly Button addButton = new Button();
    readonly Button connectButton = new Button();
    readonly Button ipButton = new Button();
    readonly Label connLabel = new Label();
    readonly ListView remote = new ListView();
    readonly Button upButton = new Button();
    readonly Button refreshButton = new Button();
    readonly Button newFolderButton = new Button();
    readonly Button deleteButton = new Button();
    readonly Button renameButton = new Button();
    readonly Label remoteInfo = new Label();
    readonly ToolTip tips = new ToolTip();
    // The HostName that connected, passed to ssh as -o HostName= so the
    // "frame" entry's user and keys still apply; null: the entry as it is.
    volatile string hostName;
    // The headset's home folder ($HOME), learned on connect: paths under it
    // show as ~/..., and ".." can climb out of ~ once it is known.
    volatile string home;
    int connecting;      // 1 while a connect runs
    int listSeq;         // drops listings that a newer one overtook
    string listedPath;   // the folder the view shows (~/... or /...)
    string selectAfter;  // a name to select once the next listing shows
    string lastLocalDir; // where the last download went
    readonly Queue<Job> queue = new Queue<Job>();
    readonly object queueLock = new object();
    bool busy;
    volatile string currentPart;   // the .part being written, for a quit mid-copy
    volatile string currentLocalPart; // same for a download, on the PC
    readonly List<Process> live = new List<Process>();
    int doneCount, failCount, skipCount;
    long batchBytes, batchSent;

    public int FailCount { get { return failCount; } }

    public UploadForm(Options o)
    {
        opt = o;
        sshExe = FindSsh(o.Ssh, out gitSsh);
        // Measured on the Frame over 5 GHz Wi-Fi: Git's ssh 1 stream 40-55
        // MB/s, 4 streams 62, 8 streams 69; Windows' ssh 12-15 per stream.
        streams = o.Streams > 0 ? Math.Min(o.Streams, 16) : (gitSsh ? 6 : 8);
        string sub = o.Sub.Trim().Replace('\\', '/').Trim('/');
        pathBox.Text = !string.IsNullOrEmpty(o.Dir) ? o.Dir : "~/Videos" + (sub.Length > 0 ? "/" + sub : "");
        if (o.Files.Count > 0) Shown += delegate { Enqueue(o.Files.ToArray()); };
        Text = "Frame Upload";
        ClientSize = new Size(640, 640);
        MinimumSize = new Size(600, 480);
        AllowDrop = true;
        Font = new Font("Segoe UI", 9f);

        connectButton.Text = "Connect to Frame";
        connectButton.Size = new Size(130, 28);
        connectButton.Location = new Point(12, 10);
        connectButton.Click += delegate { Connect(null, true); };
        ipButton.Text = "Set IP...";
        ipButton.Size = new Size(75, 28);
        ipButton.Location = new Point(148, 10);
        ipButton.Click += delegate { AskForIp(null); };
        connLabel.Location = new Point(232, 16);
        connLabel.Size = new Size(396, 20);
        connLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        connLabel.AutoEllipsis = true;
        connLabel.Text = "Not connected yet.";

        // The folder the view shows is the one files are copied to. The box
        // takes ~/..., /..., or a path relative to the folder on show.
        var dest = new Label { Text = "Copy to", AutoSize = true, Location = new Point(12, 53) };
        pathBox.Location = new Point(62, 50);
        pathBox.Width = 352;
        pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        pathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ListFolder(); } };
        tips.SetToolTip(pathBox, "A headset folder: ~/... (home), /... (anywhere), or a name inside the folder on show. Enter opens it.");
        places.DropDownStyle = ComboBoxStyle.DropDownList;
        places.Items.AddRange(new object[] { "Places...", "Videos", "Home", "Downloads", "SD card / USB", "Whole headset (/)" });
        places.SelectedIndex = 0;
        places.Size = new Size(140, 26);
        places.Location = new Point(420, 50);
        places.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        places.SelectedIndexChanged += delegate { GoToPlace(); };
        upButton.Size = new Size(64, 26);
        upButton.Location = new Point(564, 48);
        upButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        hiddenBox.Text = "Show hidden";
        hiddenBox.AutoSize = true;
        hiddenBox.Location = new Point(14, 84);
        hiddenBox.CheckedChanged += delegate { ListFolder(); };
        var x = 628;
        foreach (var b in new[] { downloadButton, deleteButton, renameButton, newFolderButton, refreshButton })
        {
            b.Size = new Size(b == newFolderButton ? 84 : b == renameButton ? 70 : b == downloadButton ? 84 : 64, 26);
            x -= b.Width;
            b.Location = new Point(x, 80);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            x -= 4;
        }
        downloadButton.Text = "Download...";
        downloadButton.Click += delegate { DownloadSelected(); };
        tips.SetToolTip(downloadButton, "Copy the selected headset files or folders to a folder on this PC.");
        upButton.Text = "Up";
        upButton.Click += delegate { GoUp(); };
        refreshButton.Text = "Refresh";
        refreshButton.Click += delegate { ListFolder(); };
        newFolderButton.Text = "New folder...";
        newFolderButton.Click += delegate { NewFolder(); };
        deleteButton.Text = "Delete...";
        deleteButton.Click += delegate { DeleteSelected(); };
        renameButton.Text = "Rename...";
        renameButton.Click += delegate { RenameSelected(); };

        remote.View = View.Details;
        remote.FullRowSelect = true;
        remote.HideSelection = false;
        remote.Dock = DockStyle.Fill;
        remote.Columns.Add("On the headset", 330);
        remote.Columns.Add("Size", 90, HorizontalAlignment.Right);
        remote.Columns.Add("Modified", 130);
        remote.ItemActivate += delegate { OpenSelected(); };
        remote.ColumnClick += (s, e) => SortBy(e.Column);
        remote.ListViewItemSorter = sorter;
        remote.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Back) GoUp();
            else if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F2) RenameSelected();
            else if (e.KeyCode == Keys.F5) ListFolder();
        };

        list.Dock = DockStyle.Fill;
        list.HorizontalScrollbar = true;
        list.IntegralHeight = false;

        var split = new SplitContainer
        {
            Orientation = Orientation.Horizontal,
            Location = new Point(12, 112),
            Size = new Size(616, 392),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            SplitterDistance = 250,
            SplitterWidth = 6,
        };
        remoteInfo.Dock = DockStyle.Bottom;
        remoteInfo.Height = 22;
        remoteInfo.TextAlign = ContentAlignment.MiddleLeft;
        remoteInfo.ForeColor = SystemColors.GrayText;
        split.Panel1.Controls.Add(remote);
        split.Panel1.Controls.Add(remoteInfo);
        remote.BringToFront(); // fill what the info line leaves
        split.Panel2.Controls.Add(list);

        fileBar.Location = new Point(12, 514);
        fileBar.Size = new Size(616, 18);
        fileBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        totalBar.Location = new Point(12, 538);
        totalBar.Size = new Size(616, 18);
        totalBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        status.Location = new Point(12, 564);
        status.Size = new Size(616, 34);
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        status.Text = "Drag videos or pictures here, or click Add files.";

        var via = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Location = new Point(12, 609),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            Text = "via " + (gitSsh ? "Git's ssh" : "Windows' ssh (slow: install Git for Windows for ~4x)")
                + ", up to " + streams + " streams" };

        addButton.Text = "Add files...";
        addButton.Size = new Size(110, 28);
        addButton.Location = new Point(518, 602);
        addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        addButton.Click += delegate { PickFiles(); };

        fileBar.Maximum = totalBar.Maximum = 1000;
        Controls.AddRange(new Control[] { connectButton, ipButton, connLabel, dest, pathBox, places, upButton, hiddenBox,
            refreshButton, newFolderButton, renameButton, deleteButton, downloadButton, split, fileBar, totalBar, status, via, addButton });
        Shown += delegate { Connect(null, false); };

        DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (s, e) => Enqueue((string[])e.Data.GetData(DataFormats.FileDrop));
        FormClosing += (s, e) =>
        {
            if (busy && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "A copy is still running. Quit anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
            else if (busy) StopAndCleanUp();
        };
    }

    void PickFiles()
    {
        using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Pick videos or pictures",
            Filter = "Videos and pictures|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.ts;*.jpg;*.jpeg;*.png;*.webp;*.heic|All files|*.*" })
        {
            if (dlg.ShowDialog(this) == DialogResult.OK) Enqueue(dlg.FileNames);
        }
    }

    // One file to copy: PC -> headset (Dir: the headset folder), or with
    // Down set, headset -> PC (Remote: the headset file, Local: where it goes).
    class Job
    {
        public bool Down;
        public string Local, Dir, Remote, Label;
        public long Size;
    }

    void Enqueue(string[] paths)
    {
        string dir, why;
        if ((dir = Resolve(pathBox.Text, out why)) == null)
        {
            MessageBox.Show(this, why, Text);
            return;
        }
        var jobs = new List<Job>();
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue; // folders are skipped
            jobs.Add(new Job { Local = p, Dir = dir, Label = Path.GetFileName(p), Size = new FileInfo(p).Length });
        }
        if (Enqueue(jobs) == 0)
        {
            status.Text = "Nothing to copy (folders are skipped; drop the files themselves).";
            if (opt.Exit && !busy) Close();
        }
    }

    int Enqueue(List<Job> jobs)
    {
        lock (queueLock)
        {
            if (!busy) { doneCount = failCount = skipCount = 0; batchBytes = batchSent = 0; }
            foreach (var j in jobs)
            {
                queue.Enqueue(j);
                batchBytes += j.Size;
                list.Items.Add("waiting   " + j.Label);
            }
            if (jobs.Count > 0 && !busy)
            {
                busy = true;
                new Thread(Worker) { IsBackground = true }.Start();
            }
        }
        if (jobs.Count > 0) list.TopIndex = Math.Max(0, list.Items.Count - 1);
        return jobs.Count;
    }

    void Worker()
    {
        bool toVideos = false;
        while (true)
        {
            Job job;
            lock (queueLock)
            {
                if (queue.Count == 0) { busy = false; break; }
                job = queue.Dequeue();
            }
            string error = null;
            var watch = Stopwatch.StartNew();
            int used = 1;
            try { error = job.Down ? Download(job) : Copy(job.Local, job.Dir, job.Label, job.Size, out used); }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) doneCount++; else if (error == Skipped) skipCount++; else failCount++;
            if (!job.Down && InVideos(job.Dir)) toVideos = true;
            Log(job.Label, job.Size, watch.Elapsed.TotalSeconds, used, error);
            string label = job.Label;
            UI(delegate { MarkItem(label, error == null ? "copied    " : error == Skipped ? "skipped   " : "FAILED    "); });
            if (error != null && error != Skipped) UI(delegate { list.Items.Add("    " + error); });
        }
        UI(delegate
        {
            fileBar.Value = 0;
            totalBar.Value = failCount == 0 ? 1000 : totalBar.Value;
            string skipped = skipCount > 0 ? " " + skipCount + " already on the PC, left as they were." : "";
            status.Text = failCount == 0
                ? "Done: " + doneCount + " file(s) copied." + skipped + (toVideos ? " They show up in FrameCinema's Library." : "")
                : "Finished with errors: " + doneCount + " copied, " + failCount + " failed (see list)." + skipped;
            if (opt.Exit) { Close(); return; }
            ListFolder();
            (failCount == 0 ? SystemSounds.Asterisk : SystemSounds.Hand).Play();
        });
    }

    void Log(string name, long size, double seconds, int used, string error)
    {
        if (opt.Log == null) return;
        string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}\t{1}\t{2}\t{3:0.00}s\t{4:0.0} MB/s\t{5} stream(s)\t{6}\t{7}\r\n",
            DateTime.Now, name, size, seconds, size / 1048576.0 / Math.Max(seconds, 0.001), used,
            gitSsh ? "git-ssh" : "windows-ssh", error == null ? "ok" : "FAILED " + error);
        try { File.AppendAllText(opt.Log, line, Encoding.UTF8); } catch (IOException) { }
    }

    // A file is written to a hidden .part name first and renamed when it is
    // whole, so FrameCinema never sees a half-copied file. Small files stream
    // into "cat"; big ones go as `used` byte ranges at once, each into "dd"
    // at its offset of a .part made at full size up front.
    string Copy(string path, string headsetDir, string name, long size, out int used)
    {
        string dir = ShellPath(headsetDir);
        string part = dir + "/" + Sh("." + name + ".part");
        string final = dir + "/" + Sh(name);
        currentPart = part;

        int n =size < ParallelMin ? 1 : (int)Math.Max(1, Math.Min(streams, size / RangeMin));
        used = n;
        if (n == 1)
            return Transfer(path, name, size, new[] { new Range(0, size,
                "mkdir -p " + dir + " && cat > " + part + " && mv -f " + part + " " + final) });

        // fallocate fails up front when the file can't fit; truncate covers
        // file systems without it (a full disk then fails in dd instead).
        string error = RunRemote("mkdir -p " + dir + " && rm -f " + part + " && { fallocate -l " + size + " "
            + part + " || truncate -s " + size + " " + part + "; }");
        if (error != null) return error;

        // Ranges are whole MiB (dd's seek is in bytes here, so this is only
        // for tidy reads); the last one takes the rest.
        long step = ((size + n - 1) / n + 1048575) & ~1048575L;
        var ranges = new List<Range>();
        for (long off = 0; off < size; off += step)
        {
            long len = Math.Min(step, size - off);
            ranges.Add(new Range(off, len, "dd of=" + part + " bs=4M seek=" + off
                + " oflag=seek_bytes conv=notrunc iflag=fullblock status=none"));
        }
        used = ranges.Count;
        error = Transfer(path, name, size, ranges.ToArray());
        if (error == null)
            error = RunRemote("s=$(stat -c %s " + part + ") && [ \"$s\" = " + size + " ] && mv -f " + part + " " + final
                + " || { echo \"the copy on the headset is $s bytes, not " + size + "\" >&2; exit 1; }");
        if (error != null) RunRemote("rm -f " + part);
        return error;
    }

    class Range
    {
        public readonly long Offset, Length;
        public readonly string Remote;
        public Process Proc;
        public string Error;
        public Range(long offset, long length, string remote) { Offset = offset; Length = length; Remote = remote; }
    }

    // Sends each range of the file into its own ssh process at once, showing
    // the progress of them all; the first failure stops the others.
    string Transfer(string path, string name, long size, Range[] ranges)
    {
        long sent = 0;
        int failed = 0;
        string firstError = null; // the others fail because they were stopped
        var threads = new List<Thread>();
        foreach (var r in ranges)
        {
            var range = r;
            var t = new Thread(delegate ()
            {
                range.Error = SendRange(path, range, delegate (int k)
                {
                    Interlocked.Add(ref sent, k);
                    Interlocked.Add(ref batchSent, k);
                    return Thread.VolatileRead(ref failed) == 0;
                });
                if (range.Error != null && Interlocked.Exchange(ref failed, 1) == 0)
                {
                    firstError = range.Error;
                    foreach (var other in ranges)
                        if (other != range) Kill(other.Proc);
                }
            }) { IsBackground = true };
            threads.Add(t);
            t.Start();
        }

        var watch = Stopwatch.StartNew();
        while (true)
        {
            bool alive = false;
            foreach (var t in threads) if (t.IsAlive) { alive = true; break; }
            ShowProgress(name, Interlocked.Read(ref sent), size, watch.Elapsed.TotalSeconds, ranges.Length);
            if (!alive) break;
            Thread.Sleep(200);
        }

        return firstError;
    }

    const string Stopped = "stopped";

    // Streams one byte range of the file into an ssh process's stdin.
    // `progress` gets each block sent and returns false to stop.
    string SendRange(string path, Range r, Func<int, bool> progress)
    {
        var stderr = new StringBuilder();
        long sent = 0;
        using (var proc = StartSsh(r.Remote, stderr))
        {
            r.Proc = proc;
            var buf = new byte[1 << 20];
            bool stopped = false;
            try
            {
                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var dst = proc.StandardInput.BaseStream)
                {
                    src.Seek(r.Offset, SeekOrigin.Begin);
                    while (sent < r.Length)
                    {
                        int n = src.Read(buf, 0, (int)Math.Min(buf.Length, r.Length - sent));
                        if (n <= 0) break;
                        dst.Write(buf, 0, n);
                        sent += n;
                        if (!progress(n)) { stopped = true; break; }
                    }
                }
            }
            catch (IOException)
            {
                // ssh exited early (bad host, auth, disk full); its stderr says why.
            }
            if (stopped) { Kill(proc); proc.WaitForExit(); return Stopped; }
            proc.WaitForExit(); // also waits for the async stderr reader to drain
            if (proc.ExitCode != 0 || sent != r.Length)
            {
                string why;
                lock (stderr) why = stderr.ToString().Trim();
                return why.Length > 0 ? why.Replace("\r", "").Replace("\n", " | ") : "ssh exit code " + proc.ExitCode;
            }
        }
        return null;
    }

    const string Skipped = "skipped";

    // Copies one headset file to the PC: "cat" over ssh into a .part next to
    // the target, renamed when the size matches. A file already on the PC is
    // left alone (the job is marked skipped).
    string Download(Job job)
    {
        if (File.Exists(job.Local) || Directory.Exists(job.Local)) return Skipped;
        Directory.CreateDirectory(Path.GetDirectoryName(job.Local));
        string part = job.Local + ".part";
        currentLocalPart = part;
        var stderr = new StringBuilder();
        long got = 0;
        bool ok = false;
        try
        {
            using (var proc = StartSsh("cat -- " + ShellPath(job.Remote), stderr, null, hostName, 10, true))
            {
                proc.StandardInput.Close();
                var buf = new byte[1 << 20];
                var watch = Stopwatch.StartNew();
                long shown = -1;
                using (var src = proc.StandardOutput.BaseStream)
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        got += n;
                        Interlocked.Add(ref batchSent, n);
                        long ms = watch.ElapsedMilliseconds;
                        if (ms - shown >= 200) { shown = ms; ShowProgress(job.Label, got, job.Size, watch.Elapsed.TotalSeconds, 1); }
                    }
                }
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    string why;
                    lock (stderr) why = stderr.ToString().Trim();
                    return why.Length > 0 ? why.Replace("\r", "").Replace("\n", " | ") : "ssh exit code " + proc.ExitCode;
                }
            }
            // The size listed when it was queued: a file still growing on
            // the headset (a copy under way) doesn't count as whole.
            if (got != job.Size) return "got " + got + " bytes, the headset listed " + job.Size;
            File.Move(part, job.Local);
            ok = true;
            return null;
        }
        finally
        {
            currentLocalPart = null;
            if (!ok) try { File.Delete(part); } catch (Exception) { }
        }
    }

    void ShowProgress(string name, long s, long size, double seconds, int ranges)
    {
        long b = Interlocked.Read(ref batchSent);
        double mbps = s / 1048576.0 / Math.Max(seconds, 0.001);
        int f = size > 0 ? (int)Math.Min(1000, s * 1000 / size) : 1000;
        int tt = batchBytes > 0 ? (int)Math.Min(1000, b * 1000 / batchBytes) : 1000;
        string msg = string.Format("Copying {0}  {1} / {2}  ({3:0.0} MB/s{4})",
            name, Mb(s), Mb(size), mbps, ranges > 1 ? ", " + ranges + " streams" : "");
        UI(delegate { fileBar.Value = f; totalBar.Value = tt; status.Text = msg; });
    }

    // Runs a command on the headset with no input; null when it succeeds.
    string RunRemote(string remote)
    {
        string output;
        return RunRemote(remote, hostName, 10, out output);
    }

    // Same, keeping what the command prints, against `host` (null: the
    // "frame" entry as it is) with a connect timeout in seconds.
    string RunRemote(string remote, string host, int timeout, out string output)
    {
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        using (var proc = StartSsh(remote, stderr, stdout, host, timeout))
        {
            proc.StandardInput.Close();
            proc.WaitForExit();
            lock (stdout) output = stdout.ToString();
            if (proc.ExitCode == 0) return null;
            string why;
            lock (stderr) why = stderr.ToString().Trim();
            return why.Length > 0 ? why.Replace("\r", "").Replace("\n", " | ") : "ssh exit code " + proc.ExitCode;
        }
    }

    Process StartSsh(string remote, StringBuilder stderr)
    {
        return StartSsh(remote, stderr, null, hostName, 10);
    }

    Process StartSsh(string remote, StringBuilder stderr, StringBuilder stdout, string host, int timeout)
    {
        return StartSsh(remote, stderr, stdout, host, timeout, false);
    }

    // `rawOut`: stdout is left for the caller to read as bytes (a download).
    Process StartSsh(string remote, StringBuilder stderr, StringBuilder stdout, string host, int timeout, bool rawOut)
    {
        // AES-GCM first: both ends have AES instructions, and it beat
        // chacha20 on the Frame (55 vs 50 MB/s on one stream). A HostName
        // from Connect replaces only the entry's address; accept-new lets a
        // new IP of the headset in but still refuses a changed host key.
        var psi = new ProcessStartInfo(sshExe,
            "-o BatchMode=yes -o ConnectTimeout=" + timeout + " -o StrictHostKeyChecking=accept-new"
            + (host != null ? " -o HostName=" + host : "")
            + " -c aes128-gcm@openssh.com,chacha20-poly1305@openssh.com "
            + opt.Host + " " + WinArg(remote))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Git's ssh finds ~/.ssh (config, keys, known_hosts) through HOME,
        // which is unset outside Git Bash.
        if (gitSsh && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME")))
            psi.EnvironmentVariables["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Dropped from `live` when it exits (not by asking HasExited, which
        // throws once the process object is disposed).
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += delegate { lock (live) live.Remove(proc); };
        lock (live) live.Add(proc);
        proc.Start();
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.OutputDataReceived += (s, e) => { if (e.Data != null && stdout != null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        if (!rawOut) proc.BeginOutputReadLine();
        return proc;
    }

    // Quit mid-copy: stops the ssh processes and removes the half-written
    // .part from the headset (it is hidden, but it takes space).
    void StopAndCleanUp()
    {
        lock (queueLock) queue.Clear();
        lock (live) { foreach (var p in live) Kill(p); live.Clear(); }
        string local = currentLocalPart;
        if (local != null) try { File.Delete(local); } catch (Exception) { } // may still be open; best effort
        string part = currentPart;
        if (part == null) return;
        try
        {
            var stderr = new StringBuilder();
            using (var proc = StartSsh("rm -f " + part, stderr))
            {
                proc.StandardInput.Close();
                proc.WaitForExit(5000);
            }
        }
        catch (Exception) { } // best effort: quitting anyway
    }

    static void Kill(Process p)
    {
        try { if (p != null && !p.HasExited) p.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    // Single-quote for the remote POSIX shell.
    static string Sh(string s) { return "'" + s.Replace("'", "'\\''") + "'"; }

    // Quotes one argument for the Windows command line (the C runtime's
    // rules, which both ssh builds follow), so the remote command reaches
    // ssh whole, double quotes and runs of spaces included.
    static string WinArg(string s)
    {
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1);
            else sb.Append('\\', slashes);
            sb.Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    static string Mb(long b) { return (b / 1048576.0).ToString("0.0") + " MB"; }

    // --ssh=PATH, else Git for Windows' ssh (OpenSSH 10, ~4x Windows' own
    // on this link), else Windows' built-in one.
    static string FindSsh(string forced, out bool git)
    {
        git = false;
        if (!string.IsNullOrEmpty(forced))
        {
            git = forced.IndexOf(@"\usr\bin\", StringComparison.OrdinalIgnoreCase) >= 0;
            return forced;
        }
        foreach (var root in new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        {
            string p = Path.Combine(root, @"Git\usr\bin\ssh.exe");
            if (File.Exists(p)) { git = true; return p; }
        }
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string w = Path.Combine(win, @"System32\OpenSSH\ssh.exe");
        if (File.Exists(w)) return w;
        w = Path.Combine(win, @"Sysnative\OpenSSH\ssh.exe");
        return File.Exists(w) ? w : "ssh.exe";
    }

    // ---- Connect to Frame ----

    static string SavedIpFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"FrameUpload\host.txt"); }
    }

    static string LoadSavedIp()
    {
        try { return File.ReadAllText(SavedIpFile).Trim(); } catch (Exception) { return ""; }
    }

    static void SaveIp(string ip)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SavedIpFile)); File.WriteAllText(SavedIpFile, ip); }
        catch (Exception) { } // only a convenience for next time
    }

    // Host names and IPs only: it goes on ssh's command line.
    static bool ValidHost(string h)
    {
        if (h.Length == 0 || h.Length > 253 || h[0] == '-') return false;
        foreach (char c in h)
            if (!(c < 128 && char.IsLetterOrDigit(c)) && c != '.' && c != '-' && c != ':' && c != '_') return false;
        return true;
    }

    // Tries `only` when given, else the "frame" entry as it is, the last IP
    // that worked, then frame.local; `ask` offers to type an IP when none
    // answers. The headset's own address (from $SSH_CONNECTION) is saved
    // whichever way it connected, so the next fallback is fresh.
    void Connect(string only, bool ask)
    {
        if (Interlocked.Exchange(ref connecting, 1) == 1) return;
        connectButton.Enabled = ipButton.Enabled = false;
        var tries = new List<string>();
        if (only != null) tries.Add(only);
        else
        {
            tries.Add(null);
            string saved = LoadSavedIp();
            if (ValidHost(saved)) tries.Add(saved);
            tries.Add("frame.local");
        }
        new Thread(delegate ()
        {
            string firstError = null, ip = null, worked = null, dir = null;
            bool ok = false;
            foreach (var h in tries)
            {
                string name = h ?? opt.Host;
                UI(delegate { SetConn("Trying " + name + "...", SystemColors.GrayText); });
                string output;
                string error = RunRemote("echo $SSH_CONNECTION; echo \"$HOME\"", h, 5, out output);
                if (error == null)
                {
                    ok = true;
                    worked = h;
                    var lines = output.Replace("\r", "").Split('\n');
                    var f = lines[0].Trim().Split(' ');
                    if (f.Length >= 3 && ValidHost(f[2])) ip = f[2];
                    if (lines.Length > 1 && lines[1].StartsWith("/") && lines[1].Length > 1) dir = lines[1].TrimEnd('/');
                    break;
                }
                if (firstError == null) firstError = name + ": " + error;
            }
            UI(delegate
            {
                Interlocked.Exchange(ref connecting, 0);
                connectButton.Enabled = ipButton.Enabled = true;
                if (ok)
                {
                    hostName = worked;
                    if (dir != null) home = dir;
                    if (ip != null) SaveIp(ip);
                    string name = worked ?? opt.Host;
                    SetConn("Connected to " + name + (ip != null && ip != name ? " (" + ip + ")" : ""), Color.ForestGreen);
                    ListFolder();
                }
                else
                {
                    SetConn("Not connected. " + firstError, Color.Firebrick);
                    if (ask) AskForIp(firstError);
                }
            });
        }) { IsBackground = true }.Start();
    }

    void AskForIp(string why)
    {
        if (why != null && why.Length > 200) why = why.Substring(0, 200) + "...";
        string ip = Prompt("Frame IP address", (why != null ? "Couldn't reach the Frame (" + why + ").\n\n" : "")
            + "The headset's IP address (your router lists it). It is saved for next time once it connects.",
            LoadSavedIp());
        if (ip == null) return;
        ip = ip.Trim();
        if (!ValidHost(ip)) { MessageBox.Show(this, "\"" + ip + "\" isn't an IP address or host name.", Text); return; }
        Connect(ip, true);
    }

    void SetConn(string text, Color color)
    {
        connLabel.Text = text;
        connLabel.ForeColor = color;
        tips.SetToolTip(connLabel, text);
    }

    string Prompt(string title, string text, string value)
    {
        return Prompt(title, text, value, -1);
    }

    // `selectLength` >= 0 selects that much of `value` from the start (a
    // file's name without its extension), else all of it.
    string Prompt(string title, string text, string value, int selectLength)
    {
        using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false,
            MaximizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(400, 170), Font = Font })
        {
            var label = new Label { Text = text, Location = new Point(12, 12), Size = new Size(376, 84) };
            var box = new TextBox { Text = value ?? "", Location = new Point(12, 100), Width = 376 };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(232, 134), Size = new Size(75, 26) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(313, 134), Size = new Size(75, 26) };
            f.Controls.AddRange(new Control[] { label, box, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            if (selectLength >= 0) f.Shown += delegate { box.Select(0, Math.Min(selectLength, box.Text.Length)); };
            return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
        }
    }

    // ---- The headset's folders ----

    class Entry
    {
        public string Name;
        public bool Dir;
        public long Size = -1;   // a folder's is -1 when du didn't report it
        public double Time;
    }

    // Folders stay above files; within each, by the clicked column.
    class EntrySorter : System.Collections.IComparer
    {
        public int Column;
        public bool Descending;
        public int Compare(object x, object y)
        {
            var a = ((ListViewItem)x).Tag as Entry;
            var b = ((ListViewItem)y).Tag as Entry;
            if (a == null || b == null) return 0;
            if (a.Dir != b.Dir) return a.Dir ? -1 : 1;
            int c = Column == 1 ? a.Size.CompareTo(b.Size)
                : Column == 2 ? a.Time.CompareTo(b.Time)
                : 0;
            if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return Descending ? -c : c;
        }
    }

    readonly EntrySorter sorter = new EntrySorter();
    readonly string[] columnTitles = { "", "Size", "Modified" };

    // A click sorts by that column; a second click on it flips the order.
    // Size and date start biggest/newest first.
    void SortBy(int column)
    {
        if (sorter.Column == column) sorter.Descending = !sorter.Descending;
        else
        {
            sorter.Column = column;
            sorter.Descending = column != 0;
        }
        remote.Sort();
        ShowSortArrow();
    }

    void ShowSortArrow()
    {
        for (int i = 0; i < remote.Columns.Count; i++)
            remote.Columns[i].Text = columnTitles[i] + (i == sorter.Column ? (sorter.Descending ? "  ▼" : "  ▲") : "");
    }

    // Headset paths are kept as ~ or ~/... (under the home folder) or /...;
    // ShellPath makes one a quoted word for the remote shell.
    static string ShellPath(string p)
    {
        if (p == "~") return "\"$HOME\"";
        if (p.StartsWith("~/")) return "\"$HOME\"/" + Sh(p.Substring(2));
        return Sh(p);
    }

    static string Join(string dir, string name) { return dir == "/" ? "/" + name : dir + "/" + name; }

    static bool InVideos(string p) { return p == "~/Videos" || p.StartsWith("~/Videos/"); }

    // Where delete and rename work: the home folder, and SD cards and USB
    // drives (SteamOS mounts them under /run/media). Elsewhere the system's
    // own files are read-only to this user anyway, or not ours to remove.
    static bool MayChange(string dir) { return dir == "~" || dir.StartsWith("~/") || dir.StartsWith("/run/media/"); }

    const string MayChangeText = "Delete and rename work in your home folder (~) and on SD cards and USB drives (/run/media).";

    // An absolute path, shown as ~/... when it is under the home folder.
    string Tidy(string abs)
    {
        string h = home;
        if (h != null && (abs == h || abs.StartsWith(h + "/"))) return "~" + abs.Substring(h.Length);
        return abs;
    }

    // The folder that holds `p`, or null at the top (or above ~ before the
    // home folder is known).
    string ParentOf(string p)
    {
        if (p == "/") return null;
        if (p == "~")
        {
            string h = home;
            return h == null ? null : Tidy(h.Substring(0, Math.Max(1, h.LastIndexOf('/'))));
        }
        int slash = p.LastIndexOf('/');
        return slash <= 0 ? "/" : slash == 1 && p[0] == '~' ? "~" : p.Substring(0, slash);
    }

    // Reads a typed path: ~/..., /..., or a path inside the folder on show;
    // "." and ".." are worked out here. Null (and why) when it can't be used.
    string Resolve(string text, out string why)
    {
        why = null;
        string t = text.Trim().Replace('\\', '/');
        if (t.Length == 0) t = "~";
        bool fromHome;
        if (t == "~" || t.StartsWith("~/")) { fromHome = true; t = t.Substring(1); }
        else if (t.StartsWith("~")) { why = "Only your own home folder can be written with ~ (as ~ or ~/...)."; return null; }
        else if (t.StartsWith("/")) fromHome = false;
        else
        {
            string here = listedPath ?? "~";
            fromHome = here[0] == '~';
            t = (fromHome ? here.Substring(1) : here) + "/" + t;
        }
        var parts = new List<string>();
        foreach (var s in t.Split('/'))
        {
            if (s.Length == 0 || s == ".") continue;
            if (s != "..") { parts.Add(s); continue; }
            if (parts.Count > 0) { parts.RemoveAt(parts.Count - 1); continue; }
            if (!fromHome) continue; // / has no parent
            string h = home;
            if (h == null) { why = "Connect first: going above ~ needs the headset's home folder."; return null; }
            fromHome = false;
            parts.AddRange(h.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        }
        string joined = string.Join("/", parts.ToArray());
        return fromHome ? (joined.Length > 0 ? "~/" + joined : "~") : Tidy("/" + joined);
    }

    // The folder in the box: the one on show when it wasn't edited (so a
    // name with a backslash survives), else what was typed.
    string TargetDir(out string why)
    {
        why = null;
        if (listedPath != null && pathBox.Text == listedPath) return listedPath;
        return Resolve(pathBox.Text, out why);
    }

    // The folder on show, else the one typed in the box, else ~.
    string CurrentDir()
    {
        string why;
        return listedPath ?? Resolve(pathBox.Text, out why) ?? "~";
    }

    void ListFolder()
    {
        string why, p = TargetDir(out why);
        if (p == null) { MessageBox.Show(this, why, Text); return; }
        ListPath(p);
    }

    // Lists a headset folder (hidden names only when Show hidden is ticked,
    // and never the .part of a copy under way), whether it can be written
    // to, and the free space there.
    void ListPath(string p)
    {
        int seq = ++listSeq;
        string host = hostName;
        string hide = hiddenBox.Checked ? "! -name '.*.part'" : "! -name '.*'";
        string cmd = "d=" + ShellPath(p) + "; [ -d \"$d\" ] || { echo MISSING; exit 0; }; cd \"$d\" || { echo DENIED; exit 0; }"
            + "; df -B1 --output=avail . 2>/dev/null | tail -n 1 | tr -d ' '; [ -w . ] && echo RW || echo RO"
            + "; find . -mindepth 1 -maxdepth 1 " + hide + " -printf '%Y\\t%s\\t%T@\\t%f\\n' 2>/dev/null"
            + "; echo SIZES";
        // Folder sizes in the home folder and on drives: du reads only
        // metadata, but a big tree could take a while, so each folder gets
        // 3 s and all of them 4 s; the rest show as "folder".
        if (MayChange(p))
            cmd += "; find . -mindepth 1 -maxdepth 1 -type d " + hide + " -exec sh -c"
                + " 'end=$(($(date +%s)+4)); for x; do [ \"$(date +%s)\" -lt \"$end\" ] || break;"
                + " timeout 3 du -s --apparent-size -B1 -- \"$x\" 2>/dev/null; done' sh {} +";
        cmd += "; true";
        remoteInfo.Text = "Listing " + p + "...";
        new Thread(delegate ()
        {
            string output;
            string error = RunRemote(cmd, host, 5, out output);
            UI(delegate { if (seq == listSeq) ShowListing(p, error, output); });
        }) { IsBackground = true }.Start();
    }

    void ShowListing(string p, string error, string output)
    {
        listedPath = p;
        pathBox.Text = p;
        upButton.Enabled = ParentOf(p) != null;
        renameButton.Enabled = deleteButton.Enabled = MayChange(p);
        remote.BeginUpdate();
        remote.Items.Clear();
        columnTitles[0] = p;
        ShowSortArrow();
        string first = (output ?? "").Trim();
        if (error != null)
        {
            remote.Items.Add(new ListViewItem("Can't list it: " + error) { ForeColor = Color.Firebrick });
            remoteInfo.Text = "Not connected? Try Connect to Frame.";
        }
        else if (first == "MISSING")
        {
            remote.Items.Add(new ListViewItem("(no such folder yet: copying here makes it)") { ForeColor = SystemColors.GrayText });
            remoteInfo.Text = "";
        }
        else if (first == "DENIED")
        {
            remote.Items.Add(new ListViewItem("(this folder can't be opened: no permission)") { ForeColor = SystemColors.GrayText });
            remoteInfo.Text = "";
        }
        else
        {
            var lines = output.Replace("\r", "").Split('\n');
            var folders = new List<ListViewItem>();
            var files = new List<ListViewItem>();
            long total = 0, free;
            // du's "bytes<TAB>./name" lines after the SIZES marker.
            var dirSizes = new Dictionary<string, long>();
            int mark = Array.IndexOf(lines, "SIZES");
            if (mark < 0) mark = lines.Length;
            for (int i = mark + 1; i < lines.Length; i++)
            {
                var d = lines[i].Split(new[] { '\t' }, 2);
                long n;
                if (d.Length == 2 && d[1].StartsWith("./") && long.TryParse(d[0], out n)) dirSizes[d[1].Substring(2)] = n;
            }
            // Line 0 is the free space, line 1 RW or RO, then the entries.
            for (int i = 2; i < mark; i++)
            {
                var f = lines[i].Split(new[] { '\t' }, 4);
                if (f.Length < 4 || f[3].Length == 0) continue;
                bool dir = f[0] == "d";
                long size;
                double t;
                long.TryParse(f[1], out size);
                double.TryParse(f[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out t);
                if (dir && !dirSizes.TryGetValue(f[3], out size)) size = -1;
                var item = new ListViewItem(dir ? f[3] + "/" : f[3]);
                item.SubItems.Add(size < 0 ? "folder" : Human(size));
                item.SubItems.Add(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                item.Tag = new Entry { Name = f[3], Dir = dir, Size = size, Time = t };
                if (f[3][0] == '.') item.ForeColor = SystemColors.GrayText;
                (dir ? folders : files).Add(item);
                if (size > 0) total += size;
            }
            remote.Items.AddRange(folders.ToArray());
            remote.Items.AddRange(files.ToArray());
            if (remote.Items.Count == 0)
                remote.Items.Add(new ListViewItem("(empty)") { ForeColor = SystemColors.GrayText });
            if (selectAfter != null)
                foreach (ListViewItem item in remote.Items)
                {
                    var e = item.Tag as Entry;
                    if (e != null && e.Name == selectAfter) { item.Selected = item.Focused = true; item.EnsureVisible(); break; }
                }
            remoteInfo.Text = (folders.Count > 0 ? folders.Count + " folder(s), " : "") + files.Count + " file(s), " + Human(total)
                + (long.TryParse(lines[0].Trim(), out free) ? "   |   " + Human(free) + " free there" : "")
                + (lines.Length > 1 && lines[1] == "RO" ? "   |   read-only" : "");
        }
        remote.EndUpdate();
        selectAfter = null;
    }

    void OpenSelected()
    {
        if (remote.SelectedItems.Count != 1) return;
        var e = remote.SelectedItems[0].Tag as Entry;
        if (e == null || !e.Dir) return;
        ListPath(Join(CurrentDir(), e.Name));
    }

    // Goes to the folder above, with the one just left selected.
    void GoUp()
    {
        string here = CurrentDir(), up = ParentOf(here);
        if (up == null) return;
        selectAfter = here == "~" ? home.Substring(home.LastIndexOf('/') + 1) : here.Substring(here.LastIndexOf('/') + 1);
        ListPath(up);
    }

    void GoToPlace()
    {
        int i = places.SelectedIndex;
        if (i <= 0) return;
        places.SelectedIndex = 0; // back to "Places...", so the same place can be picked again
        ListPath(new[] { null, "~/Videos", "~", "~/Downloads", "/run/media", "/" }[i]);
    }

    List<Entry> SelectedEntries()
    {
        var entries = new List<Entry>();
        foreach (ListViewItem item in remote.SelectedItems)
        {
            var e = item.Tag as Entry;
            if (e != null && e.Name != "." && e.Name != ".." && e.Name.IndexOf('/') < 0) entries.Add(e);
        }
        return entries;
    }

    void NewFolder()
    {
        string dir = CurrentDir();
        string name = Prompt("New folder", "Make a folder in " + dir + " named:", "");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            MessageBox.Show(this, "A folder name can't be empty, \".\" or \"..\", or contain / or \\.", Text);
            return;
        }
        string target = Join(dir, name);
        RunThenRelist("mkdir -p " + ShellPath(target), target);
    }

    // Hidden items right in ~ hold the headset's own settings, the ssh key
    // this tool connects with among them.
    bool Protected(string dir, List<Entry> entries)
    {
        if (!MayChange(dir)) { MessageBox.Show(this, MayChangeText, Text); return true; }
        if (dir != "~") return false;
        foreach (var e in entries)
            if (e.Name[0] == '.')
            {
                MessageBox.Show(this, "Hidden items in ~ (like " + e.Name + ") hold the headset's own settings, "
                    + "and the ssh key this tool uses, so they can't be changed from here.", Text);
                return true;
            }
        return false;
    }

    // Asks first: names the items, says folders go with everything in them.
    void DeleteSelected()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0) return;
        string dir = CurrentDir();
        if (Protected(dir, entries)) return;
        var names = new StringBuilder();
        bool anyDir = false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Dir) anyDir = true;
            if (i < 12) names.Append("    ").Append(entries[i].Name).Append(entries[i].Dir ? "/" : "").Append('\n');
        }
        if (entries.Count > 12) names.Append("    ...and ").Append(entries.Count - 12).Append(" more\n");
        if (MessageBox.Show(this, "Delete " + (entries.Count == 1 ? "this" : "these " + entries.Count) + " from the headset's "
                + dir + "?\n\n" + names
                + (anyDir ? "\nFolders are deleted with everything in them." : "")
                + (InVideos(dir) ? "" : "\nThis is outside ~/Videos: be sure nothing on the headset needs it.")
                + "\nThis can't be undone.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var cmd = new StringBuilder("cd " + ShellPath(dir) + " && rm -rf --");
        foreach (var e in entries) cmd.Append(' ').Append(Sh(e.Name));
        RunThenRelist(cmd.ToString(), dir);
    }

    // Renames one file or folder where it is. Refuses a name that is
    // taken (mv -n, after a check that also catches a folder of that name,
    // which mv would move the item into). In ~/Videos it asks before an
    // extension change and refuses a hidden name, since FrameCinema goes by
    // both.
    void RenameSelected()
    {
        if (remote.SelectedItems.Count != 1) return;
        var e = remote.SelectedItems[0].Tag as Entry;
        if (e == null || e.Name == "." || e.Name == ".." || e.Name.IndexOf('/') >= 0) return;
        string dir = CurrentDir();
        if (Protected(dir, new List<Entry> { e })) return;
        if (busy && e.Dir)
        {
            MessageBox.Show(this, "Copies are under way. Rename folders once they finish.", Text);
            return;
        }
        bool videos = InVideos(dir);
        string oldExt = e.Dir ? "" : Path.GetExtension(e.Name);
        string name = Prompt("Rename", "Rename " + (e.Dir ? "the folder " : "") + "\"" + e.Name + "\" in "
            + dir + " to:", e.Name, e.Name.Length - oldExt.Length);
        if (name == null) return;
        name = name.Trim();
        if (name == e.Name) return;
        if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            MessageBox.Show(this, "A name can't be empty, \".\" or \"..\", or contain / or \\.", Text);
            return;
        }
        if (videos && name[0] == '.')
        {
            MessageBox.Show(this, "A name starting with \".\" would hide it, here and in FrameCinema's Library.", Text);
            return;
        }
        if (Encoding.UTF8.GetByteCount(name) > 255)
        {
            MessageBox.Show(this, "That name is too long for the headset (255 bytes at most).", Text);
            return;
        }
        string newExt = e.Dir ? "" : Path.GetExtension(name);
        if (videos && !string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, (newExt.Length == 0 ? "Drop the extension " + oldExt : "Change the extension from "
                    + (oldExt.Length > 0 ? oldExt : "(none)") + " to " + newExt)
                + "?\n\nFrameCinema goes by the extension, so the file may not play or show up.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        selectAfter = name;
        RunThenRelist(RenameCommand(dir, e.Name, name), dir);
    }

    static string RenameCommand(string dir, string from, string to)
    {
        return "cd " + ShellPath(dir) + " || exit 1; o=" + Sh(from) + "; n=" + Sh(to) + ";"
            + " [ -e \"$o\" ] || [ -L \"$o\" ] || { echo \"$o is gone (renamed or deleted elsewhere?)\" >&2; exit 1; };"
            + " if [ -e \"$n\" ] || [ -L \"$n\" ]; then echo \"$n already exists here\" >&2; exit 1; fi;"
            + " mv -n -T -- \"$o\" \"$n\" && [ -e \"$n\" ] && ! [ -e \"$o\" ]"
            + " || { echo \"couldn't rename it\" >&2; exit 1; }";
    }

    void RunThenRelist(string cmd, string showPath)
    {
        string host = hostName;
        new Thread(delegate ()
        {
            string output;
            string error = RunRemote(cmd, host, 5, out output);
            UI(delegate
            {
                if (error != null) MessageBox.Show(this, "That didn't work on the headset:\n\n" + error, Text);
                ListPath(error == null ? showPath : CurrentDir());
            });
        }) { IsBackground = true }.Start();
    }

    // ---- Headset -> PC ----

    // Copies the selected files and folders (with everything in them) to a
    // folder on the PC, through the same queue as uploads. Files already
    // there are skipped, not overwritten.
    void DownloadSelected()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0)
        {
            MessageBox.Show(this, "Select the files or folders to copy in the headset view first.", Text);
            return;
        }
        string dir = CurrentDir(), to;
        using (var dlg = new FolderBrowserDialog { ShowNewFolderButton = true,
            Description = "Copy " + (entries.Count == 1 ? "\"" + entries[0].Name + "\"" : entries.Count + " items")
                + " from the headset into this PC folder:" })
        {
            if (lastLocalDir != null) dlg.SelectedPath = lastLocalDir;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            to = lastLocalDir = dlg.SelectedPath;
        }
        DownloadTo(dir, entries, to);
    }

    void DownloadTo(string dir, List<Entry> entries, string to)
    {
        // One find lists every file to copy with its size; -H follows a
        // selected item that is a link (to a file or a folder), and ./ keeps
        // a name starting with - from reading as an option.
        var cmd = new StringBuilder("cd " + ShellPath(dir) + " && find -H");
        foreach (var e in entries) cmd.Append(' ').Append(Sh("./" + e.Name));
        cmd.Append(" -type f ! -name '.*.part' -printf '%s\\t%p\\n'");
        string host = hostName;
        status.Text = "Finding the files to copy...";
        new Thread(delegate ()
        {
            string output;
            string error = RunRemote(cmd.ToString(), host, 5, out output);
            var jobs = new List<Job>();
            var problems = new List<string>();
            if (error != null) problems.Add(error); // e.g. a folder it can't read; the rest still go
            foreach (var line in output.Replace("\r", "").Split('\n'))
            {
                var f = line.Split(new[] { '\t' }, 2);
                long size;
                if (f.Length < 2 || !long.TryParse(f[0], out size)) continue;
                string rel = f[1].StartsWith("./") ? f[1].Substring(2) : f[1];
                string local = LocalPath(to, rel);
                if (local == null) { problems.Add(rel + ": the name can't be used on Windows"); continue; }
                jobs.Add(new Job { Down = true, Remote = Join(dir, rel), Local = local, Size = size, Label = "to PC: " + rel });
            }
            UI(delegate
            {
                foreach (var pr in problems) list.Items.Add("    skipped " + pr);
                if (Enqueue(jobs) == 0) status.Text = problems.Count > 0 ? "Nothing copied (see list)." : "Nothing to copy (no files in there).";
            });
        }) { IsBackground = true }.Start();
    }

    // Where a headset file (path relative to the folder on show) lands
    // under `root`; null when a part of its name isn't allowed on Windows.
    static string LocalPath(string root, string rel)
    {
        var bad = Path.GetInvalidFileNameChars();
        string local = root;
        foreach (var part in rel.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." || part.IndexOfAny(bad) >= 0 || part.EndsWith(".") || part.EndsWith(" ")) return null;
            local = Path.Combine(local, part);
        }
        return local;
    }

    static string Human(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1048576) return (b / 1024.0).ToString("0.0") + " KB";
        if (b < 1L << 30) return (b / 1048576.0).ToString("0.0") + " MB";
        return (b / 1073741824.0).ToString("0.00") + " GB";
    }

    void MarkItem(string name, string tag)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            string s = (string)list.Items[i];
            if (s == "waiting   " + name) { list.Items[i] = tag + name; return; }
        }
    }

    void UI(MethodInvoker a)
    {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }
}
