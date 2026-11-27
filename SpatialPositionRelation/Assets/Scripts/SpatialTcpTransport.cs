using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// 在场景对象启用时启动监听队列，给两端提供本机消息通道。
/// TCP 按字节流传送，不自带消息边界；本项目用 UTF-8 和换行符分隔 JSON 消息。
/// DefaultExecutionOrder 的负值会把该组件的同类生命周期回调排到默认值 0 之前。
/// </summary>
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

    /// <summary>
    /// 组件启用时读取播放器命令行里的端口和实例令牌，然后启动后台监听线程。
    /// OnEnable 是 Unity 生命周期钩子；端口需在 1 至 65535，否则继续用默认 47631。
    /// 后台线程和 Unity 主线程不共享直接改场景的工作；消息先放线程安全队列，由 Update 取出处理。
    /// </summary>
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

    /// <summary>
    /// 让主线程非阻塞地取一条已收到的完整消息；队列空时返回 false。
    /// out 参数会由方法写入取出的文本，是 C# 的额外输出参数。
    /// </summary>
    public bool TryDequeue(out string message)
    {
        return incoming.TryDequeue(out message);
    }

    /// <summary>
    /// 仅在连接建立且文本不为空时把 JSON 加换行排入发送队列。
    /// 这里的连接只代表本地 TCP 客户端已连上；消息仍要等监听线程写入网络流。
    /// </summary>
    public void SendJson(string json)
    {
        if (connected && !string.IsNullOrEmpty(json)) outgoing.Enqueue(json + "\n");
    }

    /// <summary>
    /// 在后台线程监听环回地址上的一个客户端连接，并在客户端断开后回到监听。
    /// Loopback 只允许本机通信；Pending 期间每次睡眠 30 毫秒，避免空转。
    /// 客户端重连时清理旧待发数据并增加连接编号；volatile 让 stopping/connected 的跨线程读写可见，但不负责多步锁定。
    /// Socket 错误通过带 __ERROR__ 前缀的队列文本送回主线程；这是内部标记，不是 SPR/1 JSON。
    /// </summary>
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

    /// <summary>
    /// 一边发送待发 UTF-8 字节，一边把收到的字节拼回逐行消息。
    /// 网络块可能在一个字符中间断开，所以 UTF-8 Decoder 跨块保存解码状态；只在换行处分割 JSON。
    /// 单行缓存最多 262144 个字符，再来的普通字符会触发清空，后续字符可继续累积；空行丢弃，CRLF 去掉末尾回车。
    /// Poll 的 100000 单位是微秒；套接字可读但 Available 为 0 表示对端已关闭。
    /// </summary>
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

    /// <summary>
    /// 组件禁用或销毁时停止监听、关闭活动客户端，并有限等待后台线程退出。
    /// OnDisable 是 Unity 生命周期钩子；Join 的 1000 单位是毫秒，超时不等于强制终止线程。
    /// </summary>
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
