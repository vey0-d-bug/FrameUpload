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
    public string Host = "frame", Sub = "", Ssh, Log;
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
        // Optional: --host=NAME --sub=FOLDER --streams=N --ssh=PATH\ssh.exe
        // --log=FILE --exit FILE... (files passed in, e.g. by dropping them on
        // the exe icon, start copying right away).
        var o = new Options();
        foreach (var a in args)
        {
            if (a.StartsWith("--host=")) o.Host = a.Substring(7);
            else if (a.StartsWith("--sub=")) o.Sub = a.Substring(6);
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
    readonly TextBox subfolder = new TextBox();
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
    int connecting;      // 1 while a connect runs
    int listSeq;         // drops listings that a newer one overtook
    string listedSub;    // the folder the view shows
    string selectAfter;  // a name to select once the next listing shows
    readonly Queue<string> queue = new Queue<string>();
    readonly object queueLock = new object();
    bool busy;
    volatile string currentPart;   // the .part being written, for a quit mid-copy
    readonly List<Process> live = new List<Process>();
    int doneCount, failCount;
    long batchBytes, batchSent;

    public int FailCount { get { return failCount; } }

    public UploadForm(Options o)
    {
        opt = o;
        sshExe = FindSsh(o.Ssh, out gitSsh);
        // Measured on the Frame over 5 GHz Wi-Fi: Git's ssh 1 stream 40-55
        // MB/s, 4 streams 62, 8 streams 69; Windows' ssh 12-15 per stream.
        streams = o.Streams > 0 ? Math.Min(o.Streams, 16) : (gitSsh ? 6 : 8);
        subfolder.Text = o.Sub;
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

        // The folder the view shows is the one files are copied to.
        var dest = new Label { Text = "Copy to  ~/Videos/", AutoSize = true, Location = new Point(12, 53) };
        subfolder.Location = new Point(118, 50);
        subfolder.Width = 144;
        subfolder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        subfolder.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ListFolder(); } };
        var x = 628;
        foreach (var b in new[] { deleteButton, renameButton, newFolderButton, refreshButton, upButton })
        {
            b.Size = new Size(b == newFolderButton ? 84 : b == renameButton ? 70 : 64, 26);
            x -= b.Width;
            b.Location = new Point(x, 48);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            x -= 4;
        }
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
            Location = new Point(12, 80),
            Size = new Size(616, 424),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            SplitterDistance = 270,
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
        Controls.AddRange(new Control[] { connectButton, ipButton, connLabel, dest, subfolder,
            upButton, refreshButton, newFolderButton, renameButton, deleteButton, split, fileBar, totalBar, status, via, addButton });
        Shown += delegate { Connect(null, false); };

        DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (s, e) => Enqueue((string[])e.Data.GetData(DataFormats.FileDrop));
        FormClosing += (s, e) =>
        {
            if (busy && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "An upload is still running. Quit anyway?", Text,
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

    void Enqueue(string[] paths)
    {
        string sub;
        if (!TryCleanSubfolder(subfolder.Text, out sub))
        {
            MessageBox.Show(this, "The subfolder name can't contain \"..\" or start with ~.", Text);
            return;
        }
        int added = 0;
        lock (queueLock)
        {
            if (!busy) { doneCount = failCount = 0; batchBytes = batchSent = 0; }
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue; // folders are skipped
                queue.Enqueue(sub + "\n" + p);
                batchBytes += new FileInfo(p).Length;
                list.Items.Add("waiting   " + Path.GetFileName(p));
                added++;
            }
            if (added > 0 && !busy)
            {
                busy = true;
                new Thread(Worker) { IsBackground = true }.Start();
            }
        }
        if (added == 0)
        {
            status.Text = "Nothing to copy (folders are skipped; drop the files themselves).";
            if (opt.Exit && !busy) Close();
        }
    }

    static bool TryCleanSubfolder(string text, out string sub)
    {
        sub = text.Trim().Replace('\\', '/').Trim('/');
        if (sub.StartsWith("~")) return false;
        foreach (var part in sub.Split('/'))
            if (part == "..") return false;
        return true;
    }

    void Worker()
    {
        while (true)
        {
            string item;
            lock (queueLock)
            {
                if (queue.Count == 0) { busy = false; break; }
                item = queue.Dequeue();
            }
            int nl = item.IndexOf('\n');
            string sub = item.Substring(0, nl), path = item.Substring(nl + 1);
            string name = Path.GetFileName(path);
            string error = null;
            var watch = Stopwatch.StartNew();
            int used = 1;
            try { error = Copy(path, sub, name, out used); }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) doneCount++; else failCount++;
            Log(name, new FileInfo(path).Length, watch.Elapsed.TotalSeconds, used, error);
            UI(delegate { MarkItem(name, error == null ? "copied    " : "FAILED    "); });
            if (error != null) UI(delegate { list.Items.Add("    " + error); });
        }
        UI(delegate
        {
            fileBar.Value = 0;
            totalBar.Value = failCount == 0 ? 1000 : totalBar.Value;
            status.Text = failCount == 0
                ? "Done: " + doneCount + " file(s) copied. They show up in FrameCinema's Library."
                : "Finished with errors: " + doneCount + " copied, " + failCount + " failed (see list).";
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
    string Copy(string path, string sub, string name, out int used)
    {
        string dir = "$HOME/Videos" + (sub.Length > 0 ? "/" + Sh(sub) : "");
        string part = dir + "/" + Sh("." + name + ".part");
        string final = dir + "/" + Sh(name);
        long size = new FileInfo(path).Length;
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
            long s = Interlocked.Read(ref sent), b = Interlocked.Read(ref batchSent);
            double mbps = s / 1048576.0 / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            int f = size > 0 ? (int)Math.Min(1000, s * 1000 / size) : 1000;
            int tt = batchBytes > 0 ? (int)Math.Min(1000, b * 1000 / batchBytes) : 1000;
            string msg = string.Format("Copying {0}  {1} / {2}  ({3:0.0} MB/s{4})",
                name, Mb(s), Mb(size), mbps, ranges.Length > 1 ? ", " + ranges.Length + " streams" : "");
            UI(delegate { fileBar.Value = f; totalBar.Value = tt; status.Text = msg; });
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
        proc.BeginOutputReadLine();
        return proc;
    }

    // Quit mid-copy: stops the ssh processes and removes the half-written
    // .part from the headset (it is hidden, but it takes space).
    void StopAndCleanUp()
    {
        lock (queueLock) queue.Clear();
        lock (live) { foreach (var p in live) Kill(p); live.Clear(); }
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
            string firstError = null, ip = null, worked = null;
            bool ok = false;
            foreach (var h in tries)
            {
                string name = h ?? opt.Host;
                UI(delegate { SetConn("Trying " + name + "...", SystemColors.GrayText); });
                string output;
                string error = RunRemote("echo $SSH_CONNECTION", h, 5, out output);
                if (error == null)
                {
                    ok = true;
                    worked = h;
                    var f = output.Trim().Split(' ');
                    if (f.Length >= 3 && ValidHost(f[2])) ip = f[2];
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

    // ---- The headset's ~/Videos ----

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

    static string RemoteDir(string sub) { return "$HOME/Videos" + (sub.Length > 0 ? "/" + Sh(sub) : ""); }

    static string Join(string sub, string name) { return sub.Length > 0 ? sub + "/" + name : name; }

    // The folder on show, else the one typed in the box.
    string CurrentSub()
    {
        string sub;
        if (listedSub != null) return listedSub;
        return TryCleanSubfolder(subfolder.Text, out sub) ? sub : "";
    }

    // Lists the folder in the box (hidden names, like the .part files of
    // copies under way, are left out) and the free space on the headset.
    void ListFolder()
    {
        string sub;
        if (!TryCleanSubfolder(subfolder.Text, out sub))
        {
            MessageBox.Show(this, "The subfolder name can't contain \"..\" or start with ~.", Text);
            return;
        }
        int seq = ++listSeq;
        string host = hostName;
        string cmd = "d=" + RemoteDir(sub) + "; [ -d \"$d\" ] || { echo MISSING; exit 0; }; cd \"$d\""
            + " && df -B1 --output=avail . | tail -n 1"
            + " && find . -mindepth 1 -maxdepth 1 ! -name '.*' -printf '%Y\\t%s\\t%T@\\t%f\\n'"
            // Folder sizes: du reads only metadata, well under a second for ~/Videos.
            + " && echo SIZES && { find . -mindepth 1 -maxdepth 1 -type d ! -name '.*' -exec du -s --apparent-size -B1 {} + 2>/dev/null; true; }";
        remoteInfo.Text = "Listing ~/Videos" + (sub.Length > 0 ? "/" + sub : "") + "...";
        new Thread(delegate ()
        {
            string output;
            string error = RunRemote(cmd, host, 5, out output);
            UI(delegate { if (seq == listSeq) ShowListing(sub, error, output); });
        }) { IsBackground = true }.Start();
    }

    void ShowListing(string sub, string error, string output)
    {
        listedSub = sub;
        subfolder.Text = sub;
        upButton.Enabled = sub.Length > 0;
        string where = "~/Videos" + (sub.Length > 0 ? "/" + sub : "");
        remote.BeginUpdate();
        remote.Items.Clear();
        columnTitles[0] = where;
        ShowSortArrow();
        if (error != null)
        {
            remote.Items.Add(new ListViewItem("Can't list it: " + error) { ForeColor = Color.Firebrick });
            remoteInfo.Text = "Not connected? Try Connect to Frame.";
        }
        else if (output.Trim() == "MISSING")
        {
            remote.Items.Add(new ListViewItem("(no such folder yet: copying here makes it)") { ForeColor = SystemColors.GrayText });
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
            for (int i = 1; i < mark; i++)
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
                + (long.TryParse(lines[0].Trim(), out free) ? "   |   " + Human(free) + " free on the headset" : "");
        }
        remote.EndUpdate();
        selectAfter = null;
    }

    void OpenSelected()
    {
        if (remote.SelectedItems.Count != 1) return;
        var e = remote.SelectedItems[0].Tag as Entry;
        if (e == null || !e.Dir) return;
        subfolder.Text = Join(CurrentSub(), e.Name);
        ListFolder();
    }

    void GoUp()
    {
        string sub = CurrentSub();
        if (sub.Length == 0) return;
        int slash = sub.LastIndexOf('/');
        subfolder.Text = slash < 0 ? "" : sub.Substring(0, slash);
        ListFolder();
    }

    void NewFolder()
    {
        string sub = CurrentSub();
        string name = Prompt("New folder", "Make a folder in ~/Videos" + (sub.Length > 0 ? "/" + sub : "") + " named:", "");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            MessageBox.Show(this, "A folder name can't be empty, \".\" or \"..\", or contain / or \\.", Text);
            return;
        }
        string target = Join(sub, name);
        RunThenRelist("mkdir -p " + RemoteDir(target), target);
    }

    // Asks first: names the items, says folders go with everything in them.
    void DeleteSelected()
    {
        var entries = new List<Entry>();
        foreach (ListViewItem item in remote.SelectedItems)
        {
            var e = item.Tag as Entry;
            if (e != null && e.Name != "." && e.Name != ".." && e.Name.IndexOf('/') < 0) entries.Add(e);
        }
        if (entries.Count == 0) return;
        string sub = CurrentSub();
        var names = new StringBuilder();
        bool anyDir = false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Dir) anyDir = true;
            if (i < 12) names.Append("    ").Append(entries[i].Name).Append(entries[i].Dir ? "/" : "").Append('\n');
        }
        if (entries.Count > 12) names.Append("    ...and ").Append(entries.Count - 12).Append(" more\n");
        if (MessageBox.Show(this, "Delete " + (entries.Count == 1 ? "this" : "these " + entries.Count) + " from the headset's ~/Videos"
                + (sub.Length > 0 ? "/" + sub : "") + "?\n\n" + names
                + (anyDir ? "\nFolders are deleted with everything in them." : "") + "\nThis can't be undone.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var cmd = new StringBuilder("cd " + RemoteDir(sub) + " && rm -rf --");
        foreach (var e in entries) cmd.Append(' ').Append(Sh(e.Name));
        RunThenRelist(cmd.ToString(), sub);
    }

    // Renames one file or folder where it is. Refuses a name that is
    // taken (mv -n, after a check that also catches a folder of that name,
    // which mv would move the item into) and asks before an extension change.
    void RenameSelected()
    {
        if (remote.SelectedItems.Count != 1) return;
        var e = remote.SelectedItems[0].Tag as Entry;
        if (e == null || e.Name == "." || e.Name == ".." || e.Name.IndexOf('/') >= 0) return;
        string sub = CurrentSub();
        if (busy && e.Dir)
        {
            MessageBox.Show(this, "Copies are under way. Rename folders once they finish.", Text);
            return;
        }
        string oldExt = e.Dir ? "" : Path.GetExtension(e.Name);
        string name = Prompt("Rename", "Rename " + (e.Dir ? "the folder " : "") + "\"" + e.Name + "\" in ~/Videos"
            + (sub.Length > 0 ? "/" + sub : "") + " to:", e.Name, e.Name.Length - oldExt.Length);
        if (name == null) return;
        name = name.Trim();
        if (name == e.Name) return;
        if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            MessageBox.Show(this, "A name can't be empty, \".\" or \"..\", or contain / or \\.", Text);
            return;
        }
        if (name[0] == '.')
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
        if (!string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, (newExt.Length == 0 ? "Drop the extension " + oldExt : "Change the extension from "
                    + (oldExt.Length > 0 ? oldExt : "(none)") + " to " + newExt)
                + "?\n\nFrameCinema goes by the extension, so the file may not play or show up.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        selectAfter = name;
        RunThenRelist(RenameCommand(sub, e.Name, name), sub);
    }

    static string RenameCommand(string sub, string from, string to)
    {
        return "cd " + RemoteDir(sub) + " || exit 1; o=" + Sh(from) + "; n=" + Sh(to) + ";"
            + " [ -e \"$o\" ] || [ -L \"$o\" ] || { echo \"$o is gone (renamed or deleted elsewhere?)\" >&2; exit 1; };"
            + " if [ -e \"$n\" ] || [ -L \"$n\" ]; then echo \"$n already exists here\" >&2; exit 1; fi;"
            + " mv -n -T -- \"$o\" \"$n\" && [ -e \"$n\" ] && ! [ -e \"$o\" ]"
            + " || { echo \"couldn't rename it\" >&2; exit 1; }";
    }

    void RunThenRelist(string cmd, string showSub)
    {
        string host = hostName;
        new Thread(delegate ()
        {
            string output;
            string error = RunRemote(cmd, host, 5, out output);
            UI(delegate
            {
                if (error != null) MessageBox.Show(this, "That didn't work on the headset:\n\n" + error, Text);
                else subfolder.Text = showSub;
                ListFolder();
            });
        }) { IsBackground = true }.Start();
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
