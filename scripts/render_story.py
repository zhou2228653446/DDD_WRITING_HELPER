# -*- coding: utf-8 -*-
"""把《剃头》正文渲染成长篇小说的 HTML（供 Edge 打印 PDF）。"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

SRC = os.path.join(os.environ["TEMP"], "tdx-story")
DST = r"D:\repos\WRITTING\docs\剃头_短篇_2026-11-19.html"

CSS = """
@page { size: A4; margin: 20mm 22mm 18mm 22mm; }
@page :first { margin: 0; }
body { font-family: "Songti SC", "SimSun", serif; font-size: 11.5pt; line-height: 1.8;
       color: #16181c; margin: 0; }
h1 { font-family: "Microsoft YaHei", sans-serif; font-size: 26pt; font-weight: 700;
     letter-spacing: 6px; text-align: center; margin: 0 0 10px; }
.sub { text-align: center; color: #6b7280; font-size: 9.5pt; letter-spacing: 1px;
       font-family: "Microsoft YaHei", sans-serif; }
.cover { height: 297mm; display: flex; flex-direction: column;
         justify-content: center; align-items: center; page-break-after: always; }
.cover .rule { width: 46px; height: 2px; background: #16181c; margin: 26px 0 30px; }
.cover .meta { font-size: 9pt; color: #6b7280; line-height: 2.1; text-align: center; }
h2 { font-family: "Microsoft YaHei", sans-serif; font-size: 14pt; font-weight: 600;
     margin: 0 0 22px; padding-bottom: 8px; border-bottom: 1px solid #d8d3c8;
     page-break-before: always; letter-spacing: 2px; }
p { margin: 0; text-align: justify; text-indent: 0; }
.indent { display: inline-block; width: 2em; }
p.brk { text-indent: 0; }
/* 仅供屏幕预览：把正文压到 A4 正文宽度，截图才和 PDF 一致 */
@media screen { body { max-width: 627px; margin: 0 auto; } }
.tail { margin-top: 30px; padding-top: 10px; border-top: 1px solid #e5e7eb;
        font-size: 8.5pt; color: #9ca3af; text-indent: 0; line-height: 1.8;
        font-family: "Microsoft YaHei", sans-serif; }
"""


def main():
    chs = []
    for i in range(1, 6):
        p = os.path.join(SRC, "ch%d.txt" % i)
        if os.path.exists(p):
            chs.append((i, io.open(p, encoding="utf-8").read().strip()))
    if not chs:
        print("没有正文")
        return 1

    total = sum(len(re.findall(r"[\u4e00-\u9fa5]", t)) for _, t in chs)

    body = []
    titles = ["第一章 剃头摊", "第二章 门面", "第三章 深圳", "第四章 拆迁", "第五章 最后一刀"]
    for idx, (num, txt) in enumerate(chs, 1):
        # 章节标题：正文首行若是「第N章 …」就当作标题，否则用预设标题
        lines = [x.strip() for x in txt.split("\n") if x.strip()]
        title = titles[num - 1] if num <= len(titles) else "第%d章" % num
        if lines and re.match(r"^第[一二三四五六七八九十\d]+章", lines[0]):
            lines = lines[1:]
        body.append('<h2>%s</h2>' % title)
        for ln in lines:
            # ★ 三种缩进都试过：text-indent 在打印时会被丢；全角空格被 CSS 空白折叠吃掉；
            #   pre-wrap 保住了缩进却会撑破容器导致右侧裁字。用固定宽度的 inline-block 占位最稳。
            body.append('<p><span class="indent"></span>%s</p>'
                        % ln.replace("&", "&amp;").replace("<", "&lt;"))

    html = """<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>剃头</title>
<style>%s</style></head><body>
<div class="cover">
  <h1>剃头</h1>
  <div class="rule"></div>
  <div class="meta">短篇 · 约 %d 字 · 五章<br>余华风格档案生成<br>2026-11-19</div>
</div>
%s
<p class="tail">全文 %d 字。生成方式：作者风格档案「余华」绑定 + 锚定指令，
分五章连续生成，未人工改写。仅作风格效果观察用。</p>
</body></html>""" % (CSS, total, "\n".join(body), total)

    io.open(DST, "w", encoding="utf-8").write(html)
    print("已写出:", DST, "| %d 字 | %d 章" % (total, len(chs)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
