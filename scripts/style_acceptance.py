# -*- coding: utf-8 -*-
"""作者风格锚定的可重复验收（方案 C）。

用法：
    python style_acceptance.py --profile gemini  --sampling stable --out <目录>
    python style_acceptance.py --profile huoshan --sampling stable --out <目录>

它做两件事，刻意都**不看 style_check 的相似度总分**（单章千余字时签名词/引导语
样本太小，实测分数在 7~52 之间乱跳，当尺子会误判）：

1. 采样方差：第 1 章写回做前情基准，第 2 章保持空章，对 default / stable 两档
   各重复生成 N 次（writeBack=false，prompt 逐字相同），比较句长/逗号/段长的标准差。
2. 连载一致性：把其中一档结果写回第 2 章，顺序生成后续章节，输出跨章风格表
   （句长/逗号/段长/对话/签名词密度 + 标准差）与事实表（硬事实命中、矛盾词）。

所有产物落在 --out 目录，项目是临时书，不碰任何真实项目。
"""
import argparse
import glob
import json
import os
import queue
import re
import statistics
import subprocess
import tempfile
import threading
import time

# ★ 脚本在 scripts/ 下，仓库根是上一级——别写回 dirname(__file__)，
#   那样 DEFAULT_EXE 会指到 scripts/编辑器.Mcp/…（2026-11-24 整理目录时踩过）。
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(
    ROOT, "编辑器.Mcp", "bin", "Debug", "net10.0-windows", "TdxClaw.Mcp.exe")

FULL_OUTLINE = """1998 年冬，东海北岬灯塔。林守义独自守灯，等女儿林小满的信。
四章写他在一个冬天里的四件小事：雨夜巡检、收到信、风浪里重新点灯、给女儿回信。
不出现超自然事件，不出现新人物，不换场景，不写回忆杀。全篇克制、平静、就事论事。"""

CHARACTERS = """林守义：58 岁，北岬灯塔看守人，左腿有旧伤，走路时先迈右脚。话少，从不主动提亡妻。
林小满：16 岁，林守义的女儿，在城里读中学，每月寄一封信回来。本季度她不回家过寒假。"""

BACKGROUND = """时间：1998 年 12 月，入冬后海面常年灰雾，补给船每月来一次。
地点：北岬灯塔，塔顶有一盏编号 7 的铜灯，灯罩裂过一道缝，是林守义自己焊的。
硬事实：灯塔只有林守义一个人；铜灯编号 7；补给船每月一次；女儿每月一封信。"""

CHAPTER_OUTLINE = """第1章 雨夜：林守义冒雨巡检，发现铜灯裂缝又渗油；夜里等补给船，没等到。
第2章 来信：补给船带来林小满的信，说寒假不回来了。他把信压在灯座下。
第3章 风浪：大风刮断电线，铜灯灭了半夜。他摸黑把灯重新点起来。
第4章 回信：天亮后他给女儿回信，只写三行；结尾海上的雾散了。"""

VIEWPOINT = """第三人称限知：只写林守义能看见、听见、感知与推断的内容；
不进入其他人物内心，不交代他不可能知道的信息。"""

TITLES = ["雨夜", "来信", "风浪", "回信"]
INSTRUCTION = ("写本章正文，900～1200 字。只写正文，不要标题、章节号、解释或总结。"
               "人物、时间、地点、道具必须与设定和前文一致。")

FACT_PATTERNS = {
    "林守义": r"林守义",
    "林小满": r"林小满",
    "铜灯": r"铜灯",
    "左腿旧伤": r"左腿",
    "先迈右脚": r"右脚",
    "补给船": r"补给船",
    "编号7": r"编号\s*7|[“\"]7|铸着一个.{0,2}7",
}
CONTRADICTIONS = ["59岁", "十七岁", "编号8", "每周都来", "灯塔有两个人",
                  "女儿回来了", "妻子还在", "林守义今年六十"]


def vis(s):
    return sum(1 for c in s if c.isalnum())


def metrics(t, sigwords):
    sents = [vis(x) for x in re.split(r"[。！？…；”」]+", t) if vis(x) >= 4]
    paras = [vis(x) for x in t.split("\n") if vis(x) >= 4]
    dlg = sum(vis(m) for m in re.findall(r"[“「][^”」]*[”」]", t))
    v = max(1, vis(t))
    return {
        "字数": vis(t),
        "句长": round(statistics.median(sents), 1) if sents else 0,
        "逗号": round(t.count("，") / max(1, len(sents)), 2),
        "段长": round(statistics.median(paras), 1) if paras else 0,
        "对话%": round(dlg / v * 100, 1),
        "签名词": round(sum(t.count(w) for w in sigwords) * 1000.0 / v, 2),
    }


def facts(t):
    hit = [k for k, p in FACT_PATTERNS.items() if re.search(p, t)]
    return hit, [k for k in FACT_PATTERNS if k not in hit], [b for b in CONTRADICTIONS if b in t]


def extract_body(raw):
    """去掉 ai_write 的诊断头与尾部提示，只留模型正文。"""
    if raw.startswith("模型没有返回可用内容") or raw.startswith("读取 API 配置失败"):
        return ""
    body = raw.split("\n\n", 1)[1] if raw.startswith("[") and "\n\n" in raw else raw
    for marker in ("\n\n（只返回文本", "\n\n✅ 已"):
        i = body.find(marker)
        if i >= 0:
            body = body[:i]
    return body.strip()


class McpTimeout(Exception):
    """网关把流式请求挂住了；不是工具错误，重启连接再试。"""


class Mcp:
    def __init__(self, exe):
        self.exe = exe
        self._start()

    def _start(self):
        # 每次重启用**新队列**：旧读线程在旧进程退出后会往旧队列塞 None，
        # 复用同一队列会让新进程误以为自己退出了。
        self.q = queue.Queue()
        self.proc = subprocess.Popen(
            [self.exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
            errors="replace", bufsize=1)
        threading.Thread(target=self._reader, args=(self.proc, self.q), daemon=True).start()
        self.seq = 0

    @staticmethod
    def _reader(proc, q):
        for line in proc.stdout:
            q.put(line)
        q.put(None)

    def call(self, name, args, timeout=180):
        self.seq += 1
        req = {"jsonrpc": "2.0", "id": self.seq, "method": "tools/call",
               "params": {"name": name, "arguments": args}}
        self.proc.stdin.write(json.dumps(req, ensure_ascii=True) + "\n")
        self.proc.stdin.flush()
        while True:
            try:
                line = self.q.get(timeout=timeout)
            except queue.Empty:
                raise McpTimeout(f"{name} 等了 {timeout}s 没有响应")
            if line is None:
                raise RuntimeError("MCP 进程退出")
            try:
                obj = json.loads(line)
            except Exception:
                continue
            if obj.get("id") != self.seq:
                continue
            res = obj.get("result", {})
            content = res.get("content", [])
            return ((content[0].get("text", "") if content else ""), bool(res.get("isError")))

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()

    def restart(self):
        self.close()
        self._start()


# ── A4：拿到一段「合格正文」才算一次样本 ────────────────────────────
#   2026-11-24 的教训：杀虫那篇只出 648 字、断在「寻不着，不在」，
#   旧门槛是 400 字，于是残篇被当成合格样本进了统计（对话 9.7% 就是这么来的）；
#   另一次网关抖动直接「模型没有返回可用内容」，脚本打印一行就进入下一轮，
#   样本悄悄从 2 变 1，中位数照算。这两类都必须重试，重试不掉就显式排除。

END_PUNCT = "。！？…～」”’）】》—"


def looks_truncated(body):
    """结尾没有句末标点/收引号 = 模型在中途停了。"""
    t = (body or "").rstrip()
    return (not t) or (t[-1] not in END_PUNCT)


def gen_body(mcp, args, clean=None, timeout=420, retries=2, min_words=800,
             on_restart=None, log=print):
    """调 ai_write，直到拿到一段合格正文。

    不合格四类：bad/空、过短（< min_words）、断尾（无句末标点）。
    返回 (raw, body, note)；body == "" 表示重试用尽，调用方必须把它排除在统计外。
    """
    clean = clean or extract_body
    reason = ""
    for attempt in range(retries + 1):
        try:
            raw, _bad = mcp.call("ai_write", args, timeout=timeout)
        except McpTimeout as ex:
            reason = f"超时 {ex}"
            log(f"  ⚠ {reason} → 重启连接后重试")
            try:
                mcp.restart()
                if on_restart:      # 新进程没有「当前项目」，调用方负责恢复会话
                    on_restart()
            except Exception:
                pass
            continue
        raw = raw or ""
        body = clean(raw)
        if not body:
            reason = "无可用内容"
        elif vis(body) < min_words:
            reason = f"过短 {vis(body)} 字"
        elif looks_truncated(body):
            reason = "断尾"
        else:
            note = f"重试 {attempt} 次后成功（上次：{reason}）" if attempt else ""
            if note:
                log(f"  ↻ {note}")
            return raw, body, note
        log(f"  ✗ 第 {attempt + 1} 次不合格：{reason}")

    return "", "", f"{reason}，重试 {retries} 次仍不合格 → 已排除，不进统计"


def say(*parts):
    print(" ".join(str(x) for x in parts), flush=True)


def save(out, name, text):
    with open(os.path.join(out, name), "w", encoding="utf-8") as f:
        f.write(text)


def stdev(xs):
    return round(statistics.stdev(xs), 2) if len(xs) >= 2 else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--profile", default="", help="api_profiles.json 里的配置名；留空用当前启用")
    ap.add_argument("--sampling", default="stable", help="连载用档：default / stable / legacy")
    ap.add_argument("--compare", default="stable,default", help="方差对照档，逗号分隔。网关连跑易挂起时只填一档，" +
                    "配合固定 --baseline 分两轮跑完再合并")
    ap.add_argument("--repeat", type=int, default=3, help="每档重复生成次数")
    ap.add_argument("--chapters", type=int, default=4, help="连载章数（2~4）")
    ap.add_argument("--style", default="余华", help="绑定的作者风格档案 Id")
    ap.add_argument("--out", default="", help="产物目录")
    ap.add_argument("--timeout", type=int, default=120, help="单次生成等待上限（秒）；网关挂起时别拖垮整轮")
    ap.add_argument("--baseline", default="", help="固定前情基准：第 1 章正文 txt 路径。给了就直接写回、不再生成，" +
                    "使跨轮 / 跨档的方差样本可以合并比较；首次运行会在产物目录留 baseline.txt")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    a = ap.parse_args()

    tag = (a.profile or "active") + "-" + a.sampling
    out = a.out or os.path.join(tempfile.gettempdir(), "tdx-acceptance-" + tag)
    os.makedirs(out, exist_ok=True)
    say("产物目录:", out)

    profile_dir = os.path.join(os.path.expandvars(r"%APPDATA%\TdxClaw"), "author_profiles")
    pf = os.path.join(profile_dir, a.style + ".json")
    sigwords = ([w["Word"] for w in json.load(open(pf, encoding="utf-8")).get("SignatureWords", [])]
                if os.path.exists(pf) else [])
    say("签名词:", sigwords[:8])

    mcp = Mcp(a.exe)
    base = {"profile": a.profile} if a.profile else {}
    name = "验收" + re.sub(r"\W", "", a.profile or "active") + time.strftime("%H%M%S")
    t, err = mcp.call("project_create", {
        "name": name, "directory": out, "description": "风格验收临时项目，可删",
        "full_outline": FULL_OUTLINE, "characters": CHARACTERS,
        "background": BACKGROUND, "chapter_outline": CHAPTER_OUTLINE,
        "narrative_viewpoint": VIEWPOINT,
    })
    if err:
        say("建书失败:", t[:400])
        mcp.close()
        return
    pm = re.search(r"文件：(.+?\.tdxproj)", t)
    proj = pm.group(1).strip() if pm else (glob.glob(os.path.join(out, "*.tdxproj")) or [""])[0]
    mcp.call("style_profile_bind", {"id": a.style})

    def generate(chapter_no, sampling, write_back):
        args = dict(base)
        args.update({"task": "continue", "number": chapter_no,
                     "writeBack": write_back, "maxTokens": 2600,
                     "instruction": INSTRUCTION, "sampling": sampling})
        raw, bad = mcp.call("ai_write", args, timeout=a.timeout)
        save(out, f"raw_c{chapter_no}_{sampling}_{int(time.time() * 1000) % 100000}.txt", raw)
        head = raw.split("\n")[0] if raw else ""
        return extract_body(raw), head, bad

    timeouts = [0]

    def safe_generate(chapter_no, sampling, write_back):
        """网关挂起就重启连接、重新打开项目，跳过这一次而不是拖垮整轮。"""
        try:
            return generate(chapter_no, sampling, write_back)
        except (McpTimeout, RuntimeError) as ex:
            timeouts[0] += 1
            say("  ⚠", str(ex), "→ 重启连接并恢复项目，跳过本次")
            mcp.restart()
            if proj:
                mcp.call("project_open", {"path": proj})
            return "", "", True

    # ── 第 1 章：写回做前情基准 ──
    # ★ 方差对照要求「同一 prompt 逐字相同」。如果每轮重新生成第 1 章，
    #   跨轮的前情就不同，两档数据永远合不到一张表里——哪怕网关全成功。
    #   所以支持固定基准：首次生成后落盘 baseline.txt，之后用 --baseline 指回来。
    mcp.call("chapter_create", {"title": TITLES[0]})
    baseline_src = "本轮生成"
    if a.baseline and os.path.exists(a.baseline):
        body = open(a.baseline, encoding="utf-8").read()
        baseline_src = a.baseline
        mcp.call("chapter_write", {"number": 1, "content": body, "mode": "append"})
        say("[第1章] 用固定前情基准:", a.baseline, len(body), "字")
        bad = False
    else:
        if a.baseline:
            say("  ⚠ 指定的基准文件不存在，退回本轮生成")
        say("[第1章] 生成前情基准…")
        body, head, bad = safe_generate(1, a.sampling, True)
        if not body:
            # 基准章挂了会让整轮连载失去前情、事实表全空，值得重试一次。
            say("  ⚠ 前情基准没生成，重试一次…")
            body, head, bad = safe_generate(1, a.sampling, True)
        say("  ", "失败" if bad or not body else "OK", len(body), "字 |", head[:150])
        if body:
            save(out, "baseline.txt", body)
            say("  基准已存:", os.path.join(out, "baseline.txt"), "（后续轮用 --baseline 指回来）")
    baseline_ok = bool(body)
    if not baseline_ok:
        say("  ⚠ 前情基准失败，本轮连载数据无前情，仅供参考")
    chapters = {1: body}

    # ── 第 2 章：同 prompt 重复，测采样方差 ──
    mcp.call("chapter_create", {"title": TITLES[1]})
    stability = {}
    # 挂起 = 该次没有产出，不属于方差样本，重试即可；早先「连续 2 次失败就跳过整档」
    # 会在网关恰好连挂两次时白白丢掉一整档数据，这才是前几轮测不成的直接原因。
    # 现在改成「凑够 repeat 个成功样本为止」，另设连续失败上限防死等。
    for mode in [x.strip() for x in a.compare.split(",") if x.strip()]:
        rows = []
        attempts = 0
        consecutive = 0
        max_attempts = a.repeat * 3 + 2
        while len(rows) < a.repeat and attempts < max_attempts:
            attempts += 1
            say(f"[方差] {mode} 第 {len(rows) + 1}/{a.repeat} 次（尝试 {attempts}）…")
            b, h, bad = safe_generate(2, mode, False)
            if b:
                consecutive = 0
                rows.append(metrics(b, sigwords))
                save(out, f"stability_{mode}_{len(rows)}.txt", b)
            else:
                consecutive += 1
                say("    生成失败，重试")
                if consecutive >= 4:
                    say(f"    {mode} 连续 {consecutive} 次失败，放弃该档剩余样本")
                    break
        stability[mode] = rows

    # 取连载档的第一次结果写回第 2 章；该档全失败时退回任何有结果的档，
    # 否则第 2 章空着会让后续章节丢失前情，事实表还会多一行假缺失。
    chosen_key = a.sampling if (a.sampling in stability and stability[a.sampling]) else next(
        (k for k, v in stability.items() if v), None)
    chosen_files = (sorted(glob.glob(os.path.join(out, f"stability_{chosen_key}_*.txt")))
                    if chosen_key else [])
    chosen = open(chosen_files[0], encoding="utf-8").read() if chosen_files else ""
    if not chosen:
        say("  ⚠ 方差阶段没有任何成功样本，第 2 章留空；连载数据缺前情，仅供参考")
    if chosen:
        mcp.call("chapter_write", {"number": 2, "content": chosen, "mode": "append"})
    chapters[2] = chosen

    # ── 第 3..N 章：连载 ──
    for n in range(3, min(a.chapters, len(TITLES)) + 1):
        mcp.call("chapter_create", {"title": TITLES[n - 1]})
        say(f"[第{n}章] 生成…")
        b, h, bad = safe_generate(n, a.sampling, True)
        say("  ", "失败" if bad or not b else "OK", len(b), "字 |", h[:150])
        chapters[n] = b

    mcp.close()

    # 用项目文件里的正文覆盖（写回的章节以落盘为准）
    if proj and os.path.exists(proj):
        d = json.load(open(proj, encoding="utf-8"))
        for c in d.get("Chapters", []):
            n, body = c.get("ChapterNumber"), c.get("Content", "") or ""
            if body:
                chapters[n] = body
                save(out, f"ch{n}.txt", body)

    # ── 报告 ──
    keys = ["句长", "逗号", "段长", "对话%", "签名词"]
    lines = [f"# 风格验收 · 配置={a.profile or '当前启用'} · 连载采样={a.sampling}", "",
             "> 相似度总分不进表：单章样本下噪声过大，不能当一致性判据。", "",
             f"- 网关挂起 / 连接重启次数：{timeouts[0]}",
             f"- 前情基准（第 1 章）：{'成功' if baseline_ok else '失败（连载数据无前情）'}｜来源：{baseline_src}",
             f"- 第 2 章正文取自：{chosen_key or '无（方差阶段全失败）'}", "",
             "## 1. 采样方差（第 2 章，同一 prompt 重复生成）", "",
             "| 档 | 成功次数 | " + " | ".join(f"{k} 中位±σ" for k in keys) + " |",
             "|---|---|" + "---|" * len(keys)]
    for mode, rows in stability.items():
        if not rows:
            lines.append(f"| {mode} | 0 | " + " | ".join(["—"] * len(keys)) + " |")
            continue
        cells = []
        for k in keys:
            vals = [r[k] for r in rows]
            cells.append(f"{statistics.median(vals):.2f}±{stdev(vals):.2f}")
        lines.append(f"| {mode} | {len(rows)} | " + " | ".join(cells) + " |")
    lines += ["", "## 2. 跨章风格表", "",
              "| 章 | " + " | ".join(["字数"] + keys) + " |",
              "|---|" + "---|" * (len(keys) + 1)]
    series = {k: [] for k in keys}
    for n in sorted(chapters):
        m = metrics(chapters[n], sigwords)
        if not m["字数"]:
            continue
        for k in keys:
            series[k].append(m[k])
        lines.append(f"| {n} | " + " | ".join(str(m[k]) for k in ["字数"] + keys) + " |")
    lines.append("| **σ** | — | " + " | ".join(f"{stdev(series[k]):.2f}" for k in keys) + " |")
    lines += ["", "## 3. 事实表", "", "| 章 | 命中硬事实 | 缺失 | 矛盾 |", "|---|---|---|---|"]
    for n in sorted(chapters):
        hit, miss, bad = facts(chapters[n])
        lines.append(f"| {n} | {'、'.join(hit)} | {'、'.join(miss) or '无'} | {'、'.join(bad) or '无'} |")
    lines += ["", "## 4. 产物", "", f"- 临时项目：`{proj}`",
              "- `ch*.txt` 落盘正文、`stability_*.txt` 方差样本、`raw_*.txt` 原始响应", ""]

    report = "\n".join(lines)
    save(out, "report.md", report)
    say("\n" + report)
    say("\nDONE", out)


if __name__ == "__main__":
    main()
