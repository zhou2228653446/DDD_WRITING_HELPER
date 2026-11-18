# -*- coding: utf-8 -*-
"""单章风格实测：建临时项目 → 生成第 1 章 → 四项体检。

体检项（刻意不看 similarity 总分，单章千余字时它是噪声）：
  1. 指纹贴合：句长 / 每句逗号 / 段长 / 对话占比 对比档案目标
  2. 套话堆砌：签名词逐词实际密度 vs 档案目标（超过 2 倍即标记）
  3. 串名：生成文本里有没有混进原作者书里的角色名
  4. 引导语：实际用的「X道」是不是档案里的那批

用法：
  python style_probe.py --style 杀虫队队员
  python style_probe.py --style 杀虫队队员 --sampling stable --out <目录>
"""
import argparse
import json
import os
import re
import sys
import tempfile
import time
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import style_acceptance as sa

# ── 规则怪谈设定：与《十日终焉》同类型，但不复用它的任何人物与情节 ──
FULL_OUTLINE = """一栋没有出口的旧公寓，十二名参与者被编号。每层楼墙上贴着「住户须知」，
违反规则的人会在当夜消失，次日只留下一枚编号牌。
第一章只写：众人在一楼大厅醒来、读到须知、有人不信邪违反第一条、当夜死亡。
不解释公寓来历，不给万能金手指，不出现爱情线，不写回忆杀。"""

CHARACTERS = """周砚：27 岁，编号 07，前审计员，习惯先看条文再行动；话少，记性好。
老曹：45 岁，编号 03，货车司机，暴躁，不信规则，第一个违反的人。
林小满：19 岁，编号 11，大学生，胆小但观察细，最先发现规则措辞有歧义。"""

BACKGROUND = """地点：终焉公寓，七层，无窗，只有一部停用的电梯和一道锁死的大门。
硬事实：一共十二名参与者；每人胸口别着一枚铁皮编号牌；大厅墙上贴着七条「住户须知」；
第一条是「晚十点后不得离开自己的房间」；违反者次日只留下一枚编号牌，人不见踪影。
须知用宋体印在泛黄的纸上，右下角有一块咖啡渍。"""

CHAPTER_OUTLINE = """第1章 须知：众人醒来，读规则，老曹不信，晚上十点后出门抽烟，当夜消失。"""

VIEWPOINT = """第三人称限知：只写周砚能看见、听见、推断的内容；
不进入其他人物的内心，不交代周砚不可能知道的信息。"""

INSTRUCTION = ("这是全书第一章，从第一句开始写起，不承接任何前文。写 1000～1300 字。"
               "只写正文，不要标题、章节号、解释或总结。"
               "七条须知要照抄进正文，用独立段落或引号呈现，不要改写成概述。"
               "用动作、对话和可观察的细节推进，不要写「他知道」「他明白」「这意味着」这类解释句。")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--style", default="杀虫队队员")
    ap.add_argument("--profile", default="")
    ap.add_argument("--sampling", default="default")
    ap.add_argument("--out", default="")
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--exe", default=sa.DEFAULT_EXE)
    a = ap.parse_args()

    out = a.out or os.path.join(tempfile.gettempdir(), "tdx-probe-" + re.sub(r"\W", "", a.style))
    os.makedirs(out, exist_ok=True)
    print("产物目录:", out)

    pf = os.path.join(os.path.expandvars(r"%APPDATA%\TdxClaw"), "author_profiles", a.style + ".json")
    if not os.path.exists(pf):
        print("没有档案:", pf)
        return 2
    prof = json.load(open(pf, encoding="utf-8"))
    sig = [(w["Word"], w["PerK"]) for w in prof.get("SignatureWords", [])]
    target = {
        "句长": prof["SentenceMedian"],
        "逗号": prof["CommaPerSentence"],
        "段长": prof["ParagraphMedian"],
        "对话%": round(prof["DialogueRatio"] * 100, 1),
    }
    print("档案目标:", target)
    print("档案引导语:", prof.get("DialogueTags"))

    mcp = sa.Mcp(a.exe)
    try:
        name = "实测" + re.sub(r"\W", "", a.style) + time.strftime("%H%M%S")
        t, err = mcp.call("project_create", {
            "name": name, "directory": out, "description": "风格单章实测，可删",
            "full_outline": FULL_OUTLINE, "characters": CHARACTERS,
            "background": BACKGROUND, "chapter_outline": CHAPTER_OUTLINE,
            "narrative_viewpoint": VIEWPOINT,
        }, timeout=120)
        if err:
            print("建书失败:", t[:500])
            return 1
        print("建书:", t.splitlines()[0][:120] if t else "")

        mcp.call("style_profile_bind", {"id": a.style})
        print("已绑定:", a.style)

        mcp.call("chapter_create", {"title": "第1章 须知"}, timeout=60)
        print("生成中…")
        args = {"task": "continue", "number": 1, "writeBack": True,
                "maxTokens": 3200, "instruction": INSTRUCTION, "sampling": a.sampling}
        if a.profile:
            args["profile"] = a.profile
        raw, bad = mcp.call("ai_write", args, timeout=a.timeout)
        open(os.path.join(out, "raw.txt"), "w", encoding="utf-8").write(raw or "")
        body = sa.extract_body(raw)
        open(os.path.join(out, "ch1.txt"), "w", encoding="utf-8").write(body)

        head = (raw or "").split("\n")[0]
        print("\n响应头:", head[:220])
        if bad or not body:
            print("生成失败或空正文")
            return 1

        m = sa.metrics(body, [w for w, _ in sig])
        print("\n===== 一、指纹贴合 =====")
        for k, tv in target.items():
            av = m.get(k)
            dev = (av - tv) / tv * 100 if tv else 0
            print(f"  {k}: 目标 {tv} / 实际 {av} ({dev:+.0f}%)")
        print(f"  字数 {m['字数']}")

        v = max(1, sa.vis(body))
        print("\n===== 二、签名词是否被堆砌 =====")
        hot = []
        for w, pk in sig:
            act = body.count(w) * 1000.0 / v
            flag = "  ← 超目标 2 倍" if act > pk * 2 and act >= 0.5 else ""
            if flag:
                hot.append(w)
            print(f"  {w}: 目标 {pk:.2f}/千字，实际 {act:.2f}{flag}")
        print("  结论:", ("堆砌了 " + "、".join(hot)) if hot else "没有明显堆砌")

        print("\n===== 三、串名检查 =====")
        foreign = ["齐夏", "俊南", "家劲", "楚天秋", "老吕", "地猴", "青龙", "白羊", "余念安", "云瑶"]
        hit = [n for n in foreign if n in body]
        print("  他作角色名:", hit or "无")

        print("\n===== 四、引导语 =====")
        tags = re.findall(r"([\u4e00-\u9fa5]{1,4}道)[，：]", body)
        print("  生成文本 top10:", Counter(tags).most_common(10))
        print("  档案词表:", prof.get("DialogueTags"))

        print("\n===== 五、规则呈现 =====")
        rule_lines = [l.strip() for l in body.split("\n")
                      if re.search(r"^[一二三四五六七]、|^\s*\d[.、]", l.strip())]
        print(f"  疑似规则条目行 {len(rule_lines)} 条")
        for l in rule_lines[:8]:
            print("   ", l[:60])

        print("\n---- 开头 320 字 ----")
        print(body[:320])
        print("\n---- 结尾 320 字 ----")
        print(body[-320:])
        return 0
    finally:
        mcp.close()


if __name__ == "__main__":
    sys.exit(main())
