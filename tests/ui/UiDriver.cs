// Drives FrameUpload's window through UI Automation for the tests, for the
// parts that have no command line (Apps... > Manage installed apps...).
//
//   UiDriver.exe PID connected            waits until it says "Connected to"
//   UiDriver.exe PID list                 opens Manage installed apps, prints
//                                         the rows, closes it
//   UiDriver.exe PID uninstall NAME...    same, selects NAMEs, clicks
//                                         Uninstall..., prints the
//                                         confirmation, clicks OK, prints the
//                                         rows after
//   UiDriver.exe PID close                closes the main window
//
// Prints ROW<TAB>app<TAB>kind<TAB>size, MSG<TAB>text, and ERR<TAB>why; exit
// code 0 when every step happened. Clicks that open a modal window run on a
// thread of their own, since UI Automation's Invoke waits for the click to
// return.
// Build: csc /r:<WPF>\UIAutomationClient.dll /r:<WPF>\UIAutomationTypes.dll /r:<WPF>\WindowsBase.dll UiDriver.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

static class UiDriver
{
    static int pid;

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("ERR\tusage: UiDriver PID connected|list|uninstall NAME...|close"); return 2; }
        pid = int.Parse(args[0]);
        try
        {
            var main = WaitFor("the main window", 30, () => TopWindows().FirstOrDefault(w => w.Current.Name == "Frame Upload"));
            switch (args[1])
            {
                case "connected":
                    WaitFor("\"Connected to ...\"", 30, () => All(main, ControlType.Text).FirstOrDefault(t => t.Current.Name.StartsWith("Connected to")));
                    return 0;
                case "list":
                {
                    var dlg = OpenManage(main);
                    PrintRows(WaitForRows(dlg));
                    CloseDialog(dlg);
                    return 0;
                }
                case "uninstall":
                {
                    var names = args.Skip(2).ToList();
                    var dlg = OpenManage(main);
                    var list = ListOf(dlg);
                    var rows = WaitForRows(dlg);
                    bool first = true;
                    foreach (var n in names)
                    {
                        var item = Items(list).FirstOrDefault(i => i.Current.Name == n);
                        if (item == null) throw new Exception("no row named " + n + " (rows: " + string.Join(", ", rows.Select(r => r[0]).ToArray()) + ")");
                        var sel = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
                        if (first) sel.Select(); else sel.AddToSelection();
                        first = false;
                    }
                    ClickLater(Single(dlg, ControlType.Button, "Uninstall..."));
                    var box = WaitFor("the confirmation", 15, () => Dialogs().FirstOrDefault(w => w.Current.Name == "Frame Upload"
                        && IsMessageBox(w)));
                    foreach (var t in Kids(box, ControlType.Text)) Console.WriteLine("MSG\t" + t.Current.Name.Replace("\r", "").Replace("\n", "\\n"));
                    ClickLater(Single(box, ControlType.Button, "OK"));
                    // An error opens a second box; otherwise the list reloads.
                    var after = WaitFor("the list after uninstalling", 30, delegate
                    {
                        var err = Dialogs().FirstOrDefault(w => w.Current.Name == "Frame Upload" && IsMessageBox(w));
                        if (err != null)
                        {
                            foreach (var t in Kids(err, ControlType.Text)) Console.WriteLine("MSG\t" + t.Current.Name.Replace("\r", "").Replace("\n", "\\n"));
                            ClickLater(Single(err, ControlType.Button, "OK"));
                            Thread.Sleep(500);
                            return null;
                        }
                        var r = ReadRows(dlg);
                        return r != null && !r.Any(x => names.Contains(x[0])) ? r : null;
                    });
                    PrintRows(after);
                    CloseDialog(dlg);
                    return 0;
                }
                case "close":
                    ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
                    return 0;
                default:
                    Console.WriteLine("ERR\tunknown step " + args[1]);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR\t" + ex.Message.Replace("\r", "").Replace("\n", " "));
            return 1;
        }
    }

    static AutomationElement OpenManage(AutomationElement main)
    {
        ((InvokePattern)Single(main, ControlType.Button, "Apps...").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var item = WaitFor("the Manage installed apps... menu item", 10, () => TopWindows().SelectMany(w => All(w, ControlType.MenuItem))
            .FirstOrDefault(m => m.Current.Name == "Manage installed apps..."));
        ClickLater(item);
        return WaitFor("the Installed apps window", 15, () => Dialogs().FirstOrDefault(w => w.Current.Name == "Installed apps"));
    }

    static void CloseDialog(AutomationElement dlg)
    {
        ClickLater(Single(dlg, ControlType.Button, "Close"));
        WaitFor("the Installed apps window to close", 10, () => Dialogs().Any(w => w.Current.Name == "Installed apps") ? null : "closed");
    }

    // Rows once the listing is in (not "Listing..."); a one-cell row is a
    // message such as "No sideloaded apps yet.".
    static List<string[]> WaitForRows(AutomationElement dlg)
    {
        return WaitFor("the app list", 30, () => ReadRows(dlg));
    }

    static List<string[]> ReadRows(AutomationElement dlg)
    {
        var list = ListOf(dlg);
        var rows = new List<string[]>();
        foreach (var item in Items(list))
        {
            var cells = new List<string> { item.Current.Name };
            var kids = item.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().Select(k => k.Current.Name).ToList();
            if (kids.Count > 1) cells = kids;
            rows.Add(cells.ToArray());
        }
        if (rows.Count == 0 || rows.Any(r => r[0] == "Listing...")) return null;
        return rows;
    }

    static void PrintRows(List<string[]> rows)
    {
        foreach (var r in rows) Console.WriteLine("ROW\t" + string.Join("\t", r));
    }

    // A ListView in Details view shows up as a DataGrid of DataItems (or a
    // List of ListItems, depending on Windows).
    static AutomationElement ListOf(AutomationElement dlg)
    {
        var e = All(dlg, ControlType.DataGrid).FirstOrDefault() ?? All(dlg, ControlType.List).FirstOrDefault();
        if (e == null) throw new Exception("no list in " + dlg.Current.Name);
        return e;
    }

    static IEnumerable<AutomationElement> Items(AutomationElement list)
    {
        return list.FindAll(TreeScope.Children, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))).Cast<AutomationElement>();
    }

    // A message box: a window with an OK button of its own (the main window
    // holds the dialogs, so their buttons are only its descendants).
    static bool IsMessageBox(AutomationElement w)
    {
        return w.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Any(b => b.Current.Name == "OK");
    }

    static void ClickLater(AutomationElement e)
    {
        var p = (InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern);
        new Thread(delegate () { try { p.Invoke(); } catch (Exception) { } }) { IsBackground = true }.Start();
        Thread.Sleep(300);
    }

    static IEnumerable<AutomationElement> TopWindows()
    {
        return AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>();
    }

    // Dialogs show up under their owner window, or at the top.
    static IEnumerable<AutomationElement> Dialogs()
    {
        var found = new List<AutomationElement>();
        foreach (var top in TopWindows())
        {
            found.Add(top);
            found.AddRange(top.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>());
        }
        return found;
    }

    static IEnumerable<AutomationElement> All(AutomationElement root, ControlType type)
    {
        return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>();
    }

    static IEnumerable<AutomationElement> Kids(AutomationElement root, ControlType type)
    {
        return root.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>();
    }

    static AutomationElement Single(AutomationElement root, ControlType type, string name)
    {
        var e = All(root, type).FirstOrDefault(x => name == null || x.Current.Name == name);
        if (e == null) throw new Exception("no " + type.ProgrammaticName + (name != null ? " \"" + name + "\"" : "") + " in " + root.Current.Name);
        return e;
    }

    static T WaitFor<T>(string what, int seconds, Func<T> probe) where T : class
    {
        var watch = Stopwatch.StartNew();
        Exception last = null;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            try { var v = probe(); if (v != null) return v; }
            catch (ElementNotAvailableException ex) { last = ex; }
            catch (InvalidOperationException ex) { last = ex; }
            Thread.Sleep(250);
        }
        throw new Exception("timed out after " + seconds + " s waiting for " + what + (last != null ? " (" + last.Message + ")" : ""));
    }
}
