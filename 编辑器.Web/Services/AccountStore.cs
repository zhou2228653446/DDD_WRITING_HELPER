using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace 编辑器.Web.Services;

/// <summary>
/// 账户系统：自用账号 + 试用账号（密码每 10 天自动轮换，登录附四位个人码区分使用者）。
///
/// 存储：配置目录 accounts.json（不存在 = 未初始化：仅本机可用，本机直接以
/// 「自用」身份工作，与历史行为一致；非本机必须先在本机完成初始化）。
///
/// 隔离约定：每个「主体」（自用 / 试用+个人码）一套独立的作品库与 AI API 配置；
/// 提示词方案、技能、外观等全局资产共享。自用的书库即 MCP / 桌面端的书库
/// （paths.json 指过去），agent 的「人机共用」不断；试用者的书 agent 看不到。
/// </summary>
public sealed class AccountStore
{
    private readonly string _file;
    private readonly object _gate = new();

    public AccountStore(string configDir)
    {
        _file = Path.Combine(configDir, "accounts.json");
    }

    public sealed class AccountsFile
    {
        [property: JsonPropertyName("self")] public SelfAccount? Self { get; set; }
        [property: JsonPropertyName("trial")] public TrialAccount? Trial { get; set; }
    }

    public sealed class SelfAccount
    {
        [property: JsonPropertyName("salt")] public string Salt { get; set; } = "";
        [property: JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = "";
    }

    public sealed class TrialAccount
    {
        /// <summary>试用密码**明文**保存——自用账号登录后要能看到并转发给试用者。</summary>
        [property: JsonPropertyName("password")] public string Password { get; set; } = "";
        [property: JsonPropertyName("rotatedAt")] public DateTime RotatedAt { get; set; }
        /// <summary>轮换周期（天）。默认 10。</summary>
        [property: JsonPropertyName("rotateDays")] public int RotateDays { get; set; } = 10;
    }

    public const int TrialRotateDaysDefault = 10;

    public bool Initialized => File.Exists(_file);

    // ── 初始化 ──

    /// <summary>首次初始化：设自用密码、生成试用密码。仅在文件不存在时有效。</summary>
    public void Initialize(string selfPassword)
    {
        lock (_gate)
        {
            if (Initialized)
                throw new InvalidOperationException("账号系统已初始化");
            var salt = NewToken(16);
            var f = new AccountsFile
            {
                Self = new SelfAccount { Salt = salt, PasswordHash = Hash(salt, selfPassword) },
                Trial = new TrialAccount
                {
                    Password = NewTrialPassword(),
                    RotatedAt = DateTime.Now,
                    RotateDays = TrialRotateDaysDefault,
                },
            };
            Save(f);
        }
    }

    // ── 自用 ──

    public bool VerifySelf(string password)
    {
        var f = Load();
        if (f?.Self == null) return false;
        return ConstantTimeEquals(Hash(f.Self.Salt, password), f.Self.PasswordHash);
    }

    public void ChangeSelfPassword(string oldPassword, string newPassword)
    {
        lock (_gate)
        {
            var f = Load() ?? throw new InvalidOperationException("账号系统未初始化");
            if (f.Self == null || !ConstantTimeEquals(Hash(f.Self.Salt, oldPassword), f.Self.PasswordHash))
                throw new InvalidOperationException("原密码不对");
            f.Self.Salt = NewToken(16);
            f.Self.PasswordHash = Hash(f.Self.Salt, newPassword);
            Save(f);
        }
    }

    // ── 试用 ──

    /// <summary>校验试用密码；密码到期会先自动轮换（轮换后旧密码立即失效）。</summary>
    public bool VerifyTrial(string password)
    {
        lock (_gate)
        {
            var f = Load();
            if (f?.Trial == null) return false;
            RotateTrialIfNeeded(f);
            return ConstantTimeEquals(password.Trim(), f.Trial.Password);
        }
    }

    /// <summary>当前试用密码（登录校验与自用查看共用；到期先轮换）。</summary>
    public (string Password, DateTime NextRotate, int RotateDays) CurrentTrial()
    {
        lock (_gate)
        {
            var f = Load() ?? throw new InvalidOperationException("账号系统未初始化");
            RotateTrialIfNeeded(f);
            var days = Math.Max(1, f.Trial!.RotateDays);
            return (f.Trial.Password, f.Trial.RotatedAt.AddDays(days), days);
        }
    }

    /// <summary>
    /// **只续期、不换密码**：把 RotatedAt 重置成现在，有效期重新算满。
    ///
    /// 和 RotateTrialNow 的区别很重要：轮换会换掉密码（发给试用者的那个就作废了），
    /// 续期只延长期限。用户把密码发出去之后想"再保 10 天"，需要的正是这个——
    /// 不然只能换一个新的再重新通知一遍所有人。
    /// </summary>
    public (string Password, DateTime NextRotate, int RotateDays) RenewTrial()
    {
        lock (_gate)
        {
            var f = Load() ?? throw new InvalidOperationException("账号系统未初始化");
            f.Trial!.RotatedAt = DateTime.Now;
            Save(f);
            var days = Math.Max(1, f.Trial.RotateDays);
            return (f.Trial.Password, f.Trial.RotatedAt.AddDays(days), days);
        }
    }

    /// <summary>手动立刻轮换（自用账号操作）。</summary>
    public string RotateTrialNow()
    {
        lock (_gate)
        {
            var f = Load() ?? throw new InvalidOperationException("账号系统未初始化");
            f.Trial!.Password = NewTrialPassword();
            f.Trial.RotatedAt = DateTime.Now;
            Save(f);
            return f.Trial.Password;
        }
    }

    private void RotateTrialIfNeeded(AccountsFile f)
    {
        var t = f.Trial!;
        var days = Math.Max(1, t.RotateDays);
        if (DateTime.Now - t.RotatedAt < TimeSpan.FromDays(days)) return;
        t.Password = NewTrialPassword();
        t.RotatedAt = DateTime.Now;
        Save(f);
    }

    // ── 读写 ──

    private AccountsFile? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_file)) return null;
            return JsonSerializer.Deserialize<AccountsFile>(File.ReadAllText(_file));
        }
    }

    private void Save(AccountsFile f)
    {
        // 写盘原子化：整本书就一个文件，写一半崩了比没有更糟（与 NovelProject.Save 同一规矩）
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(tmp, _file, overwrite: true);
    }

    // ── 工具 ──

    private static string Hash(string salt, string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + password)));

    /// <summary>试用密码：8 位、去掉易混淆字符（0/O、1/l/I）的可读串。</summary>
    public static string NewTrialPassword()
    {
        const string chars = "23456789ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz";
        var buf = RandomNumberGenerator.GetBytes(8);
        return new string(buf.Select(b => chars[b % chars.Length]).ToArray());
    }

    public static string NewToken(int bytes = 32) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static bool ConstantTimeEquals(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>一个正在线的试用者（按个人码去重后的聚合）。</summary>
public readonly record struct TrialPresence(string Code, int Sessions, DateTime Since, DateTime LastSeen);

/// <summary>登录会话（内存态：服务重启后需重新登录，可接受）。
///
/// ★「在线」的定义是**最近还有请求**，不是"登录过就一直算"：
/// Session 每次被读取时刷新 LastSeen，超过 <see cref="IdleTimeout"/> 没有动静就当离线。
/// 不这么定，试用者直接关掉页面（没人会点退出）名额就永远占着——
/// 3 个名额会在一周内被历史会话卡死。
/// </summary>
public sealed class AuthSessions
{
    /// <summary>同时最多几个试用用户（不同个人码）。自用账号不占名额。</summary>
    public const int MaxConcurrentTrials = 3;

    private sealed class Session
    {
        public string Subject = "";
        public DateTime Created = DateTime.Now;
        public DateTime LastSeen = DateTime.Now;   // 引用类型的字段赋值是原子的，够用了
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>空闲多久算离线。默认 45 分钟；TDXCLAW_TRIAL_IDLE_MIN 可覆盖（实测用它压到分钟级）。</summary>
    public static TimeSpan IdleTimeout { get; } = ReadIdleTimeout();

    private static TimeSpan ReadIdleTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("TDXCLAW_TRIAL_IDLE_MIN");
        if (int.TryParse(raw, out var minutes) && minutes > 0) return TimeSpan.FromMinutes(minutes);
        return TimeSpan.FromMinutes(45);
    }

    /// <summary>主体标识："self" 或 "trial:&lt;四位码&gt;"。</summary>
    public string Issue(string subject)
    {
        var token = AccountStore.NewToken();
        _sessions[token] = new Session { Subject = subject };
        return token;
    }

    /// <summary>
    /// 顺带刷新 LastSeen——「最近用过」本来就是"读会话那一刻"的语义。
    /// 中间件、/api/auth/me、/mcp 都从这里拿主体，放在这里刷新，
    /// 比指望每个调用点都记得单独 Touch 可靠得多。
    /// </summary>
    public string? SubjectOf(string? token)
    {
        if (token == null || !_sessions.TryGetValue(token, out var s)) return null;
        s.LastSeen = DateTime.Now;
        return s.Subject;
    }

    public void Revoke(string token) => _sessions.TryRemove(token, out _);

    /// <summary>当前在线的试用者（按个人码去重，最近活跃的在前）。顺手清掉过期会话。</summary>
    public IReadOnlyList<TrialPresence> ActiveTrials()
    {
        Sweep();
        var cut = DateTime.Now - IdleTimeout;
        return _sessions.Values
            .Where(s => s.Subject.StartsWith("trial:", StringComparison.Ordinal) && s.LastSeen >= cut)
            .GroupBy(s => s.Subject)
            .Select(g => new TrialPresence(
                g.Key["trial:".Length..],
                g.Count(),
                g.Min(x => x.Created),
                g.Max(x => x.LastSeen)))
            .OrderByDescending(t => t.LastSeen)
            .ToList();
    }

    /// <summary>这个码还能不能进：本码已在线（多设备/断线重连）不算新增名额。</summary>
    public bool TrialSlotAvailable(string code)
    {
        var online = ActiveTrials();
        return online.Any(t => t.Code == code) || online.Count < MaxConcurrentTrials;
    }

    /// <summary>把一个试用主体踢下线（自用账号手动释放被卡住的名额）。返回踢掉的会话数。</summary>
    public int DropSubject(string subject)
    {
        var n = 0;
        foreach (var kv in _sessions)
            if (string.Equals(kv.Value.Subject, subject, StringComparison.Ordinal)
                && _sessions.TryRemove(kv.Key, out _)) n++;
        return n;
    }

    /// <summary>把所有试用会话踢下线（不动自用）。</summary>
    public int DropAllTrials()
    {
        var n = 0;
        foreach (var kv in _sessions)
            if (kv.Value.Subject.StartsWith("trial:", StringComparison.Ordinal)
                && _sessions.TryRemove(kv.Key, out _)) n++;
        return n;
    }

    /// <summary>清掉空闲超时的会话（不做后台定时器，每次统计时顺手清）。</summary>
    private void Sweep()
    {
        var cut = DateTime.Now - IdleTimeout;
        foreach (var kv in _sessions)
            if (kv.Value.LastSeen < cut) _sessions.TryRemove(kv.Key, out _);
    }
}

/// <summary>每个主体（自用 / 试用+码）一个独立工作区实例——作品库、聊天记忆、快照互不可见。</summary>
public sealed class WorkspaceRegistry
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly ConcurrentDictionary<string, WorkspaceService> _map = new();

    public WorkspaceRegistry(IHubContext<LiveHub> hub) => _hub = hub;

    public WorkspaceService For(string subject) =>
        _map.GetOrAdd(subject, s => new WorkspaceService(_hub, s));
}
