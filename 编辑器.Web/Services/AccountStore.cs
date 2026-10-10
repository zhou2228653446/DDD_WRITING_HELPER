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
    public (string Password, DateTime NextRotate) CurrentTrial()
    {
        lock (_gate)
        {
            var f = Load() ?? throw new InvalidOperationException("账号系统未初始化");
            RotateTrialIfNeeded(f);
            var days = Math.Max(1, f.Trial!.RotateDays);
            return (f.Trial.Password, f.Trial.RotatedAt.AddDays(days));
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

/// <summary>登录会话（内存态：服务重启后需重新登录，可接受）。</summary>
public sealed class AuthSessions
{
    private sealed record Session(string Subject, DateTime Created);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>主体标识："self" 或 "trial:&lt;四位码&gt;"。</summary>
    public string Issue(string subject)
    {
        var token = AccountStore.NewToken();
        _sessions[token] = new Session(subject, DateTime.Now);
        return token;
    }

    public string? SubjectOf(string? token) =>
        token != null && _sessions.TryGetValue(token, out var s) ? s.Subject : null;

    public void Revoke(string token) => _sessions.TryRemove(token, out _);
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
