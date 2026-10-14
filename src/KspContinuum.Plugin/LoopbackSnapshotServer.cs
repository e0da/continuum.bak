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
            public bool Canceled;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        readonly object sync = new object();
        readonly Queue<Pending> queue = new Queue<Pending>();
        readonly TcpListener listener;
        readonly Thread worker;
        readonly AutoResetEvent outboundReady = new AutoResetEvent(false);
        TcpClient activeClient;
        bool pushEnabled;
        string latestPush;
        bool latestPushTerminal;
        bool stopped;

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public bool PushEnabled { get { lock (sync) return !stopped && pushEnabled; } }

        public LoopbackSnapshotServer(int port)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port");
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
                    if (!candidate.Canceled) { pending = candidate; break; }
                }
            }
            if (pending == null) return false;
            try { pending.Response = handle(pending.Command); }
            catch { pending.Response = Error("handler-failed", pending.RequestId); }
            finally { pending.Done.Set(); }
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
                        client.SendTimeout = 3000;
                        NetworkStream stream = client.GetStream();
                        while (true)
                        {
                            string observation = null;
                            lock (sync)
                            {
                                if (pushEnabled && latestPush != null)
                                { observation = latestPush; latestPush = null; latestPushTerminal = false; }
                            }
                            if (observation != null)
                            {
                                WriteLine(stream, observation);
                                continue;
                            }
                            if (client.Available == 0)
                            {
                                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0) break;
                                outboundReady.WaitOne(10);
                                continue;
                            }
                            string command = ReadCommand(stream);
                            if (command == "\0") break;
                            string response;
                            if (command == null) response = Error("invalid-frame");
                            else
                            {
                                var pending = new Pending { Command = command,
                                    RequestId = LiveControlRequest.Parse(command).requestId };
                                lock (sync)
                                {
                                    if (stopped || queue.Count >= 8)
                                        response = Error(stopped ? "stopped" : "busy", pending.RequestId);
                                    else { queue.Enqueue(pending); response = null; }
                                }
                                if (response == null)
                                {
                                    if (pending.Done.WaitOne(3000)) response = pending.Response;
                                    else
                                    {
                                        lock (sync) pending.Canceled = true;
                                        response = Error("game-thread-timeout", pending.RequestId);
                                    }
                                }
                            }
                            WriteLine(stream, response);
                            if (command == null) break;
                        }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
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

        public void Dispose()
        {
            lock (sync)
            {
                if (stopped) return;
                stopped = true;
                while (queue.Count != 0)
                {
                    Pending pending = queue.Dequeue();
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
