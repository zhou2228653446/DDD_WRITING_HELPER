using System;
using System.IO;

namespace 编辑器.Tests;

/// <summary>每个测试一个独立临时目录，跑完删掉。测试之间互不污染。</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tdxtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch { /* 临时目录删不掉无所谓 */ }
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);
}
