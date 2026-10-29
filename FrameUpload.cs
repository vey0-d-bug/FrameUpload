// FrameUpload: copy videos/pictures from this PC to the Steam Frame's ~/Videos
// over ssh (the headset has no SMB share). Uses Windows' built-in ssh.exe and
// the "frame" host entry in %USERPROFILE%\.ssh\config.
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

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Optional: --host=NAME --sub=FOLDER FILE... (files passed in, e.g. by
        // dropping them on the exe icon, start copying right away).
        string host = "frame", sub = "";
        var files = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith("--host=")) host = a.Substring(7);
            else if (a.StartsWith("--sub=")) sub = a.Substring(6);
            else files.Add(a);
        }
        Application.Run(new UploadForm(host, sub, files.ToArray()));
    }
}

class UploadForm : Form
{
    readonly string host;
    readonly TextBox subfolder = new TextBox();
    readonly ListBox list = new ListBox();
    readonly ProgressBar fileBar = new ProgressBar();
    readonly ProgressBar totalBar = new ProgressBar();
    readonly Label status = new Label();
    readonly Button addButton = new Button();
    readonly Queue<string> queue = new Queue<string>();
    readonly object queueLock = new object();
    bool busy;
    int doneCount, failCount;
    long batchBytes, batchSent;

    public UploadForm(string host, string sub, string[] files)
    {
        this.host = host;
        subfolder.Text = sub;
        if (files.Length > 0) Shown += delegate { Enqueue(files); };
        Text = "Frame Upload";
        ClientSize = new Size(560, 380);
        MinimumSize = new Size(420, 300);
        AllowDrop = true;
        Font = new Font("Segoe UI", 9f);

        var dest = new Label { Text = "Copy to " + host + ":  ~/Videos/", AutoSize = true, Location = new Point(12, 15) };
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

        addButton.Text = "Add files...";
        addButton.Size = new Size(110, 28);
        addButton.Location = new Point(438, 342);
        addButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        addButton.Click += delegate { PickFiles(); };

        fileBar.Maximum = totalBar.Maximum = 1000;
        Controls.AddRange(new Control[] { dest, subfolder, hint, list, fileBar, totalBar, status, addButton });

        DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (s, e) => Enqueue((string[])e.Data.GetData(DataFormats.FileDrop));
        FormClosing += (s, e) =>
        {
            if (busy && MessageBox.Show(this, "An upload is still running. Quit anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
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
        if (added == 0) status.Text = "Nothing to copy (folders are skipped; drop the files themselves).";
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
            try { error = Copy(path, sub, name); }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) doneCount++; else failCount++;
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
            (failCount == 0 ? SystemSounds.Asterisk : SystemSounds.Hand).Play();
        });
    }

    // Streams the file into "cat" on the headset, writing to a hidden .part
    // name first so FrameCinema never sees a half-copied file.
    string Copy(string path, string sub, string name)
    {
        string dir = "$HOME/Videos" + (sub.Length > 0 ? "/" + Sh(sub) : "");
        string part = dir + "/" + Sh("." + name + ".part");
        string final = dir + "/" + Sh(name);
        string remote = "mkdir -p " + dir + " && cat > " + part + " && mv -f " + part + " " + final;

        var psi = new ProcessStartInfo(SshExe(),
            "-o BatchMode=yes -o ConnectTimeout=10 " + host + " \"" + remote + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        var stderr = new StringBuilder();
        using (var proc = Process.Start(psi))
        {
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            proc.OutputDataReceived += (s, e) => { };
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            long size = new FileInfo(path).Length, sent = 0;
            var watch = Stopwatch.StartNew();
            long lastTick = -1000;
            var buf = new byte[1 << 20];
            try
            {
                using (var src = File.OpenRead(path))
                using (var dst = proc.StandardInput.BaseStream)
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        sent += n;
                        batchSent += n;
                        if (watch.ElapsedMilliseconds - lastTick >= 200 || sent == size)
                        {
                            lastTick = watch.ElapsedMilliseconds;
                            double mbps = sent / 1048576.0 / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
                            int f = size > 0 ? (int)(sent * 1000 / size) : 1000;
                            int t = batchBytes > 0 ? (int)Math.Min(1000, batchSent * 1000 / batchBytes) : 1000;
                            string msg = string.Format("Copying {0}  {1} / {2}  ({3:0.0} MB/s)",
                                name, Mb(sent), Mb(size), mbps);
                            UI(delegate { fileBar.Value = f; totalBar.Value = t; status.Text = msg; });
                        }
                    }
                }
            }
            catch (IOException)
            {
                // ssh exited early (bad host, auth, disk full); its stderr says why.
            }
            proc.WaitForExit(); // also waits for the async stderr reader to drain
            if (proc.ExitCode != 0 || sent != size)
            {
                string why;
                lock (stderr) why = stderr.ToString().Trim();
                return why.Length > 0 ? why.Replace("\r", "").Replace("\n", " | ") : "ssh exit code " + proc.ExitCode;
            }
        }
        return null;
    }

    // Single-quote for the remote POSIX shell.
    static string Sh(string s) { return "'" + s.Replace("'", "'\\''") + "'"; }

    static string Mb(long b) { return (b / 1048576.0).ToString("0.0") + " MB"; }

    static string SshExe()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string p = Path.Combine(win, @"System32\OpenSSH\ssh.exe");
        if (File.Exists(p)) return p;
        p = Path.Combine(win, @"Sysnative\OpenSSH\ssh.exe");
        return File.Exists(p) ? p : "ssh.exe";
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
