using System.Runtime.CompilerServices;

// 测试工程要能调到 internal 的 ToolResult——MCP 的工具方法都返回它，
// 不开放的话章节管理那一层就没法写断言，而那层恰好是最需要回归保护的地方
// （删章重排一旦算错章号，整本书的定位都会错位）。
[assembly: InternalsVisibleTo("编辑器.Tests")]
// Web 版要复用同一份 MCP 分发逻辑（HandleRequestAsync / SetNotifier），
// 不能让 HTTP 那侧再抄一遍——抄一遍就迟早不一致。
[assembly: InternalsVisibleTo("编辑器.Web")]
// Web 工程的 AssemblyName 改成了 TdxClaw.Web（发布成 ASCII 文件名，方便开机自启
// 脚本精确定位主程序），友元程序集名认的是程序集名而不是项目名，所以这里要补一条。
[assembly: InternalsVisibleTo("TdxClaw.Web")]
