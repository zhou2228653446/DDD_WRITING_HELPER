# -*- coding: utf-8 -*-
"""epub → txt（按书拆分，供作者风格档案导入）。

用法：
    python scripts/epub2txt.py "corpus/马伯庸/马伯庸作品合集.epub" --out corpus/马伯庸
    python scripts/epub2txt.py xxx.epub --out dir --list      # 只列书目与字数，不写文件

为什么要自己写：epub 内部是 500+ 个 html 碎片，直接拼会丢章节边界；
这里按 toc.ncx 的一级条目（书名）切分，每本书单独出一个 txt，
既能整本导入，也能只挑成熟期的那几部（早期作品风格会污染统计）。
"""
import argparse
import io
import os
import re
import zipfile


def natural_key(path):
    """part0182_split_000.html → (182, 0)，保证碎片按顺序拼。"""
    m = re.search(r'part0*(\d+)(?:_split_0*(\d+))?', path)
    return (int(m.group(1)), int(m.group(2) or 0)) if m else (10**9, 0)


def html_to_text(raw):
    # 块级标签先转换行，避免整章挤成一行（段落统计要用）
    raw = re.sub(r'(?i)</(p|div|h[1-6]|li|tr|br)\s*>', '\n', raw)
    raw = re.sub(r'(?i)<br\s*/?>', '\n', raw)
    raw = re.sub(r'(?is)<(script|style)[^>]*>.*?</\1>', '', raw)
    raw = re.sub(r'<[^>]+>', '', raw)
    raw = (raw.replace('&nbsp;', ' ').replace('&amp;', '&').replace('&lt;', '<')
              .replace('&gt;', '>').replace('&quot;', '"').replace('&#39;', "'"))
    lines = [ln.strip() for ln in raw.split('\n')]
    out = []
    for ln in lines:
        if not ln:
            continue
        # 目录页那种「第一章 xxx …… 3」的行不要
        if re.fullmatch(r'.{2,40}[\s.·…]{2,}\d{1,4}', ln):
            continue
        out.append(ln)
    return '\n'.join(out)


def visible(s):
    return len(re.findall(r'[\u4e00-\u9fa5]', s))


CHAPTER_RX = re.compile(
    r'^\s*(第[0-9〇零一二三四五六七八九十百千两]+[章回节]|序章|序|楔子|间奏|尾声|后记|番外)')


def strip_front_matter(body):
    """砍掉版权页和目录。

    判据：**这个章节标题到下一个章节标题之间**有没有正文（≥100 字）。
    目录页里每个标题后面紧跟的是下一个标题，间隔几乎为 0；
    真正的第一章后面才是成段的叙述。
    （早先写成「标题后 40 行内有正文」，目录后面就是正文，照样误判。）
    """
    lines = body.split('\n')
    for i, ln in enumerate(lines):
        # ★ 前置页不超过几千字：过了这个量级就不是目录，说明这本书的章节
        #   格式不匹配（如《三国配角演义》），再往前找会把正文砍掉（实测丢 10 万字）。
        if visible('\n'.join(lines[:i])) > 5000:
            break
        if not CHAPTER_RX.match(ln):
            continue
        j = i + 1
        while j < len(lines) and not CHAPTER_RX.match(lines[j]):
            j += 1
        if visible('\n'.join(lines[i + 1:j])) >= 100:
            return '\n'.join(lines[i:])
    return body


def parse(path):
    z = zipfile.ZipFile(path)
    ncx_name = next((n for n in z.namelist() if n.endswith('.ncx')), None)
    if not ncx_name:
        raise SystemExit('没有 toc.ncx，这个 epub 不支持')
    ncx = z.read(ncx_name).decode('utf-8', 'ignore')
    # navPoint 顺序即阅读顺序；<text> 是标题，<content src> 是文件
    pairs = re.findall(
        r'<navPoint[^>]*>\s*<navLabel>\s*<text>(.*?)</text>\s*</navLabel>\s*<content src="([^"]+)"',
        ncx, re.S)
    labels = [(re.sub(r'<[^>]+>', '', t).strip(), s) for t, s in pairs]

    files = [n for n in z.namelist() if re.search(r'\.(x?html|htm)$', n, re.I)]
    files.sort(key=natural_key)

    # 一级书名：紧跟着「封面」或「版权信息」的那条
    books = []
    for i, (t, s) in enumerate(labels[:-1]):
        nxt = labels[i + 1][0]
        if nxt in ('封面', '版权信息') and t not in ('封面', '版权信息', '目录', '总目录'):
            books.append((t, s))
    if not books:
        raise SystemExit('没识别出书名层级')

    def idx_of(src):
        s = src.split('#')[0]
        for i, f in enumerate(files):
            if f == s or f.endswith('/' + s.split('/')[-1]):
                return i
        return None

    starts = []
    for name, src in books:
        i = idx_of(src)
        if i is not None:
            starts.append((i, name))
    starts.sort()
    # 去重（同一文件被两本书指到时只留先出现的）
    uniq, seen = [], set()
    for i, name in starts:
        if i in seen:
            continue
        seen.add(i)
        uniq.append((i, name))

    result = []
    # ★ 章节标题必须写进正文：档案的样本池按「章」切窗口、同章最多取 2 段；
    #   整本书合成一章的话，48 段样本全来自一章，多样性直接废掉。
    chapter_rx = re.compile(r'^(第[0-9〇零一二三四五六七八九十百千两]+[章回节]|序章|序|楔子|间奏|尾声|后记|番外)')
    label_by_src = {}
    for t, s in labels:
        key = s.split('#')[0]
        if key not in label_by_src and t not in ('封面', '版权信息', '目录', '总目录'):
            label_by_src[key] = t

    for k, (i, name) in enumerate(uniq):
        end = uniq[k + 1][0] if k + 1 < len(uniq) else len(files)
        text = []
        for f in files[i:end]:
            lab = label_by_src.get(f)
            if lab and chapter_rx.match(lab):
                text.append(lab)
            text.append(html_to_text(z.read(f).decode('utf-8', 'ignore')))
        body = '\n'.join(t for t in text if t)
        result.append((name, body))
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('epub')
    ap.add_argument('--out', default='', help='输出目录；不给则只打印清单')
    ap.add_argument('--list', action='store_true')
    ap.add_argument('--min', type=int, default=30000, help='少于这么多汉字的不单独出文件')
    a = ap.parse_args()

    books = parse(a.epub)
    total = sum(visible(b) for _, b in books)
    print('共 %d 部，合计 %d 汉字（%.1f 万字）\n' % (len(books), total, total / 10000))
    print('%-34s %10s' % ('书名', '汉字数'))
    print('-' * 48)
    for name, body in books:
        n = visible(body)
        flag = '' if n >= a.min else '  (跳过)'
        print('%-34s %10d%s' % (name[:32], n, flag))
    print('-' * 48)
    print('%-34s %10d' % ('合计', total))

    if not a.out or a.list:
        return
    os.makedirs(a.out, exist_ok=True)
    kept = 0
    for name, body in books:
        n = visible(body)
        if n < a.min:
            continue
        safe = re.sub(r'[\\/:*?"<>|]', '_', name)
        p = os.path.join(a.out, safe + '.txt')
        clean = strip_front_matter(body)
        head = '' if CHAPTER_RX.match(clean.split('\n')[0] if clean else '') else '第1章 ' + safe + '\n\n'
        with io.open(p, 'w', encoding='utf-8') as f:
            f.write(head + clean)
        kept += 1
    # 合集全量（有些统计想跨书取样本时用）
    allp = os.path.join(a.out, '_合集.txt')
    with io.open(allp, 'w', encoding='utf-8') as f:
        for name, body in books:
            if visible(body) >= a.min:
                f.write('第1章 ' + re.sub(r'[\\/:*?"<>|]', '_', name) + '\n\n' + body + '\n\n')
    print('\n已写出 %d 个分书 txt + _合集.txt → %s' % (kept, a.out))


if __name__ == '__main__':
    main()
