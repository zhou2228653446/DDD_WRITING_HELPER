# -*- coding: utf-8 -*-
"""同场景多作者对照：一本书、一份固定前情、一章任务，只换绑定的作者档案。

用途：验证「档案分档」是否真的落地——马伯庸的段落中位 59 字、对话 23.5%，
余华的段中位 30 字、对话 13.6%、每章一处空转，同一段故事里应该读出两种形态。

与 ab_anchor.py 的区别：那个只比「同一位作者的两种锚定措辞」，
这个比「不同作者档案 + 各自自动分档后的完整锚定块」。

用法：
  python scripts/ab_author.py --styles 马伯庸,余华 --repeat 2
"""
import argparse
import glob
import io
import json
import os
import re
import statistics
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import style_acceptance as sa  # noqa: E402

# ── 中性场景：《账》 ────────────────────────────────────────────────
# 选它的理由（对着两位作者的特征挑的，不是随便写的）：
#   · 有第二个人物在场（赵秀兰开门），马伯庸的 23.5% 对话才有地方落；
#   · 场景是楼道门口、蜂窝煤、腌菜坛子，余华的长段留白有东西可写；
#   · 只有一件事（要账），没有打斗和反转，不会逼着任何一方写成情节剧。
FULL_OUTLINE = """1997 年冬，江北县城。陈广生替纺织厂跑供销，年底要去收一笔压了三年的旧账，
欠款人是他的老同学孙德海。
四章写他一天里的四件事：骑车穿过县城、在孙家门口等人、空手回家、晚上和妻子算账。
不出现暴力冲突，不出现打斗追债，不换主线人物，不写回忆杀。"""

CHARACTERS = """陈广生：45 岁，县纺织厂供销科，右手小指短一截（早年被机器轧掉）。说话前先笑一下，声音不高。
孙德海：46 岁，三年前下海做建材，欠厂里三千二百块。个子高，说话时眼睛爱往别处看。
赵秀兰：43 岁，孙德海的妻子，在街道缝纫组做事。开门时手上还戴着一只铜顶针。
陈妻：43 岁，在县医院做护工，晚上回家做饭，账算得比陈广生清楚。"""

BACKGROUND = """时间：1997 年 12 月，县城未通暖气，家家烧蜂窝煤。
地点：纺织厂家属院，三栋三层红砖楼，楼道里堆着白菜、旧自行车和腌菜坛子。
硬事实：欠款三千二百元，欠了三年；孙德海今年春天去了南方；赵秀兰在街道缝纫组；
陈广生骑一辆二八自行车；厂里这个月已经停产半个月。"""

CHAPTER_OUTLINE = """第1章 出门：陈广生骑车穿过县城去家属院，路上在水果摊买了两斤橘子。
第2章 门口：他敲开孙家的门，赵秀兰开的；孙德海不在家。两人在楼道门口说话。
第3章 回家：他骑车回去，路上绕开了厂门口。
第4章 算账：晚上他和妻子在灯下算这个月的账。"""

VIEWPOINT = """第三人称限知：只写陈广生看得见、听得到、能推断的内容；
不进入其他人物的内心，不交代他不可能知道的信息。"""

TITLES = ["出门", "门口", "回家", "算账"]

INSTRUCTION = ("写本章正文，1200～1600 字。只写正文，不要标题、章节号、解释或总结。"
               "人物、时间、地点、道具必须与设定和前文一致。"
               "不要写「他知道」「他明白」「这意味着」这类解释句，用动作、对话和可观察的细节推进。")


def clean_body(raw):
    """去诊断头 + 尾部提示 + 混进来的「思考过程」。"""
    body = sa.extract_body(raw)
    # ★ 网关偶尔让模型把推理过程一起吐出来（实测第 4 章出现过 8000 字，
    #   开头是「分析任务 / 情节规划 / 字数检查」）。定位到「直接输出正文」就切。
    m = re.search(r"\*{0,2}直接输出正文\*{0,2}[。：:]?\s*", body)
    if m and m.start() < 400:
        body = body[m.end():]
    body = re.sub(r"（注：[\s\S]*?）\s*$", "", body)
    return body.strip()


def para_stats(t):
    ps = [sa.vis(x) for x in t.split("\n") if sa.vis(x) >= 4]
    if not ps:
        return {}
    ps.sort()
    n = len(ps)
    q = lambda r: ps[min(n - 1, int(n * r))]
    line = [x for x in t.split("\n") if sa.vis(x) >= 4]
    one = sum(1 for x in line if len(re.findall(r"[。！？…]", x)) <= 1)
    liang = len(re.findall(
        r"[一二三四五六七八九十半两几]\s*(?:只|个|片|张|条|根|块|把|撮|缸|碗|篮|袋|桶|寸|步|"
        r"声|层|股|阵|圈|道|截|点|丝|颗|粒|口|双|副|身|脸|手|脚|头)", t))
    v = max(1, sa.vis(t))
    sents = [sa.vis(x) for x in re.split(r"[。！？…；”」]+", t) if sa.vis(x) >= 4]
    cv = statistics.stdev(sents) / statistics.mean(sents) if len(sents) > 2 else 0
    return {"段数": n, "P50": q(.5), "P75": q(.75), "P90": q(.9), "max": ps[-1],
            "单句段%": round(one * 100.0 / n, 1), "量词": round(liang * 1000.0 / v, 1),
            "CV": round(cv, 2)}


def load_profile(style):
    p = os.path.join(os.path.expandvars(r"%APPDATA%\TdxClaw"), "author_profiles", style + ".json")
    d = json.load(io.open(p, encoding="utf-8"))
    return d


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--styles", default="马伯庸,余华")
    ap.add_argument("--repeat", type=int, default=2)
    ap.add_argument("--profile", default="gemini")
    ap.add_argument("--sampling", default="default")
    ap.add_argument("--max-tokens", type=int, default=20000)
    ap.add_argument("--baseline", default="", help="固定前情 txt；不给就先生成第 1 章并复用")
    ap.add_argument("--out", default="")
    ap.add_argument("--timeout", type=int, default=420)
    ap.add_argument("--retries", type=int, default=2, help="不合格返回的重试次数（A4）")
    ap.add_argument("--min-words", type=int, default=800, help="低于此字数视为残篇（A4）")
    ap.add_argument("--exe", default=sa.DEFAULT_EXE)
    a = ap.parse_args()

    styles = [x.strip() for x in a.styles.split(",") if x.strip()]
    out = a.out or os.path.join(tempfile.gettempdir(), "tdx-ab-author")
    os.makedirs(out, exist_ok=True)
    print("产物目录:", out, flush=True)

    mcp = sa.Mcp(a.exe)
    proj = ""
    try:
        name = "多作者对照" + time.strftime("%H%M%S")
        t, err = mcp.call("project_create", {
            "name": name, "directory": out, "description": "多作者同场景对照，可删",
            "full_outline": FULL_OUTLINE, "characters": CHARACTERS,
            "background": BACKGROUND, "chapter_outline": CHAPTER_OUTLINE,
            "narrative_viewpoint": VIEWPOINT,
        }, timeout=180)
        if err:
            print("建书失败:", t[:400])
            return 1
        m = re.search(r"文件：(.+?\.tdxproj)", t)
        proj = m.group(1).strip() if m else max(
            glob.glob(os.path.join(out, "*.tdxproj")), key=os.path.getmtime)
        mcp.call("project_open", {"path": proj}, timeout=120)
        print("项目:", os.path.basename(proj), flush=True)

        # ── 固定前情：不绑任何档案生成第 1 章，之后两人看到的是同一段文字 ──
        mcp.call("chapter_create", {"title": TITLES[0]}, timeout=60)
        base_path = a.baseline or os.path.join(out, "baseline_ch1.txt")
        if a.baseline and os.path.exists(a.baseline):
            base_text = io.open(a.baseline, encoding="utf-8").read()
            print("[第1章] 用既有前情 %d 字" % sa.vis(base_text), flush=True)
        elif os.path.exists(base_path):
            base_text = io.open(base_path, encoding="utf-8").read()
            print("[第1章] 复用 %s（%d 字）" % (os.path.basename(base_path), sa.vis(base_text)), flush=True)
        else:
            print("[第1章] 无档案生成前情…", flush=True)
            args = {"task": "continue", "number": 1, "writeBack": False,
                    "maxTokens": a.max_tokens, "instruction": INSTRUCTION,
                    "sampling": a.sampling}
            if a.profile:
                args["profile"] = a.profile
            raw, base_text, note = sa.gen_body(
                mcp, args, clean=clean_body, timeout=a.timeout,
                retries=a.retries, min_words=a.min_words,
                on_restart=lambda: mcp.call("project_open", {"path": proj}, timeout=120))
            sa.save(out, "raw_baseline_ch1.txt", raw or "")
            if not base_text:
                print("前情生成失败:", note)
                return 1
            sa.save(out, "baseline_ch1.txt", base_text)
            print("[第1章] 生成 %d 字%s" % (sa.vis(base_text), "　" + note if note else ""), flush=True)

        mcp.call("chapter_write", {"number": 1, "content": base_text, "mode": "append"}, timeout=60)
        mcp.call("chapter_create", {"title": TITLES[1]}, timeout=60)
        print("[第1章] 已写回作为前情\n", flush=True)

        # ── 逐个档案生成第 2 章 ──
        # styles 里可以用「A@B」表示混合档：句子/用词学 A，段落与对话节奏取 B。
        rows = []
        skipped = []
        tags = []
        for spec in styles:
            style, _, rhythm = spec.partition("@")
            style, rhythm = style.strip(), rhythm.strip()
            prof = load_profile(style)
            bindArgs = {"id": style}
            if rhythm:
                bindArgs["rhythmFrom"] = rhythm
            ok, _ = mcp.call("style_profile_bind", bindArgs)
            print("=" * 60, flush=True)
            print("绑定:", style + (f"（节奏取自 {rhythm}）" if rhythm else ""),
                  "| 目标 句长", prof["SentenceMedian"],
                  "段长", prof["ParagraphMedian"], "对话%", round(prof["DialogueRatio"] * 100, 1),
                  "| 短段", round(prof.get("ShortParagraphRatio", 0) * 100, 1),
                  "留白", round(prof.get("QuietLongRatio", 0) * 100, 1), flush=True)
            tag = style + (f"x{rhythm}" if rhythm else "")
            tags.append(tag)
            for r in range(1, a.repeat + 1):
                print(f"[{tag}] 生成 {r}/{a.repeat}…", flush=True)
                args = {"task": "continue", "number": 2, "writeBack": False,
                        "maxTokens": a.max_tokens, "instruction": INSTRUCTION,
                        "sampling": a.sampling}
                if a.profile:
                    args["profile"] = a.profile
                raw, body, note = sa.gen_body(
                    mcp, args, clean=clean_body, timeout=a.timeout,
                    retries=a.retries, min_words=a.min_words,
                    on_restart=lambda: (mcp.call("project_open", {"path": proj}, timeout=120),
                                        mcp.call("style_profile_bind", bindArgs)))
                sa.save(out, f"raw_{tag}_{r}.txt", raw or "")
                if not body:
                    # ★ 不合格样本不许静默跳过：明确记一行，并在报告里标出来
                    print("  ✗ 样本作废：", note, flush=True)
                    skipped.append(f"{tag} #{r}：{note}")
                    continue
                head = (raw or "").split("\n")[0]
                print("  头部:", head[:150], flush=True)
                if note:
                    print("  ↻", note, flush=True)
                sa.save(out, f"ch2_{tag}_{r}.txt", body)
                m = sa.metrics(body, [])
                m.update(para_stats(body))
                m["作者"] = tag
                m["轮次"] = r
                rows.append(m)
                print("  ", {k: m[k] for k in ("字数", "句长", "CV", "逗号", "P50", "P90", "max",
                                               "单句段%", "对话%", "量词")}, flush=True)

        # ── 报告 ──
        io.open(os.path.join(out, "rows.json"), "w", encoding="utf-8").write(
            json.dumps(rows, ensure_ascii=False, indent=1))
        keys = ["字数", "句长", "CV", "逗号", "P50", "P75", "P90", "max",
                "单句段%", "对话%", "量词", "段数"]
        lines = ["# 多作者同场景对照 · " + " / ".join(styles), "",
                 "> 同一本书、同一份第 1 章前情、同一段第 2 章指令，只换绑定的作者档案。",
                 "> `A@B` = 混合档：句子与用词学 A，段落与对话节奏取 B。", "",
                 "| 作者 | " + " | ".join(keys) + " |",
                 "|---" * (len(keys) + 1) + "|"]
        for tag in tags:
            sub = [x for x in rows if x["作者"] == tag]
            if not sub:
                continue
            for x in sub:
                lines.append("| %s #%d | " % (tag, x["轮次"]) +
                             " | ".join(str(x.get(k, "")) for k in keys) + " |")
            if len(sub) > 1:
                lines.append("| **%s 中位** | " % tag + " | ".join(
                    "%g" % statistics.median([x[k] for x in sub]) for k in keys) + " |")
        total = len(styles) * a.repeat
        lines.append("")
        if skipped:
            lines.append(f"样本 {len(rows)}/{total} 合格；{len(skipped)} 篇不合格已排除（不进中位数）：")
            lines += [f"- {s}" for s in skipped]
        else:
            lines.append(f"样本 {len(rows)}/{total} 全部合格"
                         f"（A4：bad/空、<{a.min_words} 字、断尾均自动重试）")
        pf = os.path.join(out, "report.md")
        io.open(pf, "w", encoding="utf-8").write("\n".join(lines))
        print()
        print("\n".join(lines))
        print("\n报告:", pf)
        return 0
    finally:
        try:
            mcp.close()
        except Exception:
            pass


if __name__ == "__main__":
    sys.exit(main())
