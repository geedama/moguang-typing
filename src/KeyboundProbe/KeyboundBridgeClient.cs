using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ActEnhanced.KeyboundProbe
{
    public sealed class KeyboundBridgeTarget
    {
        public uint EntityId;
        public int Slot;
        public bool Obstructed;
        public string Text;
    }

    public sealed class KeyboundBridgeSnapshot
    {
        public long Sequence;
        public uint TerritoryId;
        public bool InGame;
        public bool InputReady;
        public bool Armed;
        public readonly List<KeyboundBridgeTarget> Targets = new List<KeyboundBridgeTarget>();
    }

    public sealed class KeyboundBridgeSubmitResult
    {
        public string RequestId;
        public bool Success;
        public uint EntityId;
        public string Message;
    }

    public sealed class KeyboundBridgeClient : IDisposable
    {
        private const string DefaultPipeName = "ActEnhanced.KeyboundBridge.v1";
        private readonly object writerGate = new object();
        private readonly string pipeName;
        private Thread worker;
        private volatile bool stopping;
        private volatile bool connected;
        private StreamWriter writer;
        private NamedPipeClientStream pipe;
        private string error;
        private KeyboundBridgeSnapshot pendingSnapshot;

        public event Action<KeyboundBridgeSnapshot> SnapshotReceived;
        public event Action<KeyboundBridgeSubmitResult> SubmitResultReceived;
        public event Action<bool> ConnectionChanged;

        public KeyboundBridgeClient() : this(DefaultPipeName) { }

        internal KeyboundBridgeClient(string pipeName)
        {
            this.pipeName = pipeName;
        }

        public bool IsConnected { get { return connected; } }
        public string Error { get { return error; } }

        public void Start()
        {
            if (worker != null)
                return;
            stopping = false;
            worker = new Thread(Run) { IsBackground = true, Name = "KeyboundBridgeClient" };
            worker.Start();
        }

        public bool SetArmed(bool armed)
        {
            return SendLine("ARM|" + (armed ? "1" : "0"));
        }

        public bool Submit(string requestId, uint entityId, string text)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? String.Empty));
            return SendLine("SUBMIT|" + requestId + "|" +
                entityId.ToString("X8", CultureInfo.InvariantCulture) + "|" + encoded);
        }

        private void Run()
        {
            while (!stopping)
            {
                try
                {
                    var current = new NamedPipeClientStream(".", pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe = current;
                    current.Connect(1500);
                    var currentWriter = new StreamWriter(current, new UTF8Encoding(false), 4096);
                    currentWriter.AutoFlush = true;
                    currentWriter.NewLine = "\n";
                    lock (writerGate) { writer = currentWriter; }
                    SetConnected(true);

                    using (var reader = new StreamReader(current, new UTF8Encoding(false), false, 4096, true))
                    {
                        ReadLoop(reader);
                    }
                }
                catch (Exception ex)
                {
                    if (!stopping)
                        error = ex.Message;
                }
                finally
                {
                    lock (writerGate)
                    {
                        if (writer != null)
                        {
                            try { writer.Dispose(); } catch { }
                            writer = null;
                        }
                    }
                    if (pipe != null)
                    {
                        try { pipe.Dispose(); } catch { }
                        pipe = null;
                    }
                    SetConnected(false);
                }

                if (!stopping)
                    Thread.Sleep(1000);
            }
        }

        private void ReadLoop(StreamReader reader)
        {
            while (!stopping)
            {
                string line = reader.ReadLine();
                if (line == null)
                    return;
                ProcessIncomingLine(line);
            }
        }

        internal void ProcessIncomingLine(string line)
        {
            string[] fields = line.Split('|');
            if (fields.Length == 7 && fields[0] == "STATE")
            {
                long sequence;
                uint territory;
                if (!Int64.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence) ||
                    !UInt32.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out territory))
                    return;
                pendingSnapshot = new KeyboundBridgeSnapshot
                {
                    Sequence = sequence,
                    TerritoryId = territory,
                    InGame = fields[3] == "1",
                    InputReady = fields[4] == "1",
                    Armed = fields[5] == "1"
                };
            }
            else if (fields.Length == 6 && fields[0] == "TARGET" && pendingSnapshot != null)
            {
                long sequence;
                uint entityId;
                int slot;
                if (!Int64.TryParse(fields[1], out sequence) || sequence != pendingSnapshot.Sequence ||
                    !UInt32.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out entityId) ||
                    !Int32.TryParse(fields[3], out slot))
                    return;
                try
                {
                    pendingSnapshot.Targets.Add(new KeyboundBridgeTarget
                    {
                        EntityId = entityId,
                        Slot = slot,
                        Obstructed = fields[4] == "1",
                        Text = Encoding.UTF8.GetString(Convert.FromBase64String(fields[5]))
                    });
                }
                catch (FormatException) { }
            }
            else if (fields.Length == 2 && fields[0] == "END" && pendingSnapshot != null)
            {
                long sequence;
                if (Int64.TryParse(fields[1], out sequence) && sequence == pendingSnapshot.Sequence)
                {
                    Action<KeyboundBridgeSnapshot> handler = SnapshotReceived;
                    if (handler != null)
                        handler(pendingSnapshot);
                }
                pendingSnapshot = null;
            }
            else if (fields.Length == 5 && fields[0] == "RESULT")
            {
                uint entityId;
                if (!UInt32.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out entityId))
                    return;
                string message = String.Empty;
                try { message = Encoding.UTF8.GetString(Convert.FromBase64String(fields[4])); }
                catch (FormatException) { }
                Action<KeyboundBridgeSubmitResult> handler = SubmitResultReceived;
                if (handler != null)
                    handler(new KeyboundBridgeSubmitResult
                    {
                        RequestId = fields[1],
                        Success = fields[2] == "OK",
                        EntityId = entityId,
                        Message = message
                    });
            }
        }

        private bool SendLine(string line)
        {
            lock (writerGate)
            {
                if (!connected || writer == null)
                    return false;
                try
                {
                    writer.WriteLine(line);
                    writer.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private void SetConnected(bool value)
        {
            if (connected == value)
                return;
            connected = value;
            Action<bool> handler = ConnectionChanged;
            if (handler != null)
                handler(value);
        }

        public void Dispose()
        {
            stopping = true;
            if (pipe != null)
            {
                try { pipe.Dispose(); } catch { }
            }
            if (worker != null && !worker.Join(2000))
                worker.Interrupt();
            worker = null;
        }
    }
}
