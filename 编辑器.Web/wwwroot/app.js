// 网页版：人与 agent 共用的写作界面。
// 前端刻意不引构建工具链——改完刷新即可，不增加维护负担。

const $ = s => document.querySelector(s);
let book = null;          // 当前书（含章节列表）
let current = null;       // 当前章节号
let stamp = null;         // 打开时的版本号，用来发现"书被别处改过"
let saveTimer = null;
let dirty = false;

const api = async (url, opt) => {
  const r = await fetch(url, opt);
  if (!r.ok) throw new Error(`${r.status} ${await r.text()}`);
  return r.json();
};
const post = (url, body) => api(url, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body || {}),
});

// ---------- 书目 ----------

async function loadBooks() {
  const d = await api('/api/books');
  const sel = $('#bookSel');
  sel.innerHTML = '';
  d.books.forEach(b => {
    const o = document.createElement('option');
    o.value = b.fileName; o.textContent = `${b.name}（${b.chapters}章）`;
    sel.appendChild(o);
  });
  if (d.books.length && !book) await openBook(d.books[0].fileName);
}

async function openBook(fileName) {
  await post('/api/books/open', { name: fileName });
  $('#bookSel').value = fileName;
  await refresh();
  if (book?.chapters?.length) openChapter(book.chapters[0].n);
}

// ---------- 书与章节 ----------

async function refresh() {
  book = await api('/api/book');
  if (!book.open) { book = null; return; }
  stamp = book.stamp;
  renderChapters();
  renderOutline();
  loadStats();
  ['setOutline', 'setChapterOutline', 'setCharacters', 'setBackground', 'setStyle', 'setViewpoint']
    .forEach((id, i) => {
      const keys = ['outline', 'chapterOutline', 'characters', 'background', 'style', 'viewpoint'];
      $('#' + id).value = book[keys[i]] || '';
    });
}

function renderChapters() {
  const ul = $('#chList');
  ul.innerHTML = '';
  (book?.chapters || []).forEach(c => {
    const li = document.createElement('li');
    li.innerHTML = `<span>${c.n}. ${escapeHtml(c.title)}</span><span class="w">${c.words}</span>`;
    li.onclick = () => openChapter(c.n);
    if (c.n === current) li.classList.add('active');
    ul.appendChild(li);
  });
}

async function openChapter(n) {
  await flushSave();
  current = n;
  const ch = await api('/api/chapter/' + n);
  $('#chTitle').value = ch.title;
  $('#editor').value = ch.content || '';
  dirty = false;
  updateWords();
  renderChapters();
  $('#editor').focus();
}

function updateWords() {
  const t = $('#editor').value.replace(/\s/g, '');
  $('#chWords').textContent = `${t.length} 字`;
}

async function flushSave() {
  if (!dirty || current == null) return;
  clearTimeout(saveTimer);
  try {
    $('#saveState').textContent = '保存中…';
    const r = await post('/api/chapter/' + current, { content: $('#editor').value, stamp });
    stamp = r.stamp;
    dirty = false;
    $('#saveState').textContent = '已保存';
  } catch (e) {
    $('#saveState').textContent = '保存失败：' + e.message;
  }
}

// 自动保存：停手 1.2 秒后存。催人记得按 Ctrl+S 是最烂的解法。
$('#editor').addEventListener('input', () => {
  dirty = true;
  updateWords();
  $('#saveState').textContent = '未保存';
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSave, 1200);
});
window.addEventListener('beforeunload', () => { if (dirty) flushSave(); });

// ---------- 大纲 ----------

function renderOutline() {
  const cards = $('#outlineCards');
  cards.innerHTML = '';
  const list = book?.chapters || [];
  let unwritten = 0, noSummary = 0;
  list.forEach(c => {
    const empty = c.words === 0;
    const noSum = !empty && !(c.summary || '').trim();
    if (empty) unwritten++;
    if (noSum) noSummary++;
    const color = empty ? '#b4b2a9' : (noSum ? '#a0742f' : '#5f8f62');
    const d = document.createElement('div');
    d.className = 'card';
    d.innerHTML =
      `<div class="top"><span class="dot" style="background:${color}"></span>` +
      `<span class="t">${c.n}. ${escapeHtml(c.title)}</span><span class="w">${c.words} 字</span></div>` +
      `<div class="s">${escapeHtml(c.summary || '（还没写梗概）')}</div>`;
    d.onclick = () => { switchView('write'); openChapter(c.n); };
    cards.appendChild(d);
  });
  $('#outlineStats').textContent =
    `共 ${list.length} 章 · 未写 ${unwritten} 章 · 缺梗概 ${noSummary} 章`;
}

// ---------- 统计 ----------

async function loadStats() {
  try {
    const s = await api('/api/stats');
    $('#stats').textContent = s.today != null ? `今日 ${s.today} 字 · 连续 ${s.streak} 天` : '';
  } catch { $('#stats').textContent = ''; }
}

// ---------- AI ----------

$('#aiGoBtn').onclick = async () => {
  const out = $('#aiOut');
  out.textContent = '';
  const btn = $('#aiGoBtn');
  btn.disabled = true; btn.textContent = '生成中…';

  try {
    const res = await fetch('/api/ai', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        task: $('#aiTask').value,
        prompt: $('#aiPrompt').value || $('#editor').value.slice(-1500),
        maxTokens: 4096,
      }),
    });
    if (!res.ok) { out.textContent = await res.text(); return; }

    // SSE：逐块读，字一个一个出来
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      const parts = buf.split('\n\n');
      buf = parts.pop();
      for (const p of parts) {
        if (!p.startsWith('data: ')) continue;
        const ev = JSON.parse(p.slice(6));
        if (ev.type === 'text') out.textContent = ev.payload;
        else if (ev.type === 'done') out.textContent = ev.payload.text;
        else if (ev.type === 'error') out.textContent = '出错：' + ev.payload;
      }
    }
  } catch (e) {
    out.textContent = '出错：' + e.message;
  } finally {
    btn.disabled = false; btn.textContent = '生成';
  }
};

// ---------- 实时：agent 的动作出现在这里 ----------

function log(text, isAgent) {
  const box = $('#live');
  const row = document.createElement('div');
  row.className = 'row' + (isAgent ? ' agent' : '');
  row.innerHTML = `<b>${isAgent ? 'Agent' : '你'}</b> · ${escapeHtml(text)} ` +
                  `<span style="opacity:.6">${new Date().toLocaleTimeString()}</span>`;
  box.prepend(row);
}
$('#clearLog').onclick = () => { $('#live').innerHTML = ''; };

function connect() {
  if (typeof signalR === 'undefined') return;
  const conn = new signalR.HubConnectionBuilder()
    .withUrl('/hub/live').withAutomaticReconnect().build();

  conn.on('live', e => {
    if (e.kind === 'project-changed') {
      log('项目已更新');
      // 我正在编辑的那一章不能被冲掉，其余照常刷新
      refresh();
    } else if (e.kind === 'chapter-saved') {
      log(`第 ${e.payload.n} 章已保存`);
    }
  });

  // agent 通过 MCP 操作时的进度/阶段提示——这是"人机协同"的那一眼
  conn.on('mcpProgress', o => {
    const p = o?.params || {};
    log(p.message || JSON.stringify(o), true);
  });

  conn.start().catch(e => console.warn('实时连接失败', e));
}

// ---------- 杂项 ----------

function switchView(v) {
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.view === v));
  ['write', 'outline', 'settings'].forEach(n => {
    $('#view-' + n).classList.toggle('hidden', n !== v);
  });
}
document.querySelectorAll('.tab').forEach(t => t.onclick = () => switchView(t.dataset.view));

$('#addChBtn').onclick = async () => {
  const r = await post('/api/chapter', {});
  await refresh();
  openChapter(r.n);
};

$('#bookSel').onchange = e => openBook(e.target.value);
$('#newBookBtn').onclick = async () => {
  const name = prompt('书名？', '未命名');
  if (!name) return;
  const b = await post('/api/books', { name });
  await loadBooks();
  $('#bookSel').value = b.fileName;
  await openBook(b.fileName);
};

$('#saveSetBtn').onclick = async () => {
  await post('/api/settings', {
    outline: $('#setOutline').value,
    chapterOutline: $('#setChapterOutline').value,
    characters: $('#setCharacters').value,
    background: $('#setBackground').value,
    style: $('#setStyle').value,
    viewpoint: $('#setViewpoint').value,
  });
  log('设定已保存');
};

const escapeHtml = s => (s ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

// 键盘：Ctrl+S 保存，Ctrl+Enter 生成
document.addEventListener('keydown', e => {
  if (e.ctrlKey && e.key === 's') { e.preventDefault(); flushSave(); }
  if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); $('#aiGoBtn').click(); }
});

loadBooks().catch(e => console.error(e));
connect();
