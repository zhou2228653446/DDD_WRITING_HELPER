// 网页版：人与 agent 共用的写作界面。
// 前端刻意不引构建工具链——改完刷新即可，不增加维护负担。

const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let book = null;          // 当前书（含章节列表）
let current = null;       // 当前章节号
let stamp = null;         // 打开时的版本号，用来发现"书被别处改过"
let saveTimer = null;
let dirty = false;
let chatInfo = null;      // 聊天记忆的规模（服务器侧 <项目目录>/.chat/session.json，与桌面端同一份）

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

// ---------- 外观（配色 × 材质，见 appearance.js；服务器上的 appearance.json 是唯一权威） ----------

// 顶栏的配色快捷下拉：只切配色，材质/强度/背景图保持不动
$('#themeSel').onchange = async () => {
  appearance.scheme = $('#themeSel').value;
  appearanceDraft = pick(appearance);
  applyAppearance(appearanceDraft);
  await post('/api/appearance', {
    scheme: appearance.scheme, material: appearance.material, intensity: appearance.intensity,
  });
  localStorage.setItem('appearance', JSON.stringify(appearanceDraft));
  log(`配色已切换：${schemeName(appearance.scheme)}`);
};
$('#appearanceBtn').onclick = openAppearanceDlg;

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
  renderWorld(book.world);
  renderCtxChapters();
  await Promise.all([loadCharacters(), loadSnapshots(), loadMemory(), loadLiterature(), loadChatInfo()]);
}

function renderChapters() {
  const ul = $('#chList');
  ul.innerHTML = '';
  (book?.chapters || []).forEach(c => {
    const li = document.createElement('li');
    li.innerHTML = `<span>${c.n}. ${escapeHtml(c.title)}</span>` +
                   `<span class="w">${c.words}</span>` +
                   `<span class="ops">` +
                   `<button title="上移" data-op="up">↑</button>` +
                   `<button title="下移" data-op="down">↓</button>` +
                   `<button title="删除" class="del" data-op="del">✕</button></span>`;
    li.onclick = () => openChapter(c.n);
    li.querySelectorAll('.ops button').forEach(b => {
      b.onclick = async e => {
        e.stopPropagation();
        const op = b.dataset.op;
        if (op === 'up') await post(`/api/chapter/${c.n}/move`, { delta: -1 });
        else if (op === 'down') await post(`/api/chapter/${c.n}/move`, { delta: 1 });
        else {
          if (!confirm(`确定删除第 ${c.n} 章「${c.title}」？正文不可找回（除非有快照）。`)) return;
          await del('/api/chapter/' + c.n);
          if (current === c.n) { current = null; $('#editor').value = ''; }
        }
        await refresh();
      };
    });
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
// 关页面 / 刷新时：先抢救一次保存；没存上就跟用户确认再走
//（对应桌面端的「退出确认」窗口）。平时停手 1.2 秒就自动存，
// 真会拦到的只有"刚敲完字立刻关掉"那一刻——那正是最该拦的。
window.addEventListener('beforeunload', e => {
  if (!dirty) return;
  flushSave();                 // 尽力而为：本地服务器通常来不及返回，所以不能只靠它
  e.preventDefault();
  e.returnValue = '';
});

// 章节标题：失焦即改名
$('#chTitle').addEventListener('change', async () => {
  if (current == null) return;
  await post(`/api/chapter/${current}/rename`, { name: $('#chTitle').value });
  log(`章节改名：${$('#chTitle').value}`);
  await refresh();
});

// ---------- 查找替换（单章 / 全书，可区分大小写） ----------

const findBar = $('#findBar');
const findResults = $('#findResults');
let findHits = [], findPos = -1, findTimer = null;

const isCase = () => $('#findCase').checked;
const isBookScope = () => $('#findScope').value === 'book';
const escapeRe = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function findOpen() {
  findBar.classList.remove('hidden');
  $('#findInput').focus();
  $('#findInput').select();
}
function findClose() {
  findBar.classList.add('hidden');
  findResults.classList.add('hidden');
  findResults.innerHTML = '';
  findHits = []; findPos = -1;
  $('#findCount').textContent = '';
  $('#editor').focus();
}

// 单章命中定位（大小写不敏感时用小写串找位置，与原文索引一一对应）
function findCompute() {
  const raw = $('#editor').value, qRaw = $('#findInput').value;
  findHits = [];
  if (qRaw) {
    const text = isCase() ? raw : raw.toLowerCase();
    const q = isCase() ? qRaw : qRaw.toLowerCase();
    let i = 0;
    while ((i = text.indexOf(q, i)) !== -1) { findHits.push(i); i += q.length; }
  }
  $('#findCount').textContent = findHits.length ? `${findHits.length} 处` : (qRaw ? '无结果' : '');
  findPos = -1;
}
function findJump(delta) {
  findCompute();
  if (!findHits.length) return;
  findPos = (findPos + delta + findHits.length) % findHits.length;
  const ed = $('#editor');
  ed.setSelectionRange(findHits[findPos], findHits[findPos] + $('#findInput').value.length);
  const ratio = findHits[findPos] / Math.max(ed.value.length, 1);
  ed.scrollTop = ratio * ed.scrollHeight - ed.clientHeight / 2;
  $('#findCount').textContent = `${findHits.length} 处 · 第 ${findPos + 1} 个`;
}

// 全书查找：交给服务器扫（同一份章节数据），结果按章列出
async function runBookFind() {
  const q = $('#findInput').value;
  if (!q) { findResults.classList.add('hidden'); findResults.innerHTML = ''; $('#findCount').textContent = ''; return; }
  try {
    const d = await api(`/api/find?q=${encodeURIComponent(q)}&caseSensitive=${isCase()}`);
    const list = d.chapters || [];
    const total = list.reduce((a, c) => a + c.count, 0);
    $('#findCount').textContent = total ? `全书 ${total} 处` : '无结果';
    findResults.innerHTML = list.length
      ? list.map(c =>
          `<div class="fr-ch"><div class="fr-head" data-n="${c.n}">第 ${c.n} 章 ${escapeHtml(c.title)} · ${c.count} 处</div>` +
          c.hits.map(h => `<div class="fr-hit">${escapeHtml(h)}</div>`).join('') + `</div>`).join('')
      : '<div class="muted" style="padding:6px 10px">没有找到。</div>';
    findResults.classList.remove('hidden');
    findResults.querySelectorAll('.fr-head').forEach(el => {
      el.onclick = async () => { switchView('write'); await openChapter(parseInt(el.dataset.n, 10)); };
    });
  } catch (e) {
    findResults.innerHTML = `<div class="muted" style="padding:6px 10px">查找失败：${escapeHtml(e.message)}</div>`;
    findResults.classList.remove('hidden');
  }
}
function scheduleFind() {
  clearTimeout(findTimer);
  if (isBookScope()) { findTimer = setTimeout(runBookFind, 250); findHits = []; findPos = -1; }
  else { findResults.classList.add('hidden'); findPos = -1; findCompute(); }
}

$('#findInput').addEventListener('input', scheduleFind);
$('#findInput').addEventListener('keydown', e => { if (e.key === 'Enter') findJump(e.shiftKey ? -1 : 1); });
$('#findCase').addEventListener('change', scheduleFind);
$('#findScope').addEventListener('change', scheduleFind);
$('#findPrev').onclick = () => findJump(-1);
$('#findNext').onclick = () => findJump(1);
$('#findClose').onclick = findClose;

// 单章替换：区分大小写走字符串，不区分走正则（用函数式替换，避免 $1 被当反向引用）
function chapterReplaceAll(q, r) {
  const ed = $('#editor');
  if (isCase()) {
    const n = ed.value.split(q).length - 1;
    ed.value = ed.value.split(q).join(r);
    return n;
  }
  const re = new RegExp(escapeRe(q), 'gi');
  const n = (ed.value.match(re) || []).length;
  ed.value = ed.value.replace(re, () => r);
  return n;
}

function markDirtyAfterReplace() {
  dirty = true;
  $('#saveState').textContent = '未保存';
  updateWords();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSave, 1200);
}

async function doReplace(one) {
  const q = $('#findInput').value, r = $('#replaceInput').value;
  if (!q) return;

  if (isBookScope()) {
    if (!confirm(`把全书的「${q}」替换为「${r}」？替换前会自动存一份快照。`)) return;
    await flushSave();
    const res = await post('/api/replace-all', { q, r, caseSensitive: isCase() });
    log(`全书替换：${res.replaced} 处`);
    await refresh();
    if (current != null) await openChapter(current);
    await runBookFind();
    return;
  }

  findCompute();
  if (one) {
    if (!findHits.length) return;
    const ed = $('#editor');
    const selStart = ed.selectionStart;
    const hit = findHits.find(h => h === selStart) ?? findHits[findPos >= 0 ? findPos : 0];
    ed.value = ed.value.slice(0, hit) + r + ed.value.slice(hit + q.length);
  } else {
    const n = chapterReplaceAll(q, r);
    $('#findCount').textContent = `已替换 ${n} 处`;
  }
  markDirtyAfterReplace();
  findCompute();
}
$('#replaceOne').onclick = () => doReplace(true);
$('#replaceAll').onclick = () => doReplace(false);

// ---------- 大纲（卡片 + 单章梗概编辑） ----------

function renderOutline() {
  const cards = $('#outlineCards');
  cards.innerHTML = '';
  const all = book?.chapters || [];
  let unwritten = 0, noSummary = 0;
  all.forEach(c => {
    if (c.words === 0) unwritten++;
    else if (!(c.summary || '').trim()) noSummary++;
  });

  // 「只看」筛选：与桌面端 OutlineWindow 的四个选项一一对应。
  // 统计数字始终按全书算，不受筛选影响——否则切一下筛选，数字也跟着变，就看不出全书状况了。
  const filter = $('#outlineFilter').value;
  const list = all.filter(c => {
    const empty = c.words === 0;
    const noSum = !empty && !(c.summary || '').trim();
    if (filter === 'unwritten') return empty;
    if (filter === 'nosummary') return noSum;
    if (filter === 'done') return !empty;
    return true;
  });

  list.forEach(c => {
    const empty = c.words === 0;
    const noSum = !empty && !(c.summary || '').trim();
    const color = empty ? '#b4b2a9' : (noSum ? '#a0742f' : '#5f8f62');
    const d = document.createElement('div');
    d.className = 'card';
    d.innerHTML =
      `<div class="top"><span class="dot" style="background:${color}"></span>` +
      `<span class="t">${c.n}. ${escapeHtml(c.title)}</span><span class="w">${c.words} 字</span></div>` +
      `<div class="s">${escapeHtml(c.summary || '（还没写梗概）')}</div>` +
      `<div class="card-ops"><button class="mini-btn" data-op="sum">梗概</button>` +
      `<button class="mini-btn" data-op="up">↑</button><button class="mini-btn" data-op="down">↓</button></div>`;
    d.onclick = () => { switchView('write'); openChapter(c.n); };
    d.querySelectorAll('.card-ops button').forEach(b => {
      b.onclick = async e => {
        e.stopPropagation();
        if (b.dataset.op === 'sum') openSummaryDlg(c);
        else await post(`/api/chapter/${c.n}/move`, { delta: b.dataset.op === 'up' ? -1 : 1 });
        await refresh();
      };
    });
    cards.appendChild(d);
  });

  if (!list.length && all.length) {
    cards.innerHTML = '<div class="muted" style="padding:8px 2px">没有符合条件的章节。</div>';
  }

  $('#outlineStats').textContent =
    `共 ${all.length} 章 · 未写 ${unwritten} 章 · 缺梗概 ${noSummary} 章` +
    (filter === 'all' ? '' : ` · 当前显示 ${list.length} 章`);
}

$('#outlineFilter').onchange = () => renderOutline();

let summaryChapter = null;
function openSummaryDlg(c) {
  summaryChapter = c.n;
  $('#summaryTitle').textContent = `第 ${c.n} 章「${c.title}」· 梗概`;
  $('#summaryText').value = c.summary || '';
  $('#summaryDlg').showModal();
}
$('#summarySave').onclick = async () => {
  if (summaryChapter == null) return;
  await post(`/api/chapter/${summaryChapter}/summary`, { name: $('#summaryText').value });
  $('#summaryDlg').close();
  log(`第 ${summaryChapter} 章梗概已保存`);
  await refresh();
};

// ---------- 人物 ----------

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
    await loadAppearances();
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

async function loadAppearances() {
  try {
    const d = await api('/api/character-appearances');
    const box = $('#appearList');
    box.innerHTML = '';
    const stats = d.stats || [];
    if (!stats.length) { box.innerHTML = '<span class="muted">还没有正文，无从统计。</span>'; return; }
    stats.forEach(s => {
      const r = document.createElement('div');
      r.className = 'appear-row';
      const absent = s.absentStreak != null && s.absentStreak > 0
        ? `<span class="absent ${s.absentStreak >= 3 ? 'bad' : ''}">已缺席 ${s.absentStreak} 章</span>`
        : '<span class="absent" style="color:var(--ok)">在场上</span>';
      r.innerHTML = `<b>${escapeHtml(s.name)}</b>` +
        `<span class="chapters">出场章：${s.chapters.length ? s.chapters.join('、') : '（无）'} · 共 ${s.totalHits} 次</span>` +
        absent;
      box.appendChild(r);
    });
  } catch { /* 静默 */ }
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

// ---------- 文献库 ----------

async function loadLiterature() {
  if ($('#litDlg').open || $('#bibDlg').open) return;
  try {
    const d = await api('/api/literature');
    renderLiterature(d.entries || []);
  } catch { /* 静默 */ }
}

function renderLiterature(list) {
  const box = $('#litList');
  box.innerHTML = '';
  $('#litStats').textContent = list.length ? `${list.length} 条 · AI 生成时自动作引用上下文` : '空——论文模式下 AI 会提醒补文献。';
  list.forEach((e, i) => {
    const d = document.createElement('div');
    d.className = 'lit-item';
    d.innerHTML =
      `<span class="no">[${i + 1}]</span>` +
      `<span class="main">${escapeHtml(e.title || '(无标题)')}</span>` +
      `<span class="meta">${escapeHtml([e.authors, e.year, e.venue].filter(Boolean).join(' · '))}</span>`;
    d.onclick = () => openLitDlg(e);
    box.appendChild(d);
  });
}

let editingLitId = null;
function openLitDlg(e) {
  editingLitId = e ? e.id : null;
  $('#litDlgTitle').textContent = e ? '编辑文献' : '添加文献';
  $('#lTitle').value = e?.title || '';
  $('#lAuthors').value = e?.authors || '';
  $('#lYear').value = e?.year || '';
  $('#lVenue').value = e?.venue || '';
  $('#lEntryType').value = e?.entryType || '';
  $('#lDoi').value = e?.doi || '';
  $('#lPages').value = e?.pages || '';
  $('#lUrl').value = e?.url || '';
  $('#lAbstract').value = e?.abstract || '';
  $('#lNote').value = e?.note || '';
  $('#litDelete').style.display = e ? '' : 'none';
  $('#litDlg').showModal();
}
$('#litCancel').onclick = () => $('#litDlg').close();
$('#newLitBtn').onclick = () => openLitDlg(null);
$('#litForm').onsubmit = async ev => {
  ev.preventDefault();
  const e = {
    id: editingLitId || '',
    title: $('#lTitle').value.trim(),
    authors: $('#lAuthors').value.trim(),
    year: $('#lYear').value.trim(),
    venue: $('#lVenue').value.trim(),
    entryType: $('#lEntryType').value.trim(),
    doi: $('#lDoi').value.trim(),
    pages: $('#lPages').value.trim(),
    url: $('#lUrl').value.trim(),
    abstract: $('#lAbstract').value,
    note: $('#lNote').value,
    citationKey: '',
  };
  if (!e.title) return;
  await post('/api/literature', e);
  $('#litDlg').close();
  log('文献已保存：' + e.title);
  await refresh();
};
$('#litDelete').onclick = async () => {
  if (!editingLitId) return;
  if (!confirm('确定删除这条文献？')) return;
  await del('/api/literature/' + editingLitId);
  $('#litDlg').close();
  log('文献已删除');
  await refresh();
};

$('#bibImportBtn').onclick = () => { $('#bibText').value = ''; $('#bibDlg').showModal(); };
$('#bibGo').onclick = async () => {
  try {
    const r = await post('/api/literature/import-bibtex', { text: $('#bibText').value });
    $('#bibDlg').close();
    log(`BibTeX 导入 ${r.imported} 条`);
    await refresh();
  } catch (e) { alert('导入失败：' + e.message); }
};

$('#gbtCopyBtn').onclick = async () => {
  const d = await api('/api/literature/gbt');
  await navigator.clipboard.writeText(d.text || '');
  log('GB/T 7714 文献表已复制到剪贴板');
};

$('#litSearchBtn').onclick = () => {
  $('#litSearchResults').innerHTML = '';
  $('#litSearchDlg').showModal();
};
$('#litSearchGo').onclick = async () => {
  const box = $('#litSearchResults');
  box.innerHTML = '<span class="muted">检索中…</span>';
  try {
    const d = await api(`/api/literature/search?q=${encodeURIComponent($('#litSearchQ').value)}&source=${$('#litSearchSrc').value}`);
    box.innerHTML = '';
    if (!d.results.length) { box.innerHTML = '<span class="muted">没有结果</span>'; return; }
    d.results.forEach(r => {
      const row = document.createElement('div');
      row.className = 'r';
      row.innerHTML = `<span class="m">${escapeHtml(r.display)}<br><span class="muted">${escapeHtml(r.authors || '')}</span></span>`;
      const btn = document.createElement('button');
      btn.className = 'mini-btn'; btn.textContent = '入库';
      btn.onclick = async () => {
        await post('/api/literature', {
          title: r.title, authors: r.authors, year: r.year, venue: r.venue, doi: r.doi,
        });
        btn.textContent = '已入库'; btn.disabled = true;
        log('文献入库：' + r.title);
        await refresh();
      };
      row.appendChild(btn);
      box.appendChild(row);
    });
  } catch (e) { box.innerHTML = '<span class="muted">检索失败：' + escapeHtml(e.message) + '</span>'; }
};

// ---------- 快照 ----------

async function loadSnapshots() {
  try {
    const d = await api('/api/snapshots');
    renderSnapshots(Array.isArray(d) ? d : d.snapshots || []);
  } catch { /* 静默 */ }
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

// ---------- 世界设定 / AI 记忆 ----------

function renderWorld(w) {
  $('#wName').value = w?.worldName || '';
  $('#wTime').value = w?.timePeriod || '';
  $('#wLocation').value = w?.location || '';
  $('#wMagic').value = w?.magicSystem || '';
  $('#wTech').value = w?.technologyLevel || '';
  $('#wBackground').value = w?.background || '';
}

$('#saveWorldBtn').onclick = async () => {
  await post('/api/world', {
    worldName: $('#wName').value, timePeriod: $('#wTime').value,
    location: $('#wLocation').value, magicSystem: $('#wMagic').value,
    technologyLevel: $('#wTech').value, background: $('#wBackground').value,
  });
  log('世界设定已保存');
};

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

$$('.export button').forEach(b => {
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
    $('#stats').textContent = s.today != null
      ? `今日 ${s.today} 字 · 连续 ${s.streak} 天 · 累计 ${s.activeDays} 天` : '';
  } catch { $('#stats').textContent = ''; }
}

$('#stats').onclick = async () => {
  const s = await api('/api/stats');
  if (s.today == null) return;
  $('#statsSummary').innerHTML =
    `<span>今日<b>${s.today}</b>字</span><span>连续<b>${s.streak}</b>天</span><span>累计<b>${s.activeDays}</b>天</span>`;
  const max = Math.max(1, ...(s.recent || []).map(d => d.words));
  $('#statsBars').innerHTML = (s.recent || []).map(d => {
    const h = Math.round(d.words / max * 80);
    const label = (d.date || '').slice(5);
    return `<div class="bar" title="${label}：${d.words} 字">` +
           `<span class="v">${d.words > 0 ? d.words : ''}</span>` +
           `<div class="col ${d.words === 0 ? 'zero' : ''}" style="height:${Math.max(h, 2)}px"></div>` +
           `<span class="d">${label}</span></div>`;
  }).join('');
  $('#statsDlg').showModal();
};

// ---------- AI 设置（方案管理） ----------

let providers = [];
let authChoices = [];

// 后端抛的是 "400 至少保留一个配置方案" 这种——状态码对用户没意义，剥掉
function errMsg(e) {
  return String(e?.message || e || '操作失败').replace(/^\d{3}\s*/, '');
}

async function openAiDlg() {
  const [pd, profs] = await Promise.all([
    api('/api/ai/providers'),
    api('/api/ai/profiles').catch(() => ({ profiles: [], active: null })),
  ]);
  providers = pd.providers || [];
  authChoices = pd.authChoices || [];

  const sel = $('#provSel');
  sel.innerHTML = '';
  providers.forEach(p => {
    const o = document.createElement('option');
    o.value = p.id; o.textContent = `${p.name}（${p.group}）`;
    sel.appendChild(o);
  });

  // 认证方式（与桌面端 AuthComboBox 同一组四项）
  const as = $('#authSel');
  as.innerHTML = '';
  (authChoices.length ? authChoices : [{ value: '', label: '自动（推荐）' }]).forEach(c => {
    const o = document.createElement('option');
    o.value = c.value; o.textContent = c.label;
    as.appendChild(o);
  });

  const ps = $('#profSel');
  ps.innerHTML = '';
  (profs.profiles || []).forEach(p => {
    const o = document.createElement('option');
    o.value = p.name;
    o.textContent = `${p.name}${p.name === profs.active ? '（当前）' : ''}`;
    ps.appendChild(o);
  });
  // 「有没有方案」永远为真——文件不存在时 Load() 会自己造一个「默认配置」
  // 空壳（桌面端同样如此，不改动它）。真正该提醒的是"没有一个是配好 Key 的"：
  // 那才是点 AI 会直接失败的时刻。
  const hasUsable = (profs.profiles || []).some(p => p.hasKey);
  $('#profEmpty').classList.toggle('hidden', hasUsable);

  // 预选当前方案；没有方案就按当前服务商把表单填一遍，省得从空开始
  if (profs.active || (profs.profiles || []).length) loadProfileForm(profs);
  else fillFromProvider(sel.value);

  $('#testResult').textContent = '';
  $('#aiDlg').showModal();
}

// 可用型号下拉。模型名可以手填（大小写敏感，服务商说改名就改名），
// 这个下拉只是"从已知清单里挑一个"，两者指向同一个输入框。
function renderModelPresets(list) {
  const sel = $('#modelPreset');
  sel.innerHTML = '';
  const none = document.createElement('option');
  none.value = ''; none.textContent = '（手动输入）';
  sel.appendChild(none);
  (list || []).forEach(m => {
    const id = typeof m === 'string' ? m : (m.id || '');
    const label = typeof m === 'string' ? m : (m.displayName || m.id || '');
    if (!id) return;
    const o = document.createElement('option');
    o.value = id; o.textContent = label;
    sel.appendChild(o);
  });
}

function fillFromProvider(pid) {
  const p = providers.find(x => x.id === pid);
  if (!p) return;
  $('#profUrl').value = p.endpoint || '';
  $('#profModel').value = p.defaultModel || '';
  $('#modelList').innerHTML = (p.models || []).map(m => `<option value="${escapeHtml(m)}">`).join('');
  renderModelPresets(p.models || []);
  $('#provNote').textContent = p.note || '';
  // 换服务商 → 认证方式回到「自动」，避免上一家的手工选择残留到这一家
  $('#authSel').value = '';
}

function loadProfileForm(profs) {
  const cur = (profs.profiles || []).find(p => p.name === profs.active) || (profs.profiles || [])[0];
  if (!cur) return;
  $('#profSel').value = cur.name;
  $('#provSel').value = cur.provider || 'openai';
  $('#profUrl').value = cur.apiUrl || '';
  $('#profModel').value = cur.model || '';
  $('#profKey').value = '';
  fillFromProvider(cur.provider);
  // ⚠ 认证的回显必须排在 fillFromProvider 之后——它会把认证复位成「自动」
  $('#authSel').value = cur.authOverride || '';
}

$('#provSel').onchange = () => fillFromProvider($('#provSel').value);
$('#profSel').onchange = async () => {
  const profs = await api('/api/ai/profiles');
  loadProfileForm(profs);
};

// 从「可用型号」里挑一个 → 直接填进模型名输入框（与桌面端 ModelPresetComboBox 同款）
$('#modelPreset').onchange = () => {
  const v = $('#modelPreset').value;
  if (v) $('#profModel').value = v;
};

// ＋ 新建：以当前方案为蓝本复制一份（Key 一起带过来）
$('#profNew').onclick = async () => {
  const name = (prompt('新方案名称？', '') || '').trim();
  if (!name) return;
  try {
    await post('/api/ai/profiles/new', { name, from: $('#profSel').value || '', activate: true });
  } catch (e) { alert(errMsg(e)); return; }
  log(`已新建 AI 方案「${name}」`);
  await openAiDlg();
  $('#profSel').value = name;
};

// 另存为：按表单里现在的值存成新名字；Key 从当前方案带过来（表单不回显明文 Key）
$('#profDup').onclick = async () => {
  const base = $('#profSel').value || '新方案';
  const name = (prompt('把这个配置另存为？', `${base} 副本`) || '').trim();
  if (!name) return;
  try {
    await post('/api/ai/profiles/new', {
      name,
      from: $('#profSel').value || '',
      provider: $('#provSel').value,
      apiUrl: $('#profUrl').value.trim(),
      model: $('#profModel').value.trim(),
      authOverride: $('#authSel').value || '',
      activate: true,
    });
  } catch (e) { alert(errMsg(e)); return; }
  log(`AI 配置已另存为「${name}」`);
  await openAiDlg();
  $('#profSel').value = name;
};

$('#profSave').onclick = async () => {
  const name = $('#profSel').value.trim() || $('#provSel option:checked').textContent.replace(/（.*）/, '');
  if (!name) return alert('方案名不能为空');
  try {
    await post('/api/ai/profiles/save', {
      name,
      provider: $('#provSel').value,
      apiUrl: $('#profUrl').value.trim(),
      model: $('#profModel').value.trim(),
      authOverride: $('#authSel').value || '',
      apiKey: $('#profKey').value || null,   // null = 保留原 Key
      activate: true,
    });
  } catch (e) { alert(errMsg(e)); return; }
  $('#testResult').textContent = `已保存并启用方案「${name}」`;
  log(`AI 方案已切换：${name}`);
  await openAiDlg();
};

$('#profDelete').onclick = async () => {
  const name = $('#profSel').value;
  if (!name || !confirm(`确定删除方案「${name}」？`)) return;
  try {
    await post('/api/ai/profiles/delete', { name });
  } catch (e) { alert(errMsg(e)); return; }   // 「至少保留一个」会从后端挡回来
  log(`AI 方案已删除：${name}`);
  await openAiDlg();
};
$('#aiDlgClose').onclick = () => $('#aiDlg').close();
$('#aiSetBtn').onclick = openAiDlg;

$('#profTest').onclick = async () => {
  const out = $('#testResult');
  out.textContent = '连接中…';
  const r = await post('/api/ai/profiles/test', {
    provider: $('#provSel').value,
    apiUrl: $('#profUrl').value.trim(),
    model: $('#profModel').value.trim(),
    authOverride: $('#authSel').value || '',   // 认证方式不同的话，测出来的结果也不同
    apiKey: $('#profKey').value || undefined,
  });
  out.textContent = r.ok ? '✓ ' + r.message : '✗ ' + r.message;
};

// 服务商控制台（一键去申请 Key，与桌面版那个按钮同一个 consoleUrl）
$('#provConsole').onclick = () => {
  const p = providers.find(x => x.id === $('#provSel').value);
  if (p?.consoleUrl) window.open(p.consoleUrl, '_blank');
  else alert('这家服务商没有登记控制台地址。');
};

// 拉取真实模型名（与桌面版 ApiSettingsWindow.FetchModelsAsync 同一个 ModelCatalog）
$('#fetchModels').onclick = async () => {
  const st = $('#modelStatus');
  st.textContent = '拉取中…';
  try {
    const r = await post('/api/ai/models', {
      name: $('#profSel').value,
      provider: $('#provSel').value,
      apiUrl: $('#profUrl').value.trim(),
      model: $('#profModel').value.trim(),
      authOverride: $('#authSel').value || '',
      apiKey: $('#profKey').value || undefined,
    });
    if (!r.ok) { st.textContent = '✗ ' + (r.error || '拉取失败'); return; }
    $('#modelList').innerHTML = (r.models || [])
      .map(m => `<option value="${escapeHtml(m.id)}">${escapeHtml(m.displayName || '')}</option>`).join('');
    renderModelPresets(r.models || []);   // 拉到的真实清单也要能直接挑
    st.textContent = `✓ 拉到 ${r.models.length} 个模型` + (r.nonChat ? `（${r.nonChat} 个非对话模型已收起）` : '');
  } catch (e) { st.textContent = '✗ ' + e.message; }
};

// JSON 编辑（桌面版 API 设置的「JSON 编辑」Tab）
$('#rawEditBtn').onclick = async () => {
  const d = await api('/api/ai/profiles/raw');
  $('#rawPath').textContent = d.path;
  $('#rawJson').value = d.text || '';
  $('#rawResult').textContent = '';
  $('#rawDlg').showModal();
};
$('#rawReload').onclick = async () => {
  const d = await api('/api/ai/profiles/raw');
  $('#rawJson').value = d.text || '';
  $('#rawResult').textContent = '已重新载入';
};
$('#rawFormat').onclick = () => {
  try {
    $('#rawJson').value = JSON.stringify(JSON.parse($('#rawJson').value), null, 2);
    $('#rawResult').textContent = '已格式化';
  } catch (e) { $('#rawResult').textContent = 'JSON 不合法：' + e.message; }
};
$('#rawSave').onclick = async () => {
  const r = await post('/api/ai/profiles/raw', { name: $('#rawJson').value });
  $('#rawResult').textContent = r.error || '已保存';
  if (!r.error) log('API 配置文件已保存');
};
$('#rawClose').onclick = () => $('#rawDlg').close();

// ---------- AI 生成 ----------

async function loadSkills() {
  try {
    const d = await api('/api/skills');
    const sel = $('#aiSkill');
    sel.innerHTML = '<option value="">技能：无</option>';
    (d.skills || []).forEach(s => {
      const o = document.createElement('option');
      o.value = s.id;
      o.textContent = `技能：${s.name}${s.isBuiltIn ? '' : '（自定义）'}`;
      o.title = s.description || '';
      sel.appendChild(o);
    });
  } catch { /* 静默 */ }
}

function renderCtxChapters() {
  const box = $('#ctxChapters');
  box.innerHTML = '';
  (book?.chapters || []).forEach(c => {
    if (c.n === current) return;   // 当前章正文走"待处理文本"，不需要勾
    const label = document.createElement('label');
    label.innerHTML = `<input type="checkbox" value="${escapeHtml(c.id || '')}"> ${c.n}. ${escapeHtml(c.title)}（${c.words}字）`;
    box.appendChild(label);
  });
  updateCtxCount();
}
function selectedCtxIds() {
  return $$('#ctxChapters input:checked').map(i => i.value).filter(Boolean);
}
function updateCtxCount() {
  const n = $$('#ctxChapters input:checked').length;
  $('#ctxCount').textContent = n ? `（已选 ${n} 章）` : '';
}
$('#ctxChapters').addEventListener('change', updateCtxCount);
$('#ctxAll').onclick = () => { $$('#ctxChapters input').forEach(i => i.checked = true); updateCtxCount(); };
$('#ctxNone').onclick = () => { $$('#ctxChapters input').forEach(i => i.checked = false); updateCtxCount(); };

// ---------- AI 生成（续写/润色/上下文/人名/聊天/审稿，全部走 /api/ai） ----------

let aiAbort = null;
let lastTokens = '';

async function runAi(task, extra = {}) {
  const out = $('#aiOut');
  out.textContent = '';
  const go = $('#aiGoBtn'), stop = $('#aiStopBtn');
  go.disabled = true; go.textContent = '生成中…';
  stop.disabled = false;
  lastTokens = '';
  updateChatInfo();

  // 续写/润色吃服务器上的正文，先把编辑器里未保存的字落盘
  // （顺带保证服务器在生成前拍的那张快照是最新的）
  await flushSave();

  const isChat = task === 'chat';
  const body = {
    task,
    prompt: $('#aiPrompt').value,
    maxTokens: 0,                       // 0 = 由服务器按任务给上限（与桌面端各生成方法一致）
    skillId: $('#aiSkill').value || null,
    relatedChapterIds: selectedCtxIds(),
    targetChapter: current,
    polishStyle: task === 'polish' ? ($('#polishStyle').value || null) : null,
    scaleHint: extra.scaleHint || null,
  };

  aiAbort = new AbortController();
  try {
    const res = await fetch('/api/ai', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: aiAbort.signal,
    });
    if (!res.ok) { out.textContent = await res.text(); return; }

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
        else if (ev.type === 'notice') out.textContent = `[${ev.payload}]\n` + out.textContent;
        else if (ev.type === 'progress') {
          lastTokens = `${ev.payload.inTok} in / ${ev.payload.outTok} out`;
          updateChatInfo();
        }
        else if (ev.type === 'chatinfo') { chatInfo = ev.payload; updateChatInfo(); }
        else if (ev.type === 'done') {
          out.textContent = ev.payload.text;
          if (isChat) updateChatInfo();
          if (ev.payload.applied) {
            log(`AI 已写入「${ev.payload.applied}」`);
            await refresh();
          }
        }
        else if (ev.type === 'stopped') log('生成已停止，保留已生成的部分');
        else if (ev.type === 'error') out.textContent = '出错：' + ev.payload;
      }
    }
  } catch (e) {
    if (e.name === 'AbortError') log('已停止生成（保留已出来的部分）');
    else out.textContent = '出错：' + e.message;
  } finally {
    aiAbort = null;
    go.disabled = false; go.textContent = '生成';
    stop.disabled = true;
  }
}

// 聊天记忆规模：轮数 /（被摘要掉的轮数）/ 估算占用 / 模型窗口。
// 不显示出来，用户就分不清「AI 忘了」和「本来没记」。
function updateChatInfo() {
  const parts = [];
  const c = chatInfo;
  if (c?.rounds) {
    let s = `聊天记忆：${c.rounds} 轮`;
    if (c.summarizedRounds) s += `（更早 ${c.summarizedRounds} 轮已摘要）`;
    parts.push(s);
  }
  if (c?.used) {
    const pct = c.window ? Math.round(c.used / c.window * 100) : 0;
    parts.push(`占用约 ${c.used}${c.window ? ` / ${c.window}（${pct}%）` : ''}`);
  }
  if (lastTokens) parts.push(`上轮 ${lastTokens}`);
  $('#chatInfo').textContent = parts.join(' · ');
  $('#chatClearBtn').disabled = !c?.rounds && !c?.summary;
}

async function loadChatInfo() {
  try { chatInfo = await api('/api/chat'); } catch { chatInfo = null; }
  updateChatInfo();
}

$('#chatClearBtn').onclick = async () => {
  if (!chatInfo?.rounds && !chatInfo?.summary) return;
  if (!confirm(`确定清空与 AI 的对话记忆吗？（当前 ${chatInfo?.rounds || 0} 轮）\n已经写进章节的正文不受影响。`)) return;
  try {
    await post('/api/chat/clear', {});
    chatInfo = await api('/api/chat');
    updateChatInfo();
    log('已清空与 AI 的对话记忆');
  } catch (e) { alert('清空失败：' + e.message); }
};

$('#aiGoBtn').onclick = () => runAi($('#aiTask').value);
$('#aiStopBtn').onclick = () => { if (aiAbort) aiAbort.abort(); };
$('#aiTask').onchange = () =>
  $('#polishRow').classList.toggle('hidden', $('#aiTask').value !== 'polish');

// ---------- AI 面板浮动（对应桌面端的「AI 助手独立浮窗」） ----------
// 桌面版能把这个面板弹成独立窗口、拖到任意位置；网页版就在同一个页面里
// 把它变成可拖动浮层 —— 效果一样（正文区占满宽度、面板悬在上面）。

const rightPanel = $('#right');
const floatBtn = $('#floatAiBtn');

function setFloat(on, pos) {
  rightPanel.classList.toggle('floating', on);
  floatBtn.textContent = on ? '停靠' : '浮动';
  floatBtn.title = on
    ? '把 AI 面板收回右侧栏'
    : '把 AI 面板变成可拖动的浮层，正文区就能占满宽度（对应桌面端的独立浮窗）';
  if (on && pos) {
    rightPanel.style.left = pos.left + 'px';
    rightPanel.style.top = pos.top + 'px';
    rightPanel.style.right = 'auto';
    rightPanel.style.bottom = 'auto';
  } else {
    ['left', 'top', 'right', 'bottom'].forEach(k => { rightPanel.style[k] = ''; });
  }
  try {
    localStorage.setItem('aiPanelFloat', on ? '1' : '0');
    if (on && pos) localStorage.setItem('aiPanelPos', JSON.stringify(pos));
  } catch { /* 隐私模式禁 localStorage，不影响功能 */ }
}

floatBtn.onclick = () => setFloat(!rightPanel.classList.contains('floating'), null);

let aiDragOff = null;
// ⚠ 这里**不能**叫 clamp：appearance.js 已经在顶层声明了同名的 clamp，
//   两个文件都是普通 <script>，共用同一个全局作用域 —— 重名会让 app.js
//   整个文件在**解析期**就报 SyntaxError，后面所有初始化一行都不跑，
//   表现是"页面能打开、但书列表颜色主题全是空的"，极难往这上面想。
const clampUi = (v, max) => Math.min(Math.max(0, v), Math.max(0, max));
$('#aiPanelHead').addEventListener('pointerdown', e => {
  // 抓手只在浮动模式生效，且不能把「浮动/停靠」按钮的点击吃掉
  if (!rightPanel.classList.contains('floating') || e.target.closest('button')) return;
  const r = rightPanel.getBoundingClientRect();
  aiDragOff = { x: e.clientX - r.left, y: e.clientY - r.top };
  $('#aiPanelHead').setPointerCapture?.(e.pointerId);
  e.preventDefault();
});
$('#aiPanelHead').addEventListener('pointermove', e => {
  if (!aiDragOff) return;
  const r = rightPanel.getBoundingClientRect();
  rightPanel.style.left = clampUi(e.clientX - aiDragOff.x, window.innerWidth - r.width) + 'px';
  rightPanel.style.top = clampUi(e.clientY - aiDragOff.y, window.innerHeight - r.height) + 'px';
  rightPanel.style.right = 'auto';
  rightPanel.style.bottom = 'auto';
});
const endAiDrag = () => {
  if (!aiDragOff) return;
  aiDragOff = null;
  try {
    const r = rightPanel.getBoundingClientRect();
    localStorage.setItem('aiPanelPos', JSON.stringify({ left: Math.round(r.left), top: Math.round(r.top) }));
  } catch { }
};
$('#aiPanelHead').addEventListener('pointerup', endAiDrag);
$('#aiPanelHead').addEventListener('pointercancel', endAiDrag);

// 恢复上次的浮动状态。窄屏不自动浮动 —— 浮层会盖住大半屏幕，反而更没法写。
(function restoreFloat() {
  try {
    if (localStorage.getItem('aiPanelFloat') !== '1' || window.innerWidth < 1100) return;
    const raw = localStorage.getItem('aiPanelPos');
    setFloat(true, raw ? JSON.parse(raw) : null);
  } catch { }
})();

// ---------- 生成上下文（与桌面端 AI 面板那组按钮同一批任务） ----------
// 服务器侧按"已有内容是否为空"决定用生成还是扩写提示词，成功后自动写回设定。

$$('.ctx-gen-btns button').forEach(b => {
  b.onclick = async () => {
    const task = b.dataset.ctx;
    let scaleHint = null;
    // 首次生成全文大纲前问规模（与桌面版 NovelScaleDialog 同一时机）
    if (task === 'outline' && !(book?.outline || '').trim()) {
      const s = await askScale();
      if (s === null) return;      // 用户取消
      scaleHint = s || null;       // 跳过 → 不传
    }
    await runAi(task, { scaleHint });
  };
});

const SCALES = [
  ['short', '短篇小说', '1~5 万字，5~15 章，单线叙事'],
  ['medium', '中篇小说', '5~20 万字，15~40 章，双线叙事'],
  ['long', '长篇小说', '20~50 万字，40~100 章，多线叙事'],
  ['extralong', '超长篇小说', '50 万字以上，100 章以上，宏大世界观'],
];
let scalePick = 'medium';
function askScale() {
  return new Promise(resolve => {
    const box = $('#scaleList');
    box.innerHTML = '';
    SCALES.forEach(([id, name, desc]) => {
      const el = document.createElement('label');
      el.className = 'scale-item' + (id === scalePick ? ' on' : '');
      el.innerHTML = `<input type="radio" name="scale" value="${id}" ${id === scalePick ? 'checked' : ''}>` +
        `<span class="scale-name">${name}</span><span class="ap-desc">${desc}</span>`;
      el.querySelector('input').onchange = () => {
        scalePick = id;
        $$('#scaleList .scale-item').forEach(x => x.classList.toggle('on', x.querySelector('input').checked));
      };
      box.appendChild(el);
    });
    function cleanup() {
      $('#scaleOk').removeEventListener('click', onOk);
      $('#scaleSkip').removeEventListener('click', onSkip);
      $('#scaleDlg').removeEventListener('cancel', onCancel);
    }
    const done = v => { $('#scaleDlg').close(); cleanup(); resolve(v); };
    const onOk = () => done(SCALES.find(s => s[0] === scalePick)[2]);
    const onSkip = () => done('');
    const onCancel = () => done(null);
    $('#scaleOk').addEventListener('click', onOk);
    $('#scaleSkip').addEventListener('click', onSkip);
    $('#scaleDlg').addEventListener('cancel', onCancel);
    $('#scaleDlg').showModal();
  });
}

// ---------- 润色风格预设（与桌面版共用 polish_presets.json） ----------

async function loadPolishPresets() {
  try {
    const d = await api('/api/polish-presets');
    const sel = $('#polishStyle');
    const cur = sel.value;
    sel.innerHTML = '<option value="">风格：无</option>';
    (d.presets || []).forEach(p => {
      const o = document.createElement('option');
      o.value = p; o.textContent = '风格：' + p;
      sel.appendChild(o);
    });
    if (cur) sel.value = cur;
  } catch { /* 静默 */ }
}

$('#polishAdd').onclick = async () => {
  const box = $('#aiPrompt');
  const name = (prompt('新风格名称？（润色时作为要求附加）') || '').trim();
  if (!name) return;
  const r = await post('/api/polish-presets', { name });
  if (r.error) return alert(r.error);
  await loadPolishPresets();
  $('#polishStyle').value = name;
  log('润色风格已新增：' + name);
};
$('#polishDel').onclick = async () => {
  const name = $('#polishStyle').value;
  if (!name) return alert('先选中一个风格');
  const r = await post('/api/polish-presets', { name, delete: true });
  if (r.error) return alert(r.error);
  await loadPolishPresets();
  $('#polishStyle').value = '';
  log('润色风格已删除：' + name);
};

// ---------- AI 结果应用：追加到正文 / 应用到设定 / 复制 ----------

$('#aiAppendBtn').onclick = () => {
  const text = $('#aiOut').textContent.trim();
  if (!text || current == null) return;
  const ed = $('#editor');
  ed.value = ed.value.replace(/\s+$/, '') + '\n\n' + text + '\n';
  markDirtyAfterReplace();
  log('AI 结果已追加到本章末尾');
};

const APPLY_LABELS = {
  FullOutline: '全文大纲', ChapterOutline: '章节大纲', CharacterSettings: '人物设定',
  BackgroundSettings: '背景设定', WritingStyle: '文风设定', NarrativeViewpoint: '叙事视角',
};
const APPLY_KEYS = {
  FullOutline: 'outline', ChapterOutline: 'chapterOutline', CharacterSettings: 'characters',
  BackgroundSettings: 'background', WritingStyle: 'style', NarrativeViewpoint: 'viewpoint',
};
$('#aiApplyTo').onchange = async () => {
  const field = $('#aiApplyTo').value;
  $('#aiApplyTo').value = '';
  if (!field) return;
  const text = $('#aiOut').textContent.trim();
  if (!text) { log('AI 回复为空，请先生成内容'); return; }

  if ((book?.[APPLY_KEYS[field]] || '').trim()
      && !confirm(`「${APPLY_LABELS[field]}」已有内容，是否覆盖？`)) return;

  const payload = {};
  payload[APPLY_KEYS[field]] = text;
  await post('/api/settings', payload);
  log(`已应用到「${APPLY_LABELS[field]}」`);
  await refresh();
};

$('#aiCopyBtn').onclick = async () => {
  const t = $('#aiOut').textContent.trim();
  if (t) { await navigator.clipboard.writeText(t); log('AI 结果已复制'); }
};

// ---------- 提示词方案（SystemPromptStore 同源） ----------

let promptData = null;

async function loadPresetSelect() {
  try {
    const d = await api('/api/prompts');
    promptData = d;
    const sel = $('#aiPreset');
    sel.innerHTML = '';
    (d.presets || []).forEach(p => {
      const o = document.createElement('option');
      o.value = p.id;
      o.textContent = p.isBuiltIn ? p.name : `${p.name}（自定义）`;
      sel.appendChild(o);
    });
    sel.value = d.active;
  } catch { /* 静默 */ }
}

$('#aiPreset').onchange = async () => {
  await post('/api/prompts/active', { presetId: $('#aiPreset').value });
  log(`提示词方案已切换：${$('#aiPreset option:checked').textContent}（桌面端同步生效）`);
  // 方案记住的技能跟着切（与桌面版 OnPresetChanged 同款）
  const remembered = promptData?.skillByPreset?.[$('#aiPreset').value];
  await loadSkills();
  if (remembered) $('#aiSkill').value = remembered;
};

async function openPromptDlg() {
  await loadPresetSelect();
  renderPromptDlg();
  $('#promptDlg').showModal();
}

function renderPromptDlg() {
  const d = promptData;
  if (!d) return;
  const sel = $('#pPresetSel');
  sel.innerHTML = '';
  d.presets.forEach(p => {
    const o = document.createElement('option');
    o.value = p.id;
    o.textContent = p.isBuiltIn ? p.name : `${p.name}（自定义）`;
    sel.appendChild(o);
  });
  sel.value = $('#pPresetSel').dataset.cur || d.active;
  renderPromptEntries();
}

async function renderPromptEntries() {
  const d = promptData;
  if (!d) return;
  const presetId = $('#pPresetSel').value;
  $('#pPresetSel').dataset.cur = presetId;
  const preset = d.presets.find(p => p.id === presetId);
  $('#pBasedOn').value = preset?.isBuiltIn ? '内置方案' : '自定义（跟随依据方案的内置改进）';
  $('#pDelete').disabled = !!preset?.isBuiltIn;
  $('#pRename').disabled = !!preset?.isBuiltIn;

  const texts = d.texts[presetId] || {};
  const box = $('#pEntries');
  box.innerHTML = '';
  let lastGroup = null;
  for (const e of d.entries) {
    if (e.group !== lastGroup) {
      lastGroup = e.group;
      const h = document.createElement('div');
      h.className = 'p-group';
      h.textContent = e.group;
      box.appendChild(h);
    }
    const info = texts[e.key] || { text: '', overridden: false };
    const det = document.createElement('details');
    det.className = 'p-entry' + (info.overridden ? ' overridden' : '');
    det.innerHTML =
      `<summary>${escapeHtml(e.title)}${info.overridden ? ' <span class="tag">已覆写</span>' : ''}</summary>` +
      `<div class="muted" style="margin:4px 0">${escapeHtml(e.description)}</div>` +
      `<textarea rows="5">${escapeHtml(info.text)}</textarea>` +
      `<div class="dlg-btns" style="margin:4px 0">` +
      `<button type="button" class="mini-btn p-save">保存修改</button>` +
      `<button type="button" class="mini-btn p-reset" ${info.overridden ? '' : 'disabled'}>恢复默认</button></div>`;
    det.querySelector('.p-save').onclick = async ev => {
      const r = await post('/api/prompts/set', { presetId, key: e.key, value: det.querySelector('textarea').value });
      await reloadPromptData();
      renderPromptEntries();
      ev.target.closest('details').querySelector('summary').click();
      log(`提示词已更新：${e.title}（${r.text === det.querySelector('textarea').value ? '已生效' : '等同默认，已视为恢复默认'}）`);
    };
    det.querySelector('.p-reset').onclick = async () => {
      await post('/api/prompts/reset', { presetId, key: e.key });
      await reloadPromptData();
      renderPromptEntries();
    };
    box.appendChild(det);
  }
}

async function reloadPromptData() { promptData = await api('/api/prompts'); }

$('#pPresetSel').onchange = renderPromptEntries;
$('#pActivate').onclick = async () => {
  const id = $('#pPresetSel').value;
  await post('/api/prompts/active', { presetId: id });
  await loadPresetSelect();
  log(`提示词方案已启用：${$('#aiPreset option:checked').textContent}`);
};
$('#pNewCustom').onclick = async () => {
  const name = prompt('新方案名称？', '我的方案');
  if (!name) return;
  await post('/api/prompts/custom', { basedOn: $('#pPresetSel').value, name });
  await reloadPromptData();
  const created = promptData.presets.find(p => !p.isBuiltIn && p.name === name.trim());
  if (created) $('#pPresetSel').dataset.cur = created.id;
  renderPromptDlg();
  log(`已新建自定义方案「${name}」（继承当前方案的覆写语义，未改条目跟随内置改进）`);
};
$('#pRename').onclick = async () => {
  const id = $('#pPresetSel').value;
  const cur = promptData.presets.find(p => p.id === id);
  const name = prompt('新名称？', cur?.name || '');
  if (!name) return;
  await post('/api/prompts/custom', { id, renameTo: name });
  await reloadPromptData();
  $('#pPresetSel').dataset.cur = id;
  renderPromptDlg();
};
$('#pDelete').onclick = async () => {
  const id = $('#pPresetSel').value;
  const cur = promptData.presets.find(p => p.id === id);
  if (!confirm(`删除自定义方案「${cur?.name}」？`)) return;
  await post('/api/prompts/custom', { id, delete: true });
  await reloadPromptData();
  delete $('#pPresetSel').dataset.cur;
  renderPromptDlg();
};
$('#pResetAll').onclick = async () => {
  if (!confirm('把这个方案的全部条目恢复默认？')) return;
  await post('/api/prompts/reset', { presetId: $('#pPresetSel').value, all: true });
  await reloadPromptData();
  renderPromptEntries();
};
$('#pClose').onclick = () => $('#promptDlg').close();
$('#promptBtn').onclick = openPromptDlg;

// ---------- 技能管理 ----------

let skillsCache = [], editingSkillId = null;

async function openSkillDlg() {
  const d = await api('/api/skills');
  skillsCache = d.skills || [];
  renderSkillList();
  if (skillsCache.length) fillSkillForm(skillsCache[0]);
  $('#skillDlg').showModal();
}

function renderSkillList() {
  const ul = $('#skillList');
  ul.innerHTML = '';
  skillsCache.forEach(s => {
    const li = document.createElement('li');
    if (s.id === editingSkillId) li.className = 'active';
    li.innerHTML = `<span>${escapeHtml(s.name)}</span><span class="w">${s.isBuiltIn ? '内置' : '自定义'}</span>`;
    li.onclick = () => fillSkillForm(s);
    ul.appendChild(li);
  });
}

function fillSkillForm(s) {
  editingSkillId = s.id;
  renderSkillList();
  $('#sName').value = s.name || '';
  $('#sDesc').value = s.description || '';
  $('#sApplies').value = (s.appliesTo || []).join(', ');
  $('#sPresets').value = (s.presets || []).join(', ');
  $('#sHint').value = s.inputHint || '';
  $('#sTask').value = s.taskPrompt || '';
  $('#sContract').value = s.outputContract || '';
  $('#sName').disabled = $('#sDesc').disabled = $('#sApplies').disabled =
  $('#sPresets').disabled = $('#sHint').disabled = $('#sTask').disabled =
  $('#sContract').disabled = $('#sSave').disabled = $('#sDelete').disabled = s.isBuiltIn;
}

function skillFromForm() {
  return {
    id: editingSkillId,
    name: $('#sName').value.trim(),
    description: $('#sDesc').value.trim(),
    appliesTo: $('#sApplies').value.split(/[,，]/).map(x => x.trim()).filter(Boolean),
    presets: $('#sPresets').value.split(/[,，]/).map(x => x.trim()).filter(Boolean),
    inputHint: $('#sHint').value,
    taskPrompt: $('#sTask').value,
    outputContract: $('#sContract').value || null,
  };
}

$('#sNew').onclick = () => {
  fillSkillForm({ id: '', name: '', description: '', appliesTo: [], presets: [], inputHint: '', taskPrompt: '', outputContract: null });
  $('#sName').disabled = $('#sDesc').disabled = $('#sApplies').disabled =
  $('#sPresets').disabled = $('#sHint').disabled = $('#sTask').disabled =
  $('#sContract').disabled = $('#sSave').disabled = $('#sDelete').disabled = false;
  editingSkillId = '';
  $('#sName').focus();
};
$('#sSave').onclick = async () => {
  const r = await post('/api/skills/save', skillFromForm());
  if (r.error) return alert(r.error);
  log('技能已保存：' + $('#sName').value);
  const d = await api('/api/skills');
  skillsCache = d.skills || [];
  editingSkillId = r.id;
  fillSkillForm(skillsCache.find(s => s.id === r.id) || skillsCache[0]);
  await loadSkills();
};
$('#sDelete').onclick = async () => {
  if (!editingSkillId || !confirm('删除这个技能？')) return;
  const r = await post(`/api/skills/${editingSkillId}/delete`, {});
  if (r.error) return alert(r.error);
  log('技能已删除');
  skillsCache = (await api('/api/skills')).skills || [];
  editingSkillId = skillsCache[0]?.id || null;
  if (editingSkillId) fillSkillForm(skillsCache[0]); else renderSkillList();
  await loadSkills();
};
$('#sExport').onclick = async () => {
  if (!editingSkillId) return;
  const json = await api(`/api/skills/${editingSkillId}/export`);
  await navigator.clipboard.writeText(JSON.stringify(json, null, 2));
  log('技能 JSON 已复制到剪贴板');
};
$('#sImport').onclick = async () => {
  const json = prompt('粘贴技能 JSON（单个或数组）：');
  if (!json) return;
  const r = await post('/api/skills/import', { json });
  if (r.error) return alert(r.error);
  log(`已导入 ${r.imported} 个技能`);
  skillsCache = (await api('/api/skills')).skills || [];
  await loadSkills();
  renderSkillList();
};
$('#skillClose').onclick = () => $('#skillDlg').close();
$('#skillBtn').onclick = openSkillDlg;

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
      refresh();   // 正在编辑的章节由 openChapter/flushSave 机制保护
    } else if (e.kind === 'chapter-saved') {
      log(`第 ${e.payload.n} 章已保存`);
    }
  });

  conn.on('memory-changed', () => { log('AI 记忆已更新'); loadMemory(); });

  conn.on('live', e => {   // 第二个 live 处理器：SignalR 允许同一事件多次注册
    if (e.kind === 'settingsbook-changed' && !$('#view-sbook').classList.contains('hidden')) {
      loadSettingsBook();
    }
  });

  conn.on('mcpProgress', o => {
    const p = o?.params || {};
    log(p.message || JSON.stringify(o), true);
  });

  conn.start().catch(e => console.warn('实时连接失败', e));
}

// ---------- 杂项 ----------

function switchView(v) {
  $$('.tab').forEach(t => t.classList.toggle('active', t.dataset.view === v));
  ['write', 'outline', 'characters', 'literature', 'sbook', 'snapshots', 'settings'].forEach(n => {
    $('#view-' + n).classList.toggle('hidden', n !== v);
  });
  if (v === 'characters') loadCharacters();
  if (v === 'literature') loadLiterature();
  if (v === 'snapshots') loadSnapshots();
  if (v === 'sbook') loadSettingsBook();
}
$$('.tab').forEach(t => t.onclick = () => switchView(t.dataset.view));

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

// ---------- 设定集（与桌面版同一本 SettingsBook） ----------

let sbChapters = [], sbCurrentId = null;

async function loadSettingsBook() {
  const d = await api('/api/settingsbook');
  if (!d || d.open === false) { $('#sbChapters').innerHTML = '<li class="muted">先打开一本书</li>'; return; }
  sbChapters = d.chapters || [];
  $('#sbBookTitle').textContent = `${d.title || '设定集'}${d.subtitle ? ' · ' + d.subtitle : ''}`;
  const ul = $('#sbChapters');
  ul.innerHTML = '';
  sbChapters.forEach(c => {
    const li = document.createElement('li');
    if (c.chapterId === sbCurrentId) li.className = 'active';
    li.innerHTML = `<span>${escapeHtml(c.title)}</span>` +
      `<span class="w">${c.isAiGenerated ? 'AI' : ''}${c.includeInExport ? '' : ' 🚫'}</span>`;
    li.onclick = () => selectSbChapter(c.chapterId);
    ul.appendChild(li);
  });
  if (!sbCurrentId || !sbChapters.some(c => c.chapterId === sbCurrentId)) {
    if (sbChapters.length) selectSbChapter(sbChapters[0].chapterId);
  } else selectSbChapter(sbCurrentId);   // 重新载入当前章内容（可能被 agent 改过）
}

function selectSbChapter(id) {
  sbCurrentId = id;
  const c = sbChapters.find(x => x.chapterId === id);
  if (!c) return;
  $$('#sbChapters li').forEach(li => {
    li.classList.toggle('active', li.querySelector('span').textContent === c.title);
  });
  $('#sbChTitle').value = c.title + `（来源：${c.sourceLabel || c.sourceKey}）`;
  $('#sbContent').value = c.content || '';
  $('#sbInclude').checked = c.includeInExport !== false;
  $('#sbState').textContent = c.modified ? '改于 ' + new Date(c.modified).toLocaleString() : '';
}

let sbSaveTimer = null;
$('#sbContent').addEventListener('input', () => {
  $('#sbState').textContent = '未保存';
  clearTimeout(sbSaveTimer);
  sbSaveTimer = setTimeout(saveSbContent, 1200);
});
$('#sbInclude').addEventListener('change', async () => {
  await saveSbContent(true);
});

async function saveSbContent(includeOnly) {
  if (!sbCurrentId) return;
  const body = { chapterId: sbCurrentId, includeInExport: $('#sbInclude').checked };
  if (!includeOnly) body.content = $('#sbContent').value;
  await post('/api/settingsbook/chapter', body);
  $('#sbState').textContent = '已保存 ' + new Date().toLocaleTimeString();
  const c = sbChapters.find(x => x.chapterId === sbCurrentId);
  if (c && !includeOnly) { c.content = body.content; c.modified = new Date().toISOString(); }
}

$('#sbAiBtn').onclick = async () => {
  if (!sbCurrentId) return;
  const btn = $('#sbAiBtn'), out = $('#sbState');
  btn.disabled = true;
  out.textContent = 'AI 生成中…';
  try {
    const res = await fetch('/api/settingsbook/generate', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ chapterId: sbCurrentId }),
    });
    if (!res.ok) { out.textContent = await res.text(); return; }
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '', text = '';
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      const parts = buf.split('\n\n');
      buf = parts.pop();
      for (const p of parts) {
        if (!p.startsWith('data: ')) continue;
        const ev = JSON.parse(p.slice(6));
        if (ev.type === 'text') { text = ev.payload; $('#sbContent').value = text; out.textContent = '生成中… ' + text.length + ' 字'; }
        else if (ev.type === 'error') out.textContent = '出错：' + ev.payload;
        else if (ev.type === 'done') {
          if (ev.payload.saved) { out.textContent = '已生成并保存 ' + new Date().toLocaleTimeString(); log(`设定集「${$('#sbChTitle').value}」已由 AI 生成`, true); }
          else out.textContent = '生成结果不可用';
        }
      }
    }
  } catch (e) { out.textContent = '出错：' + e.message; }
  finally { btn.disabled = false; }
};

const sbExport = fmt => async () => {
  try {
    const res = await fetch('/api/settingsbook/export?format=' + fmt);
    if (!res.ok) { alert(await res.text()); return; }
    const blob = await res.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = decodeURIComponent((res.headers.get('Content-Disposition') || '').match(/filename\*?=(?:UTF-8''|")?([^";]+)/)?.[1] || `设定集.${fmt}`);
    a.click();
    URL.revokeObjectURL(a.href);
    log('设定集已导出：' + fmt.toUpperCase());
  } catch (e) { alert('导出失败：' + e.message); }
};
$('#sbExportDocx').onclick = sbExport('docx');
$('#sbExportPdf').onclick = sbExport('pdf');
$('#sbExportTxt').onclick = sbExport('txt');

// ---------- 帮助：关于 / 快捷键 / 接入 Agent ----------

const REPO_URL = 'https://github.com/zhou2228653446/DDD_WRITING_HELPER';
$('#repoLink').textContent = REPO_URL;
$('#repoLink').href = REPO_URL;

$$('#helpDlg .help-tabs button').forEach(b => {
  b.onclick = () => {
    $$('#helpDlg .help-tabs button').forEach(x => x.classList.toggle('active', x === b));
    ['about', 'keys', 'agents'].forEach(k =>
      $('#ht-' + k).classList.toggle('hidden', k !== b.dataset.htab));
    if (b.dataset.htab === 'agents') loadAgentClients();
  };
});
$('#helpBtn').onclick = () => $('#helpDlg').showModal();

// 网页版工作区本身就是一个 HTTP MCP 端点。写清楚用哪种：装了 exe 的走 stdio 最稳，
// 这条路只对"能走 Streamable HTTP 的客户端"有意义，且必须服务器开着。
$('#httpMcpHint').innerHTML =
  `另一个选择：当前网页工作区本身就是一个 HTTP MCP 端点 ` +
  `<code>${location.origin}/mcp</code>（免装 exe，改的就是网页上正开着的这本书）。` +
  `只在客户端支持 Streamable HTTP、且这个网页服务器开着时可用；` +
  `没在 Codex 客户端里实测过，求稳还是用上面 stdio 那份配置。`;

let agentClientsLoaded = false;
async function loadAgentClients() {
  if (agentClientsLoaded) return;
  const list = $('#agentList');
  list.innerHTML = '<div class="muted" style="font-size:13px">正在检测本机装了哪些 agent 客户端…</div>';
  try {
    const d = await api('/api/mcp/clients');
    if (!d.available) {
      // 拿不到清单时要说人话，并给出第二条路（GUI 装不了就让人跑脚本）
      $('#agentHint').textContent = (d.hint || '读不到客户端清单。') + ' 也可以直接双击仓库根的 install-mcp.bat。';
      list.innerHTML = '';
      return;
    }
    agentClientsLoaded = true;
    $('#agentHint').innerHTML =
      `服务器：<code>${escapeHtml(d.exe || '')}</code>` +
      (d.exeExists ? '' : ' —— <b>还没发布</b>，先在仓库根双击 deploy.bat；否则客户端连上也会失败。');
    renderAgentClients(d.clients || []);
  } catch (e) {
    $('#agentHint').textContent = '读取失败：' + e.message;
    list.innerHTML = '';
  }
}

function renderAgentClients(clients) {
  const list = $('#agentList');
  list.innerHTML = '';
  // 检测到的排前面——用户最想先确认的是"我装的那家接上了没"
  clients.slice().sort((a, b) => (b.detected ? 1 : 0) - (a.detected ? 1 : 0)).forEach(c => {
    const d = document.createElement('div');
    d.className = 'agent-item';
    d.innerHTML =
      `<div class="agent-head"><b>${escapeHtml(c.name)}</b>` +
      `<span class="badge ${c.detected ? 'on' : ''}">${c.detected ? '已检测到' : '未检测到'}</span>` +
      `<span class="grow"></span>` +
      `<button type="button" class="mini-btn" data-copy="1">复制配置片段</button></div>` +
      `<div class="agent-path">${escapeHtml(c.configPath)}</div>` +
      (c.note ? `<div class="agent-note">${escapeHtml(c.note)}</div>` : '') +
      `<pre>${escapeHtml(c.snippet)}</pre>`;
    d.querySelector('[data-copy]').onclick = async () => {
      await navigator.clipboard.writeText(c.snippet);
      log(`已复制「${c.name}」的 MCP 配置片段`);
    };
    list.appendChild(d);
  });
}

// ---------- 视图开关（对应桌面端「视图」菜单的项目管理器 / AI助手面板） ----------

[['#toggleChapters', '#chapters', 'showChapters'],
 ['#toggleRight', '#right', 'showRightPanel']].forEach(([btn, panel, key]) => {
  const el = $(panel);
  const apply = on => {
    el.classList.toggle('hidden', !on);
    $(btn).classList.toggle('off', !on);
    try { localStorage.setItem(key, on ? '1' : '0'); } catch { /* 隐私模式 */ }
  };
  $(btn).onclick = () => apply(el.classList.contains('hidden'));
  let saved = null;
  try { saved = localStorage.getItem(key); } catch { }
  if (saved === '0') apply(false);
});

// 键盘：Ctrl+S 保存，Ctrl+F 查找，Ctrl+Enter 生成
document.addEventListener('keydown', e => {
  if (e.ctrlKey && e.key === 's') { e.preventDefault(); flushSave(); }
  if (e.ctrlKey && e.key === 'f' && !$('#view-write').classList.contains('hidden')) {
    e.preventDefault(); findOpen();
  }
  if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); $('#aiGoBtn').click(); }
});

loadBooks().catch(e => console.error(e));
loadSkills();
loadPresetSelect();
loadPolishPresets();
loadAppearance().catch(e => console.error(e));
connect();
