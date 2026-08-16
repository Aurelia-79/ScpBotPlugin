using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Logger = LabApi.Features.Console.Logger;

namespace ScpBotPlugin.ExternalAI;

/// <summary>
/// C# 侧桥接器：在后台线程维护一条到外部 AI 服务器的 TCP 连接，
/// 按行收发 JSON。主线程只通过 <see cref="Enqueue"/> 发送、<see cref="DrainOrders"/> 取指令，
/// 网络线程与游戏主线程互不阻塞。
/// 收到任何数据即视为连接活跃；超过超时未收到任何数据判定失联（由调用方降级为本地 AI）。
/// </summary>
public sealed class ExternalAIBridge : IDisposable
{
    private const string JsonNewLine = "\n";

    private readonly ExternalAiConfig _config;

    private readonly object _sendLock = new();
    private readonly List<string> _sendBuffer = new();
    private readonly ConcurrentQueue<BotOrders> _incoming = new();

    private readonly byte[] _readBuffer = new byte[16 * 1024];
    private readonly StringBuilder _lineAccumulator = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _connected;
    private long _lastReceiveTicks = Environment.TickCount;

    /// <summary>是否已建立 TCP 连接。</summary>
    public bool Connected => _connected;

    /// <summary>连接是否可用（已连接且未超时）。</summary>
    public bool IsActive => _connected && (Environment.TickCount - _lastReceiveTicks) < (long)(_config.TimeoutSeconds * 1000.0);

    public ExternalAIBridge(ExternalAiConfig config)
    {
        _config = config;
    }

    /// <summary>启动后台线程（可重入：只启动一次）。</summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ScpBot-ExternalAI"
        };
        _thread.Start();
    }

    /// <summary>停止并断开连接。</summary>
    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        CloseClient();
        _connected = false;
    }

    /// <summary>主线程调用：把一行 JSON（不含换行）加入发送缓冲。</summary>
    public void Enqueue(string line)
    {
        if (!_connected)
        {
            return;
        }

        lock (_sendLock)
        {
            _sendBuffer.Add(line + JsonNewLine);
        }
    }

    /// <summary>主线程调用：取出全部待执行指令（同一 bot 的多条会全量返回，由调用方合并保留最新）。</summary>
    public IReadOnlyList<BotOrders> DrainOrders()
    {
        List<BotOrders> list = new();
        while (_incoming.TryDequeue(out BotOrders? order))
        {
            list.Add(order);
        }

        return list;
    }

    public void Dispose()
    {
        Stop();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                if (!TryConnect())
                {
                    Thread.Sleep(2000);
                    continue;
                }

                _connected = true;
                _lastReceiveTicks = Environment.TickCount;
                Logger.Info($"[ScpBot] 已连接外部 AI 服务器 {_config.Host}:{_config.Port}");

                while (_running && _stream != null)
                {
                    // 发送挂起的行（主线程 Enqueue 的数据）。
                    lock (_sendLock)
                    {
                        if (_sendBuffer.Count > 0)
                        {
                            string batch = string.Join(string.Empty, _sendBuffer);
                            _sendBuffer.Clear();
                            byte[] bytes = Encoding.UTF8.GetBytes(batch);
                            _stream.Write(bytes, 0, bytes.Length);
                            _stream.Flush();
                        }
                    }

                    // 读所有可用的行（非阻塞轮询）。
                    while (_stream.DataAvailable)
                    {
                        int read = _stream.Read(_readBuffer, 0, _readBuffer.Length);
                        if (read <= 0)
                        {
                            throw new IOException("connection closed");
                        }

                        foreach (char c in Encoding.UTF8.GetString(_readBuffer, 0, read))
                        {
                            if (c == '\n')
                            {
                                string line = _lineAccumulator.ToString();
                                _lineAccumulator.Clear();
                                if (line.Length > 0)
                                {
                                    ProcessLine(line);
                                }
                            }
                            else if (c != '\r')
                            {
                                _lineAccumulator.Append(c);
                            }
                        }
                    }

                    Thread.Sleep(2);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ScpBot] 外部 AI 连接异常：{ex.Message}");
            }
            finally
            {
                _connected = false;
                CloseClient();
                if (_running)
                {
                    Logger.Info("[ScpBot] 外部 AI 连接断开，等待重连…");
                }

                Thread.Sleep(1500);
            }
        }

        _connected = false;
    }

    private bool TryConnect()
    {
        try
        {
            _client = new TcpClient();
            _client.Connect(_config.Host, _config.Port);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 5000;
            return true;
        }
        catch
        {
            CloseClient();
            return false;
        }
    }

    private void CloseClient()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
        }

        try
        {
            _client?.Close();
        }
        catch
        {
        }

        _stream = null;
        _client = null;
    }

    private void ProcessLine(string line)
    {
        // 收到任何数据都视为连接活跃。
        _lastReceiveTicks = Environment.TickCount;

        string? type = JsonMini.FindValue(line, "type");
        if (type == null)
        {
            return;
        }

        if (type.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (type.Equals("orders", StringComparison.OrdinalIgnoreCase))
        {
            BotOrders? order = ParseOrders(line);
            if (order != null)
            {
                _incoming.Enqueue(order);
            }
        }
    }

    private static BotOrders? ParseOrders(string line)
    {
        if (!JsonMini.TryInt(JsonMini.FindValue(line, "bot"), out int botId))
        {
            return null;
        }

        BotOrders order = new() { Bot = botId };

        if (JsonMini.TryParseVector(JsonMini.FindValue(line, "moveTo"), out float mx, out float my, out float mz))
        {
            order.HasMoveTo = true;
            order.MoveX = mx;
            order.MoveY = my;
            order.MoveZ = mz;
        }

        if (JsonMini.TryParseVector(JsonMini.FindValue(line, "chaseTo"), out float cx, out float cy, out float cz))
        {
            order.HasChaseTo = true;
            order.ChaseX = cx;
            order.ChaseY = cy;
            order.ChaseZ = cz;
        }

        if (JsonMini.TryParseVector(JsonMini.FindValue(line, "look"), out float lx, out float ly, out float lz))
        {
            order.HasLook = true;
            order.LookX = lx;
            order.LookY = ly;
            order.LookZ = lz;
        }

        if (JsonMini.TryInt(JsonMini.FindValue(line, "shoot"), out int shoot))
        {
            order.Shoot = shoot != 0;
        }

        return order;
    }
}