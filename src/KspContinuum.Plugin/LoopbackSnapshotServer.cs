using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace KspContinuum
{
    // Socket work never touches Unity objects. The game thread drains one request per Update.
    public sealed class LoopbackSnapshotServer : IDisposable
    {
        sealed class Pending
        {
            public string Command, RequestId, Response;
            public bool Canceled, Started, Completed;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        readonly object sync = new object();
        readonly Queue<Pending> queue = new Queue<Pending>();
        readonly TcpListener listener;
        readonly Thread worker;
        readonly int quickWaitMilliseconds, clockWaitMilliseconds;
        readonly AutoResetEvent outboundReady = new AutoResetEvent(false);
        TcpClient activeClient;
        bool pushEnabled;
        string latestPush;
        bool latestPushTerminal;
        bool stopped;

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public bool PushEnabled { get { lock (sync) return !stopped && pushEnabled; } }

        public LoopbackSnapshotServer(int port, int quickWaitMilliseconds = 3000, int clockWaitMilliseconds = 60000)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port");
            if (quickWaitMilliseconds <= 0 || quickWaitMilliseconds > 60000) throw new ArgumentOutOfRangeException("quickWaitMilliseconds");
            if (clockWaitMilliseconds < quickWaitMilliseconds || clockWaitMilliseconds > 60000) throw new ArgumentOutOfRangeException("clockWaitMilliseconds");
            this.quickWaitMilliseconds = quickWaitMilliseconds; this.clockWaitMilliseconds = clockWaitMilliseconds;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(1);
            worker = new Thread(Serve) { IsBackground = true, Name = "Continuum read-only loopback" };
            worker.Start();
        }

        public bool DrainOne(Func<string, string> handle)
        {
            if (handle == null) throw new ArgumentNullException("handle");
            Pending pending = null;
            lock (sync)
            {
                if (stopped) return false;
                while (queue.Count != 0)
                {
                    Pending candidate = queue.Dequeue();
                    if (!candidate.Canceled) { candidate.Started = true; pending = candidate; break; }
                }
            }
            if (pending == null) return false;
            string response;
            try { response = handle(pending.Command); }
            catch { response = Error("handler-failed", pending.RequestId); }
            lock (sync) { pending.Response = response; pending.Completed = true; pending.Done.Set(); }
            return true;
        }

        public void EnablePush()
        {
            lock (sync)
            {
                if (!stopped && activeClient != null) pushEnabled = true;
            }
        }

        // The game thread replaces one pending observation and never waits for socket I/O.
        public bool PublishLatest(string observation, bool terminal)
        {
            if (observation == null) throw new ArgumentNullException("observation");
            lock (sync)
            {
                if (stopped || !pushEnabled || activeClient == null || latestPushTerminal) return false;
                latestPush = observation;
                latestPushTerminal = terminal;
            }
            outboundReady.Set();
            return true;
        }

        void Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                Socket socket = client.Client;
                if (socket == null) { client.Close(); continue; }
                lock (sync)
                {
                    if (stopped) { client.Close(); return; }
                    activeClient = client;
                    pushEnabled = false;
                    latestPush = null;
                    latestPushTerminal = false;
                }
                using (client)
                {
                    try
                    {
                        socket.SendTimeout = 3000;
                        using (NetworkStream stream = new NetworkStream(socket, false))
                        {
                        while (true)
                        {
                            string observation = null;
                            lock (sync)
                            {
                                if (stopped) break;
                                if (pushEnabled && latestPush != null)
                                { observation = latestPush; latestPush = null; latestPushTerminal = false; }
                            }
                            if (observation != null)
                            {
                                WriteLine(stream, observation);
                                continue;
                            }
                            if (socket.Available == 0)
                            {
                                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) break;
                                outboundReady.WaitOne(10);
                                continue;
                            }
                            string command = ReadCommand(stream);
                            if (command == "\0") break;
                            string response;
                            if (command == null) response = Error("invalid-frame");
                            else
                            {
                                var parsed = LiveControlRequest.Parse(command);
                                var pending = new Pending { Command = command, RequestId = parsed.requestId };
                                lock (sync)
                                {
                                    if (stopped || queue.Count >= 8)
                                        response = Error(stopped ? "stopped" : "busy", pending.RequestId);
                                    else { queue.Enqueue(pending); response = null; }
                                }
                                if (response == null)
                                {
                                    int wait = IsClockAdministration(parsed.operation) ? clockWaitMilliseconds : quickWaitMilliseconds;
                                    pending.Done.WaitOne(wait);
                                    lock (sync)
                                    {
                                        if (pending.Completed) response = pending.Response;
                                        else if (!pending.Started)
                                        { pending.Canceled = true; response = Error("game-thread-timeout-cancelled-before-execution", pending.RequestId); }
                                        else response = ReportJson.Encode(new LiveControlReply { status = "in-progress", reason = "handler-still-running", requestId = pending.RequestId, protocolVersion = parsed.protocolVersion });
                                    }
                                }
                            }
                            WriteLine(stream, response);
                            if (command == null) break;
                        }
                        }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (SocketException) { }
                }
                lock (sync) if (ReferenceEquals(activeClient, client))
                { activeClient = null; pushEnabled = false; latestPush = null; latestPushTerminal = false; }
            }
        }

        static void WriteLine(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        static string ReadCommand(Stream stream)
        {
            var bytes = new List<byte>(80);
            while (bytes.Count <= 256)
            {
                int value = stream.ReadByte();
                if (value < 0) return bytes.Count == 0 ? "\0" : null;
                if (value == '\n') return Encoding.ASCII.GetString(bytes.ToArray());
                if (value < 32 || value > 126) return null;
                bytes.Add((byte)value);
            }
            return null;
        }

        static string Error(string reason, string requestId = null)
        { return ReportJson.Encode(new LiveControlReply { status = "rejected", reason = reason, requestId = requestId }); }
        static bool IsClockAdministration(LiveControlOperation operation)
        { return operation == LiveControlOperation.ClockEnable || operation == LiveControlOperation.ClockDisable || operation == LiveControlOperation.ClockSave || operation == LiveControlOperation.ClockRestore; }

        public void Dispose()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                while (queue.Count != 0)
                {
                    Pending pending = queue.Dequeue();
                    pending.Canceled = true; pending.Completed = true;
                    pending.Response = Error("stopped", pending.RequestId);
                    pending.Done.Set();
                }
            }
            listener.Stop();
            outboundReady.Set();
            lock (sync) if (activeClient != null) activeClient.Close();
        }
    }
}
