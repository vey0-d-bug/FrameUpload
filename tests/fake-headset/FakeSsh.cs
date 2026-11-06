// Fake ssh.exe for FrameUpload's tests: instead of logging in to the Frame,
// it runs the remote command with Git for Windows' bash in a folder that
// stands in for the headset.
//
//   FAKE_HEADSET   the fake headset's folder: home\ is $HOME, state\ holds
//                  switches the test sets and what the stubs record, tmp\
//                  the commands that ran
//   FAKE_STUBS     the folder of stand-in headset commands (bin\, flatpak\)
//   FAKE_GIT_USR   Git for Windows' usr\bin (bash, coreutils)
//
// Switches (empty files in state\): offline (every ssh fails like an
// unreachable headset), no-flatpak (flatpak isn't on PATH). The stubs read
// the others themselves.
//
// Takes ssh's own command line: [-o X]... [-c X] HOST COMMAND, with stdin,
// stdout and stderr passed through byte for byte, and exits with the
// command's exit code (255 for ssh's own errors), as ssh does.
// Build: csc /target:exe /out:ssh.exe FakeSsh.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

static class FakeSsh
{
    static int seq;

    static int Main(string[] args)
    {
        int i = 0;
        while (i < args.Length && args[i].StartsWith("-"))
        {
            string a = args[i++];
            if (a == "-o" || a == "-c" || a == "-p" || a == "-i" || a == "-l" || a == "-F") i++;
        }
        if (i >= args.Length) return Fail("usage: ssh [options] host command");
        string host = args[i++];
        if (i >= args.Length) return Fail("fake ssh: an interactive login isn't supported, give a command");
        // Real ssh joins the rest with spaces; FrameUpload always sends one.
        string remote = string.Join(" ", args, i, args.Length - i);

        string root = Environment.GetEnvironmentVariable("FAKE_HEADSET");
        string stubs = Environment.GetEnvironmentVariable("FAKE_STUBS");
        string gitUsr = Environment.GetEnvironmentVariable("FAKE_GIT_USR");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(stubs) || string.IsNullOrEmpty(gitUsr))
            return Fail("fake ssh: FAKE_HEADSET, FAKE_STUBS and FAKE_GIT_USR must be set");
        string state = Path.Combine(root, "state");

        Record(Path.Combine(state, "ssh-log.txt"), host + "\t" + remote.Replace("\r", "").Replace("\n", "\\n"));
        if (File.Exists(Path.Combine(state, "offline")))
        {
            Thread.Sleep(200);
            return Fail("ssh: connect to host " + host + " port 22: Connection timed out");
        }

        // The command goes in a file, so no second round of Windows quoting
        // can change it; bash FILE runs it as sshd's `bash -c` would.
        string tmp = Path.Combine(root, "tmp");
        Directory.CreateDirectory(tmp);
        string script = Path.Combine(tmp, "cmd-" + Process.GetCurrentProcess().Id + "-" + Interlocked.Increment(ref seq) + ".sh");
        File.WriteAllText(script, remote, new UTF8Encoding(false));

        string path = Path.Combine(stubs, "bin")
            + (File.Exists(Path.Combine(state, "no-flatpak")) ? "" : ";" + Path.Combine(stubs, "flatpak"))
            + ";" + gitUsr + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System);
        var psi = new ProcessStartInfo(Path.Combine(gitUsr, "bash.exe"), "--noprofile --norc \"" + script.Replace('\\', '/') + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.Combine(root, "home"),
        };
        psi.EnvironmentVariables["HOME"] = Path.Combine(root, "home");
        psi.EnvironmentVariables["PATH"] = path;
        psi.EnvironmentVariables["FAKE_STATE"] = state;
        psi.EnvironmentVariables["SSH_CONNECTION"] = "192.168.1.10 50000 192.168.1.50 22";
        psi.EnvironmentVariables["LC_ALL"] = "C.UTF-8";
        psi.EnvironmentVariables.Remove("DISPLAY");
        psi.EnvironmentVariables.Remove("WAYLAND_DISPLAY");
        psi.EnvironmentVariables.Remove("XDG_RUNTIME_DIR");

        using (var proc = Process.Start(psi))
        {
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var stderr = Console.OpenStandardError();
            var input = new Thread(delegate ()
            {
                try { Pump(stdin, proc.StandardInput.BaseStream); } catch (IOException) { }
                try { proc.StandardInput.Close(); } catch (IOException) { }
            }) { IsBackground = true };
            var output = new Thread(delegate () { Pump(proc.StandardOutput.BaseStream, stdout); });
            var errors = new Thread(delegate () { Pump(proc.StandardError.BaseStream, stderr); });
            input.Start(); output.Start(); errors.Start();
            proc.WaitForExit();
            output.Join(); errors.Join();
            try { File.Delete(script); } catch (IOException) { }
            return proc.ExitCode;
        }
    }

    static void Pump(Stream from, Stream to)
    {
        var buf = new byte[1 << 20];
        int n;
        try
        {
            while ((n = from.Read(buf, 0, buf.Length)) > 0) { to.Write(buf, 0, n); to.Flush(); }
        }
        catch (IOException) { }
    }

    static int Fail(string why)
    {
        Console.Error.WriteLine(why);
        return 255;
    }

    // Several fake ssh processes run at once (parallel ranges), so writes
    // to the shared log retry while another one holds it.
    static void Record(string file, string line)
    {
        for (int t = 0; t < 50; t++)
        {
            try { File.AppendAllText(file, line + "\r\n", new UTF8Encoding(false)); return; }
            catch (IOException) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) { Thread.Sleep(20); }
        }
    }
}
