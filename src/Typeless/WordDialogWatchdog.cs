using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace Typeless
{
    // Dismisses the modal dialogs Word (and its PDF reflow helper) shows during Documents.Open
    // even with ConfirmConversions:false and DisplayAlerts off. Runs on its own MTA thread.
    public sealed class WordDialogWatchdog : IDisposable
    {
        private static readonly string[] ProcessNames = { "WINWORD", "PDFREFLOW" };
        private const int PollMs = 300;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Thread _thread;
        private bool _disposed;

        public WordDialogWatchdog()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "WordDialogWatchdog" };
            _thread.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _thread.Join(2000); } catch { }
            try { _cts.Dispose(); } catch { }
        }

        private void Run()
        {
            CancellationToken token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try { Scan(); } catch { }
                try { if (token.WaitHandle.WaitOne(PollMs)) break; } catch { break; }
            }
        }

        private static void Scan()
        {
            foreach (string name in ProcessNames)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); } catch { continue; }
                foreach (Process p in procs)
                {
                    int pid;
                    try { pid = p.Id; } catch { p.Dispose(); continue; }
                    p.Dispose();
                    try { ScanProcess(pid, name); } catch { }
                }
            }
        }

        private static void ScanProcess(int pid, string processName)
        {
            AutomationElementCollection windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            foreach (AutomationElement window in windows)
            {
                try { HandleWindow(window, processName); } catch { }
            }
        }

        private static void HandleWindow(AutomationElement window, string processName)
        {
            AutomationElementCollection all = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            var text = new StringBuilder();
            AutomationElement? ok = null, yes = null, dontShow = null;
            foreach (AutomationElement el in all)
            {
                string elName;
                ControlType? type;
                try
                {
                    elName = el.Current.Name ?? "";
                    type = el.Current.ControlType;
                }
                catch { continue; }

                string label = elName.Replace("&", "").Trim();
                if (type == ControlType.Button)
                {
                    if (label.Equals("OK", StringComparison.OrdinalIgnoreCase)) ok = el;
                    else if (label.Equals("Yes", StringComparison.OrdinalIgnoreCase)) yes = el;
                }
                else if (type == ControlType.CheckBox)
                {
                    if (label.IndexOf("show this message again", StringComparison.OrdinalIgnoreCase) >= 0) dontShow = el;
                }
                else
                {
                    text.Append(elName).Append('\n');
                }
            }

            string body = text.ToString();
            if (Contains(body, "will now convert your PDF") && ok != null)
            {
                if (dontShow != null) TryCheck(dontShow);
                if (TryInvoke(ok))
                    Log.Write($"WordDialogWatchdog: dismissed PDF conversion notice ({processName}).");
            }
            else if (Contains(body, "caused a serious error") && Contains(body, "still want to open") && yes != null)
            {
                if (TryInvoke(yes))
                    Log.Write($"WordDialogWatchdog: answered Yes to serious error prompt ({processName}).");
            }
        }

        private static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void TryCheck(AutomationElement checkBox)
        {
            try
            {
                if (checkBox.TryGetCurrentPattern(TogglePattern.Pattern, out object pattern))
                {
                    var toggle = (TogglePattern)pattern;
                    if (toggle.Current.ToggleState == ToggleState.Off) toggle.Toggle();
                }
            }
            catch { }
        }

        private static bool TryInvoke(AutomationElement button)
        {
            try
            {
                if (button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
