# -*- coding: utf-8 -*-
"""A/B：锚定块「报 CV 数字」 vs 「句长跟场景走 + 长短两极样本」。

用法：
  python ab_anchor.py --style 余华 --repeat 3 ^
      --baseline "%TEMP%/tdx-acceptance-baseline.txt" --out "%TEMP%/tdx-ab-anchor"

只调 ai_write 的 anchor 参数，不动默认行为；产物含 6 篇正文 + report.md。
"""
import argparse
import glob
import json
import os
import re
import statistics
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import style_acceptance as sa  # noqa: E402


def sent_lengths(t):
    return [sa.vis(x) for x in re.split(r"[。！？…；”」]+", t) if sa.vis(x) >= 4]


def extra_metrics(t):
    lens = sent_lengths(t)
    if not lens:
        return {}
    short = [x for x in lens if x <= 8]
    # 最长连续短句（<=8 字）句数：机械顿挫的直接证据
    run = best = 0
    for x in lens:
        run = run + 1 if x <= 8 else 0
        best = max(best, run)
    return {
        "CV": round(statistics.stdev(lens) / statistics.mean(lens), 2) if len(lens) > 2 else 0,
        "短句%": round(len(short) * 100.0 / len(lens), 1),
        "连短": best,
        "句数": len(lens),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--profile", default="gemini")
    ap.add_argument("--style", default="余华")
    ap.add_argument("--repeat", type=int, default=3)
    ap.add_argument("--modes", default="number,scene,spread")
    ap.add_argument("--baseline", default="")
    ap.add_argument("--out", default="")
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--exe", default=sa.DEFAULT_EXE)
    a = ap.parse_args()

    out = a.out or os.path.join(tempfile.gettempdir(), "tdx-ab-anchor")
    os.makedirs(out, exist_ok=True)
    print("产物目录:", out)

    pf = os.path.join(os.path.expandvars(r"%APPDATA%\TdxClaw"), "author_profiles",
                      a.style + ".json")
    prof = json.load(open(pf, encoding="utf-8"))
    sig = [w["Word"] for w in prof.get("SignatureWords", [])]
    target = {
        "句长": prof["SentenceMedian"],
        "CV": prof["SentenceCv"],
        "逗号": round(prof["CommaPerSentence"], 2),
        "段长": prof["ParagraphMedian"],
        "对话%": round(prof["DialogueRatio"] * 100, 1),
        "短句%": round(prof["ShortSentenceRatio"] * 100, 1),
    }
    print("档案目标:", target)

    mcp = sa.Mcp(a.exe)
    try:
        name = "AB锚定" + time.strftime("%H%M%S")
        t, err = mcp.call("project_create", {
            "name": name, "directory": out, "description": "A/B 锚定对照临时项目，可删",
            "full_outline": sa.FULL_OUTLINE, "characters": sa.CHARACTERS,
            "background": sa.BACKGROUND, "chapter_outline": sa.CHAPTER_OUTLINE,
            "narrative_viewpoint": sa.VIEWPOINT,
        }, timeout=180)
        if err:
            print("建书失败:", t[:400])
            return 1
        m = re.search(r"文件：(.+?\.tdxproj)", t)
        proj = m.group(1).strip() if m else (glob.glob(os.path.join(out, "*.tdxproj")) or [""])[0]
        mcp.call("project_open", {"path": proj}, timeout=120)
        mcp.call("style_profile_bind", {"id": a.style})

        # 固定前情：直接写回第 1 章，不再调模型
        mcp.call("chapter_create", {"title": sa.TITLES[0]})
        base_text = open(a.baseline, encoding="utf-8").read()
        mcp.call("chapter_write", {"number": 1, "content": base_text, "mode": "append"})
        print("[第1章] 固定前情", len(base_text), "字")
        mcp.call("chapter_create", {"title": sa.TITLES[1]})

        results = {}
        for mode in [x.strip() for x in a.modes.split(",") if x.strip()]:
            rows = []
            attempts = 0
            while len(rows) < a.repeat and attempts < a.repeat * 3 + 2:
                attempts += 1
                print(f"[{mode}] 生成 {len(rows) + 1}/{a.repeat}（尝试 {attempts}）…", flush=True)
                args = {"task": "continue", "number": 2, "writeBack": False,
                        "maxTokens": 2600, "instruction": sa.INSTRUCTION,
                        "sampling": "default"}
                if a.profile:
                    args["profile"] = a.profile
                if mode != "number":
                    args["anchor"] = mode
                try:
                    raw, bad = mcp.call("ai_write", args, timeout=a.timeout)
                except Exception as ex:                    # 超时/连接断：重启后继续
                    print("  ⚠", type(ex).__name__, str(ex)[:80], "→ 重启连接")
                    sa.Mcp.restart(mcp)
                    mcp.call("project_open", {"path": proj}, timeout=120)
                    continue
                sa.save(out, f"raw_{mode}_{len(rows) + 1}.txt", raw)
                body = sa.extract_body(raw)
                head = raw.split("\n")[0] if raw else ""
                if not body:
                    print("  失败或空正文:", head[:120])
                    continue
                mm = sa.metrics(body, sig)
                mm.update(extra_metrics(body))
                rows.append(mm)
                sa.save(out, f"ab_{mode}_{len(rows)}.txt", body)
                print("  ", {k: mm[k] for k in ("句长", "CV", "逗号", "段长", "对话%", "短句%", "连短")},
                      "字数", mm["字数"], "|", head[:110])
            results[mode] = rows

        keys = ["句长", "CV", "逗号", "段长", "对话%", "短句%", "连短", "字数"]
        lines = [f"# A/B 锚定对照 · {a.style}", "",
                 "> A（number）= 锚定块报「中位句长 + CV 数字 + 长短要有起伏」；",
                 "> B（scene）= 句长跟场景走 + 长短两极样本。同一前情、同一采样（default）、同一 maxTokens。", "",
                 "| 指标 | 档案目标 | A 中位 | A 范围 | B 中位 | B 范围 |", "|---|---|---|---|---|---|"]
        for k in keys:
            if not results["number"] or not results["scene"]:
                continue
            av = [r[k] for r in results["number"]]
            bv = [r[k] for r in results["scene"]]
            tgt = target.get(k, "—")
            lines.append(f"| {k} | {tgt} | {statistics.median(av):g} | "
                         f"{min(av):g}~{max(av):g} | {statistics.median(bv):g} | {min(bv):g}~{max(bv):g} |")
        lines += ["", "> 样本数：A = %d，B = %d。CV 只由成功产出的正文计算，挂起不计。" %
                  (len(results["number"]), len(results["scene"]))]
        open(os.path.join(out, "report.md"), "w", encoding="utf-8").write("\n".join(lines))
        print("\n".join(lines))
        print("\n报告:", os.path.join(out, "report.md"))
        return 0
    finally:
        try:
            mcp.close()
        except Exception:
            pass


if __name__ == "__main__":
    sys.exit(main())
