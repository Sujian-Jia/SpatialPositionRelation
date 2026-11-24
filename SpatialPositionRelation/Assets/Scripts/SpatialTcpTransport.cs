using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[DefaultExecutionOrder(-100)]
public abstract class SpatialTcpBridgeBase : MonoBehaviour
{
    private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();
    private readonly object clientLock = new object();
    private TcpListener listener;
    private TcpClient activeClient;
    private Thread listenerThread;
    private volatile bool stopping;
    private volatile bool connected;

    private int connectionId;
    private int port = 47631;
    public bool IsConnected => connected;
    public int ConnectionId => Volatile.Read(ref connectionId);
    public string InstanceToken { get; private set; } = "";

    protected virtual void OnEnable()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-spatialPort" && int.TryParse(args[i + 1], out int requestedPort)
                && requestedPort > 0 && requestedPort <= 65535) port = requestedPort;
            else if (args[i] == "-spatialToken") InstanceToken = args[i + 1];
        }
        stopping = false;
        listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "Spatial TCP listener" };
        listenerThread.Start();
    }

    public bool TryDequeue(out string message)
    {
        return incoming.TryDequeue(out message);
    }

    public void SendJson(string json)
    {
        if (connected && !string.IsNullOrEmpty(json)) outgoing.Enqueue(json + "\n");
    }

    private void ListenLoop()
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(1);
            while (!stopping)
            {
                if (!listener.Pending()) { Thread.Sleep(30); continue; }
                TcpClient client = listener.AcceptTcpClient();
                client.NoDelay = true;
                lock (clientLock) activeClient = client;
                while (outgoing.TryDequeue(out _)) { }
                Interlocked.Increment(ref connectionId);
                connected = true;
                RunClient(client);
                connected = false;
                lock (clientLock) activeClient = null;
                client.Close();
            }
        }
        catch (SocketException exception)
        {
            if (!stopping) incoming.Enqueue("__ERROR__" + exception.Message);
        }
        catch (ObjectDisposedException) { }
        finally { connected = false; }
    }

    private void RunClient(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] bytes = new byte[4096];
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        StringBuilder partial = new StringBuilder(4096);
        while (!stopping && client.Connected)
        {
            while (outgoing.TryDequeue(out string line))
            {
                byte[] packet = Encoding.UTF8.GetBytes(line);
                try
                {
                    stream.Write(packet, 0, packet.Length);
                    stream.Flush();
                }
                catch (IOException) { return; }
                catch (ObjectDisposedException) { return; }
            }

            bool readable;
            try
            {
                readable = client.Client.Poll(100000, SelectMode.SelectRead);
                if (readable && client.Available == 0) break;
            }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            if (!readable) continue;

            int count;
            try { count = stream.Read(bytes, 0, bytes.Length); }
            catch (IOException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            if (count <= 0) break;
            int characterCount = decoder.GetChars(bytes, 0, count, characters, 0, false);
            for (int i = 0; i < characterCount; i++)
            {
                char character = characters[i];
                if (character == '\n')
                {
                    string message = partial.ToString().TrimEnd('\r');
                    partial.Length = 0;
                    if (!string.IsNullOrWhiteSpace(message)) incoming.Enqueue(message);
                }
                else if (partial.Length < 262144) partial.Append(character);
                else partial.Length = 0;
            }
        }
    }

    protected virtual void OnDisable()
    {
        stopping = true;
        try { listener?.Stop(); } catch (SocketException) { }
        lock (clientLock)
        {
            try { activeClient?.Close(); } catch (SocketException) { }
            activeClient = null;
        }
        if (listenerThread != null && listenerThread.IsAlive) listenerThread.Join(1000);
        listenerThread = null;
    }
}
