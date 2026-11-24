# -*- coding: utf-8 -*-
"""用系统写一篇万字短篇《剃头》（余华档案）。分章生成，可断点续跑。"""
import glob
import io
import json
import os
import re
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, r"D:\repos\WRITTING")
import style_acceptance as sa

OUT = os.path.join(os.environ["TEMP"], "tdx-story")
STYLE = "余华"

FULL_OUTLINE = """《剃头》
镇口的老陈剃了四十年头。一九八五年秋天，一个外乡人剃完平头说没带钱，下次给。
老陈等到二〇一七年，等到手抖得握不住剃刀，也没等到那个人。
小说写这四十年里镇子的变化、儿子小军的出走、老街的拆迁，
以及一个人守着一件小事不放的固执。没有反转，没有揭底，那个人不会回来。"""

CHARACTERS = """老陈：剃头匠，一九五〇年生，十七岁跟师父学手艺。左手推子，右手剃刀。
话少，手稳，记得每个老主顾后脑勺的形状。一辈子没离开过镇子。
吴同志：一九八五年来剃头的外乡人，三十来岁，蓝卡其布中山装，说普通话。
剃平头。剃完摸遍口袋，说没带钱，下次给。之后再没出现过。
小军：老陈的儿子，一九七八年生。不爱学剃头，二〇〇一年去了深圳，越走越远。
陈师母：老陈的妻子，在镇小学食堂做饭。二〇〇四年病故。"""

BACKGROUND = """地点：江南小镇，镇口一棵老槐树，树下铺着一块青石板。
老陈的摊子就在槐树下：一把竹椅、一面挂镜、一个脸盆架、一只煤炉、一只铝饭盒装工具。
一九八八年搭铁皮棚，一九九五年换成砖房，二〇〇九年老街拆迁搬到巷子深处。
理发价：一九八五年一毛五，一九九三年一块二，二〇〇一年三块，二〇〇九年五块，二〇一七年十五块。
镇上二〇〇一年后开了三家发廊，招牌上写着「剪吹烫染」。"""

CHAPTER_OUTLINE = """第1章 一九八五：秋，槐树下。上午两个老主顾，中午陈师母送饭，下午外乡人来剃平头。
  剃完说没带钱，下次给。老陈说行。
第2章 一九九三：摊子换成镇口一间小门面。小军十五岁，暑假在店里帮忙，不爱学，跟老陈顶嘴。
  老陈记账，账本上还留着一笔没销：一九八五年秋，一毛五。
第3章 二〇〇一：小军去深圳，老陈送站。镇上开了三家发廊，年轻人不来了。
  陈师母咳嗽，去县医院查。
第4章 二〇〇九：老街拆迁，店搬到巷子深处。老陈去派出所问过一个人，没问出来。
第5章 二〇一七：老陈七十岁，手抖。最后一个头是个六七岁的小孩。关门，烧账本。"""

VIEWPOINT = """第三人称限知，只写老陈能看见、听见、想到的。不进入其他人物内心，
不交代老陈不知道的事（比如那个外乡人后来怎样了）。时间跳跃只交代必要信息，不写年表。"""

CHAPTERS = [
    ("第1章 剃头摊",
     "这是全书第一章，从第一句开始写起，不承接任何前文。写一九八五年秋天，老陈在镇口槐树下"
     "摆剃头摊的一天：上午两个老主顾，中午陈师母送饭来，下午那个外乡人来剃平头。"
     "外乡人剃完摸遍口袋说没带钱，下次给，老陈说行。"
     "写 2200~2500 字。只写正文，不要标题、不要章节号、不要任何解释。"
     "用动作和对话推进，不写「他知道」「他明白」「这意味着」这类解释句，结尾不要总结。"),
    ("第2章 门面",
     "第二章。一九九三年，老陈的摊子换成了镇口一间小门面，装了玻璃门。"
     "小军十五岁，暑假在店里帮忙，嫌剃头脏、嫌钱少，跟老陈顶嘴。"
     "关门后老陈翻账本，账本上还留着一笔没销：一九八五年秋，一毛五。"
     "写 2200~2500 字。只写正文，不要标题。承接上一章的人物和物件，不要重复交代背景。"
     "不写心理总结，结尾不要升华。"),
    ("第3章 深圳",
     "第三章。二〇〇一年，小军要去深圳打工，老陈送他到镇口汽车站。"
     "镇上开了三家发廊，挂「剪吹烫染」的招牌，年轻人不来了，店里只剩老主顾。"
     "陈师母开始咳嗽，老陈陪她去县医院查。"
     "写 2200~2500 字。只写正文，不要标题。承接上一章。不写心理总结，结尾不要升华。"),
    ("第4章 拆迁",
     "第四章。二〇〇九年，老街拆迁，老陈的店搬到巷子深处。"
     "搬家那天翻出旧账本。老陈去派出所问过一个人，户籍警翻了本子，没问出来。"
     "写 2200~2500 字。只写正文，不要标题。承接上一章。"
     "注意：问不出结果，不要给答案，不要让警察提供线索，不要安排巧合。结尾不要升华。"),
    ("第5章 最后一刀",
     "第五章，全书的最后一章。二〇一七年，老陈七十岁，手开始抖，剃刀在手里不稳。"
     "最后一个头是个六七岁的小孩，他爷爷带来的。剃完，老陈收了推子，关了店。"
     "晚上烧账本。"
     "写 2200~2500 字。只写正文，不要标题。承接上一章。"
     "不要写「他这一生」「四十年来」这类总结句。结尾落在一个具体的动作或声音上，不要升华。"),
]


def find_project():
    hits = sorted(glob.glob(os.path.join(OUT, "*.tdxproj")))
    return hits[0] if hits else ""


def main():
    os.makedirs(OUT, exist_ok=True)
    print("产物目录:", OUT)

    mcp = sa.Mcp(sa.DEFAULT_EXE)
    try:
        proj = find_project()
        if proj:
            print("复用已有项目:", os.path.basename(proj))
            mcp.call("project_open", {"path": proj}, timeout=120)
        else:
            t, err = mcp.call("project_create", {
                "name": "剃头", "directory": OUT, "description": "万字短篇实测，可删",
                "full_outline": FULL_OUTLINE, "characters": CHARACTERS,
                "background": BACKGROUND, "chapter_outline": CHAPTER_OUTLINE,
                "narrative_viewpoint": VIEWPOINT,
            }, timeout=180)
            print("建书:", (t or "").splitlines()[0][:120], "| err=", err)
            if err:
                return 1
            proj = find_project()
            if not proj:
                print("找不到 tdxproj")
                return 1
            mcp.call("project_open", {"path": proj}, timeout=120)

        mcp.call("style_profile_bind", {"id": STYLE})
        print("已绑定:", STYLE)

        for i, (title, instruction) in enumerate(CHAPTERS, 1):
            tag = "ch%d.txt" % i
            if os.path.exists(os.path.join(OUT, tag)):
                print("[%d/5] 已有产物，跳过" % i)
                continue
            mcp.call("chapter_create", {"title": title}, timeout=60)
            print("[%d/5] 生成 %s …" % (i, title))
            t0 = time.time()
            args = {"task": "continue", "number": i, "writeBack": True,
                    "maxTokens": 9000, "instruction": instruction}
            ok = False
            for attempt in (1, 2):
                try:
                    raw, bad = mcp.call("ai_write", args, timeout=420)
                except sa.McpTimeout:
                    print("   超时，重启 MCP 后重试")
                    mcp.restart()
                    mcp.call("project_open", {"path": proj}, timeout=120)
                    continue
                if bad or not raw or len(sa.extract_body(raw)) < 400:
                    print("   失败:", (raw or "")[:180])
                    continue
                body = sa.extract_body(raw)
                io.open(os.path.join(OUT, tag), "w", encoding="utf-8").write(body)
                print("   完成 %d 字，用时 %.0fs" % (sa.vis(body), time.time() - t0))
                ok = True
                break
            if not ok:
                print("   第 %d 章放弃" % i)
                return 2

        # 合成
        parts = []
        total = 0
        for i, (title, _) in enumerate(CHAPTERS, 1):
            p = os.path.join(OUT, "ch%d.txt" % i)
            if not os.path.exists(p):
                continue
            txt = io.open(p, encoding="utf-8").read().strip()
            total += sa.vis(txt)
            parts.append("%s\n\n%s" % (title, txt))
        io.open(os.path.join(OUT, "剃头_全文.md"), "w", encoding="utf-8").write(
            "# 剃头\n\n" + "\n\n\n".join(parts) + "\n")
        print("\n合计 %d 字" % total)
        return 0
    finally:
        mcp.close()


if __name__ == "__main__":
    sys.exit(main())
