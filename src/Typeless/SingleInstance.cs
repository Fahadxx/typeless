using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Typeless
{
    // One radial menu per multi-select: the first process owns a named mutex and a pipe server,
    // later processes send their paths over the pipe and exit.
    internal sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\Typeless.Radial";
        private const int ConnectTimeoutMs = 1500;

        private static readonly string PipeName = "Typeless.Radial." + Process.GetCurrentProcess().SessionId;

        private readonly Mutex mutex;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private bool released;

        private SingleInstance(Mutex mutex)
        {
            this.mutex = mutex;
        }

        // Returns null when another process already owns the menu.
        public static SingleInstance? Acquire()
        {
            var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // The mutex object can outlive a released owner while handles are still open.
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    mutex.Dispose();
                    return null;
                }
            }
            return new SingleInstance(mutex);
        }

        public static bool Send(IReadOnlyList<string> paths)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(ConnectTimeoutMs);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                foreach (string p in paths) writer.WriteLine(p);
                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Could not reach the running Typeless menu: " + ex.Message);
                return false;
            }
        }

        // Accepts connections on a background thread until Release. onPath runs on that thread.
        public void StartServer(Action<string> onPath)
        {
            CancellationToken token = cts.Token;
            Task.Run(() => ServeAsync(onPath, token));
        }

        private static async Task ServeAsync(Action<string> onPath, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    Log.Write("Pipe server failed to start: " + ex.Message);
                    return;
                }

                try
                {
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    server.Dispose();
                    if (!(ex is OperationCanceledException)) Log.Write("Pipe server stopped: " + ex.Message);
                    return;
                }

                _ = Task.Run(() => ReadClient(server, onPath));
            }
        }

        private static void ReadClient(NamedPipeServerStream server, Action<string> onPath)
        {
            try
            {
                using (server)
                using (var reader = new StreamReader(server, new UTF8Encoding(false)))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length > 0) onPath(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Pipe read failed: " + ex.Message);
            }
        }

        // Must run on the thread that acquired the mutex. Safe to call more than once.
        public void Release()
        {
            if (released) return;
            released = true;
            cts.Cancel();
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            mutex.Dispose();
        }

        public void Dispose() => Release();
    }
}
