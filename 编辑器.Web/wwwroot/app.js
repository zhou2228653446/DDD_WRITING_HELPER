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
const del = url => api(url, { method: 'DELETE' });

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
  await Promise.all([loadCharacters(), loadSnapshots(), loadMemory()]);
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

// ---------- 查找替换 ----------

const findBar = $('#findBar');
let findHits = [], findPos = -1;

function findOpen() {
  findBar.classList.remove('hidden');
  $('#findInput').focus();
  $('#findInput').select();
}
function findClose() {
  findBar.classList.add('hidden');
  findHits = []; $('#findCount').textContent = '';
  $('#editor').focus();
}
function findCompute() {
  const text = $('#editor').value, q = $('#findInput').value;
  findHits = [];
  if (q) {
    let i = 0;
    while ((i = text.indexOf(q, i)) !== -1) { findHits.push(i); i += q.length; }
  }
  $('#findCount').textContent = findHits.length ? `${findHits.length} 处` : (q ? '无结果' : '');
  findPos = -1;
}
function findJump(delta) {
  findCompute();
  if (!findHits.length) return;
  findPos = (findPos + delta + findHits.length) % findHits.length;
  const ed = $('#editor');
  ed.setSelectionRange(findHits[findPos], findHits[findPos] + $('#findInput').value.length);
  // 把选中处滚进视野：借 scrollHeight 比例估算
  const ratio = findHits[findPos] / Math.max(ed.value.length, 1);
  ed.scrollTop = ratio * ed.scrollHeight - ed.clientHeight / 2;
  $('#findCount').textContent = `${findHits.length} 处 · 第 ${findPos + 1} 个`;
}
$('#findInput').addEventListener('input', () => { findPos = -1; findCompute(); });
$('#findInput').addEventListener('keydown', e => { if (e.key === 'Enter') findJump(e.shiftKey ? -1 : 1); });
$('#findPrev').onclick = () => findJump(-1);
$('#findNext').onclick = () => findJump(1);
$('#findClose').onclick = findClose;

function doReplace(one) {
  const ed = $('#editor'), q = $('#findInput').value, r = $('#replaceInput').value;
  if (!q) return;
  findCompute();
  if (one) {
    if (!findHits.length) return;
    // 优先替换当前选中处
    const selStart = ed.selectionStart;
    const hit = findHits.find(h => h === selStart) ?? findHits[findPos >= 0 ? findPos : 0];
    ed.value = ed.value.slice(0, hit) + r + ed.value.slice(hit + q.length);
  } else {
    const n = findHits.length;
    ed.value = ed.value.split(q).join(r);
    $('#findCount').textContent = `已替换 ${n} 处`;
  }
  dirty = true;
  $('#saveState').textContent = '未保存';
  updateWords();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSave, 1200);
  findCompute();
}
$('#replaceOne').onclick = () => doReplace(true);
$('#replaceAll').onclick = () => doReplace(false);

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

// ---------- 人物卡 ----------

const FIELDS = [
  ['gender', '性别'], ['occupation', '职业'], ['age', '年龄'], ['role', '定位'],
  ['abilities', '能力'], ['appearance', '外貌'], ['personality', '性格'],
  ['background', '背景'], ['relationships', '关系'], ['notes', '备注'],
];

async function loadCharacters() {
  if ($('#charDlg').open) return;   // 正在编辑就不冲掉表单
  try {
    const d = await api('/api/characters');
    renderCharacters(d.characters || []);
  } catch { /* 书没开时静默 */ }
}

function renderCharacters(list) {
  const cards = $('#charCards');
  cards.innerHTML = '';
  $('#charStats').textContent = list.length ? `${list.length} 位人物` : '还没有人物，点"新建人物"开始。';
  list.forEach(c => {
    const d = document.createElement('div');
    d.className = 'card';
    const rows = FIELDS
      .filter(([k]) => (c[k] ?? '') !== '' && !(k === 'age' && !c.age))
      .slice(0, 5)
      .map(([k, label]) => `<div><b>${label}</b> ${escapeHtml(String(c[k]))}</div>`)
      .join('');
    d.innerHTML =
      `<div class="top"><span class="t">${escapeHtml(c.name || '（未命名）')}</span>` +
      `<span class="w">${escapeHtml(c.role || '')}</span></div>` +
      `<div class="fields">${rows}</div>` +
      `<div class="ops"><button class="char-edit">编辑</button></div>`;
    d.querySelector('.char-edit').onclick = () => openCharDlg(c);
    cards.appendChild(d);
  });
}

let editingCharId = null;
function openCharDlg(c) {
  editingCharId = c ? c.characterId : null;
  $('#charDlgTitle').textContent = c ? `编辑：${c.name}` : '新建人物';
  $('#cName').value = c?.name || '';
  $('#cRole').value = c?.role || '';
  $('#cGender').value = c?.gender || '';
  $('#cAge').value = c?.age || '';
  $('#cOccupation').value = c?.occupation || '';
  $('#cAbilities').value = c?.abilities || '';
  $('#cAppearance').value = c?.appearance || '';
  $('#cPersonality').value = c?.personality || '';
  $('#cBackground').value = c?.background || '';
  $('#cRelationships').value = c?.relationships || '';
  $('#cNotes').value = c?.notes || '';
  $('#charDelete').style.display = c ? '' : 'none';
  $('#charDlg').showModal();
}

$('#charCancel').onclick = () => $('#charDlg').close();
$('#newCharBtn').onclick = () => openCharDlg(null);
$('#charForm').onsubmit = async e => {
  e.preventDefault();
  const c = {
    characterId: editingCharId || crypto.randomUUID(),
    name: $('#cName').value.trim(),
    role: $('#cRole').value.trim(),
    gender: $('#cGender').value.trim(),
    age: parseInt($('#cAge').value) || 0,
    occupation: $('#cOccupation').value.trim(),
    abilities: $('#cAbilities').value.trim(),
    appearance: $('#cAppearance').value,
    personality: $('#cPersonality').value,
    background: $('#cBackground').value,
    relationships: $('#cRelationships').value,
    notes: $('#cNotes').value,
  };
  if (!c.name) return;
  await post('/api/characters', c);
  $('#charDlg').close();
  log(editingCharId ? `更新了人物「${c.name}」` : `新建了人物「${c.name}」`);
  await refresh();
};
$('#charDelete').onclick = async () => {
  if (!editingCharId) return;
  const name = $('#cName').value;
  if (!confirm(`确定删除人物「${name}」？此操作不可撤销。`)) return;
  await del('/api/characters/' + editingCharId);
  $('#charDlg').close();
  log(`删除了人物「${name}」`);
  await refresh();
};

// ---------- 快照 ----------

async function loadSnapshots() {
  try {
    const d = await api('/api/snapshots');
    renderSnapshots(Array.isArray(d) ? d : d.snapshots || []);
  } catch { /* 书没开时静默 */ }
}

function renderSnapshots(list) {
  const box = $('#snapList');
  box.innerHTML = '';
  if (!list.length) {
    box.innerHTML = '<div class="muted">还没有快照。</div>';
    return;
  }
  list.forEach(s => {
    const d = document.createElement('div');
    d.className = 'snap-item';
    const when = s.timestamp ? new Date(s.timestamp).toLocaleString() : '未知时间';
    d.innerHTML =
      `<span class="when">${escapeHtml(when)}</span>` +
      `<span class="why">${escapeHtml(s.description || '')}</span>` +
      `<button class="snap-restore">恢复</button>`;
    d.querySelector('.snap-restore').onclick = async () => {
      if (!confirm('恢复到这个快照？当前正文会被覆盖，编辑器里未保存的内容会丢。')) return;
      await flushSave();
      await post('/api/snapshot/restore', { name: s.id });
      log('恢复到快照：' + when);
      await refresh();
      if (current != null) openChapter(current);
    };
    box.appendChild(d);
  });
}

// ---------- AI 记忆 ----------

async function loadMemory() {
  try {
    const d = await api('/api/memory');
    if (document.activeElement !== $('#setMemory')) $('#setMemory').value = d.text || '';
  } catch { /* 静默 */ }
}

$('#saveMemBtn').onclick = async () => {
  await post('/api/memory', { name: $('#setMemory').value });
  log('AI 写作记忆已保存');
};

// ---------- 导出 ----------

document.querySelectorAll('.export button').forEach(b => {
  b.onclick = () => {
    if (!book) return;
    window.open(`/api/export?format=${b.dataset.fmt}`, '_blank');
    log(`导出 ${b.textContent} 已开始下载`);
  };
});

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

  conn.on('memory-changed', () => {
    log('AI 记忆已更新');
    loadMemory();
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
  ['write', 'outline', 'characters', 'snapshots', 'settings'].forEach(n => {
    $('#view-' + n).classList.toggle('hidden', n !== v);
  });
  if (v === 'characters') loadCharacters();
  if (v === 'snapshots') loadSnapshots();
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

// 键盘：Ctrl+S 保存，Ctrl+F 查找，Ctrl+Enter 生成
document.addEventListener('keydown', e => {
  if (e.ctrlKey && e.key === 's') { e.preventDefault(); flushSave(); }
  if (e.ctrlKey && e.key === 'f' && !$('#view-write').classList.contains('hidden')) {
    e.preventDefault(); findOpen();
  }
  if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); $('#aiGoBtn').click(); }
});

loadBooks().catch(e => console.error(e));
connect();
