// FrameUpload: copy videos/pictures from this PC to the Steam Frame's ~/Videos
// over ssh (the headset has no SMB share), using the "frame" host entry in
// %USERPROFILE%\.ssh\config.
// v2: Windows' built-in ssh.exe (OpenSSH for Windows 9.5) tops out at about
// 12-15 MB/s on its own, while the link does 55 MB/s on one TCP stream and
// ~70 on several. So v2 prefers Git for Windows' ssh.exe when it is installed
// and sends big files as several byte ranges at once, each over its own ssh
// connection, written into place on the headset with dd.
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
        ClientSize = new Size(560, 380);
        MinimumSize = new Size(420, 300);
        AllowDrop = true;
        Font = new Font("Segoe UI", 9f);

        var dest = new Label { Text = "Copy to " + o.Host + ":  ~/Videos/", AutoSize = true, Location = new Point(12, 15) };
        subfolder.Location = new Point(200, 12);
        subfolder.Width = 200;
        subfolder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var hint = new Label { Text = "(blank = ~/Videos)", AutoSize = true, ForeColor = SystemColors.GrayText,
            Location = new Point(410, 15), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        list.Location = new Point(12, 44);
        list.Size = new Size(536, 200);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.HorizontalScrollbar = true;

        fileBar.Location = new Point(12, 254);
        fileBar.Size = new Size(536, 18);
        fileBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        totalBar.Location = new Point(12, 278);
        totalBar.Size = new Size(536, 18);
        totalBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        status.Location = new Point(12, 304);
        status.Size = new Size(536, 34);
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        status.Text = "Drag videos or pictures here, or click Add files.";

        var via = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Location = new Point(12, 349),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            Text = "via " + (gitSsh ? "Git's ssh" : "Windows' ssh (slow: install Git for Windows for ~4x)")
                + ", up to " + streams + " streams" };

        addButton.Text = "Add files...";
        addButton.Size = new Size(110, 28);
        addButton.Location = new Point(438, 342);
        addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        addButton.Click += delegate { PickFiles(); };

        fileBar.Maximum = totalBar.Maximum = 1000;
        Controls.AddRange(new Control[] { dest, subfolder, hint, list, fileBar, totalBar, status, via, addButton });

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
        var stderr = new StringBuilder();
        using (var proc = StartSsh(remote, stderr))
        {
            proc.StandardInput.Close();
            proc.WaitForExit();
            if (proc.ExitCode == 0) return null;
            string why;
            lock (stderr) why = stderr.ToString().Trim();
            return why.Length > 0 ? why.Replace("\r", "").Replace("\n", " | ") : "ssh exit code " + proc.ExitCode;
        }
    }

    Process StartSsh(string remote, StringBuilder stderr)
    {
        // AES-GCM first: both ends have AES instructions, and it beat
        // chacha20 on the Frame (55 vs 50 MB/s on one stream).
        var psi = new ProcessStartInfo(sshExe,
            "-o BatchMode=yes -o ConnectTimeout=10 -c aes128-gcm@openssh.com,chacha20-poly1305@openssh.com "
            + opt.Host + " \"" + remote + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // Git's ssh finds ~/.ssh (config, keys, known_hosts) through HOME,
        // which is unset outside Git Bash.
        if (gitSsh && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME")))
            psi.EnvironmentVariables["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var proc = Process.Start(psi);
        lock (live) { live.RemoveAll(p => p.HasExited); live.Add(proc); }
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.OutputDataReceived += (s, e) => { };
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
