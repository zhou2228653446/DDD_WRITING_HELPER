// 网页版：人与 agent 共用的写作界面。
// 前端刻意不引构建工具链——改完刷新即可，不增加维护负担。

const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let book = null;          // 当前书（含章节列表）
let current = null;       // 当前章节号
let stamp = null;         // 打开时的版本号，用来发现"书被别处改过"
let saveTimer = null;
let dirty = false;
let chatHistory = [];     // 万能写作的对话记忆（本轮会话内）

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

// ---------- 配色主题（与桌面版 AppearanceManager 同名同色） ----------

const THEMES = [
  ['default-white', '温润纸白'], ['night-mode', '墨色玻璃'],
  ['green-theme', '雾绿纸张'], ['yellow-theme', '暖砂纸卷'],
  ['ocean-blue', '深海蓝'], ['mist-gray', '晨雾灰'],
  ['terracotta', '赤陶'], ['ink-pine', '松墨'],
];
{
  const sel = $('#themeSel');
  for (const [id, name] of THEMES) {
    const o = document.createElement('option');
    o.value = id; o.textContent = name;
    sel.appendChild(o);
  }
  const saved = localStorage.getItem('theme') || 'default-white';
  document.documentElement.dataset.scheme = saved;
  sel.value = saved;
  sel.onchange = () => {
    document.documentElement.dataset.scheme = sel.value;
    localStorage.setItem('theme', sel.value);
  };
}

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
  await Promise.all([loadCharacters(), loadSnapshots(), loadMemory(), loadLiterature()]);
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
window.addEventListener('beforeunload', () => { if (dirty) flushSave(); });

// 章节标题：失焦即改名
$('#chTitle').addEventListener('change', async () => {
  if (current == null) return;
  await post(`/api/chapter/${current}/rename`, { name: $('#chTitle').value });
  log(`章节改名：${$('#chTitle').value}`);
  await refresh();
});

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

// ---------- 大纲（卡片 + 单章梗概编辑） ----------

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
  $('#outlineStats').textContent =
    `共 ${list.length} 章 · 未写 ${unwritten} 章 · 缺梗概 ${noSummary} 章`;
}

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
async function openAiDlg() {
  const [pd, profs] = await Promise.all([
    api('/api/ai/providers'),
    api('/api/ai/profiles').catch(() => ({ profiles: [], active: null })),
  ]);
  providers = pd.providers || [];

  const sel = $('#provSel');
  sel.innerHTML = '';
  providers.forEach(p => {
    const o = document.createElement('option');
    o.value = p.id; o.textContent = `${p.name}（${p.group}）`;
    sel.appendChild(o);
  });

  const ps = $('#profSel');
  ps.innerHTML = '';
  (profs.profiles || []).forEach(p => {
    const o = document.createElement('option');
    o.value = p.name;
    o.textContent = `${p.name}${p.name === profs.active ? '（当前）' : ''}`;
    ps.appendChild(o);
  });

  // 预选当前方案
  if (profs.active) loadProfileForm(profs);
  else sel.selectedIndex >= 0 && fillFromProvider(sel.value);

  $('#testResult').textContent = '';
  $('#aiDlg').showModal();
}

function fillFromProvider(pid) {
  const p = providers.find(x => x.id === pid);
  if (!p) return;
  $('#profUrl').value = p.endpoint || '';
  $('#profModel').value = p.defaultModel || '';
  $('#modelList').innerHTML = (p.models || []).map(m => `<option value="${m}">`).join('');
  $('#provNote').textContent = p.note || '';
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
}

$('#provSel').onchange = () => fillFromProvider($('#provSel').value);
$('#profSel').onchange = async () => {
  const profs = await api('/api/ai/profiles');
  loadProfileForm(profs);
};

$('#profSave').onclick = async () => {
  const name = $('#profSel').value.trim() || $('#provSel option:checked').textContent.replace(/（.*）/, '');
  if (!name) return alert('方案名不能为空');
  await post('/api/ai/profiles/save', {
    name,
    provider: $('#provSel').value,
    apiUrl: $('#profUrl').value.trim(),
    model: $('#profModel').value.trim(),
    apiKey: $('#profKey').value || null,   // null = 保留原 Key
    activate: true,
  });
  $('#testResult').textContent = `已保存并启用方案「${name}」`;
  log(`AI 方案已切换：${name}`);
  await openAiDlg();
};

$('#profDelete').onclick = async () => {
  const name = $('#profSel').value;
  if (!name || !confirm(`确定删除方案「${name}」？`)) return;
  await post('/api/ai/profiles/delete', { name });
  log(`AI 方案已删除：${name}`);
  $('#aiDlg').close();
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
    apiKey: $('#profKey').value || undefined,
  });
  out.textContent = r.ok ? '✓ ' + r.message : '✗ ' + r.message;
};

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

$('#aiGoBtn').onclick = async () => {
  const out = $('#aiOut');
  out.textContent = '';
  const btn = $('#aiGoBtn');
  btn.disabled = true; btn.textContent = '生成中…';

  const task = $('#aiTask').value;
  const isChat = task === 'chat';
  const userPrompt = $('#aiPrompt').value ||
    (['review'].includes(task) ? '' : $('#editor').value.slice(-1500));

  try {
    const res = await fetch('/api/ai', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        task,
        prompt: userPrompt,
        maxTokens: 4096,
        skillId: $('#aiSkill').value || null,
        relatedChapterIds: selectedCtxIds(),
        history: isChat ? chatHistory : null,
      }),
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
        else if (ev.type === 'progress') out.textContent += `\n[${ev.payload.inTok} in / ${ev.payload.outTok} out]`;
        else if (ev.type === 'done') {
          out.textContent = ev.payload.text;
          if (isChat) {
            chatHistory.push({ role: 'user', content: userPrompt });
            chatHistory.push({ role: 'assistant', content: ev.payload.text });
            if (chatHistory.length > 12) chatHistory = chatHistory.slice(-12);
          }
        }
        else if (ev.type === 'error') out.textContent = '出错：' + ev.payload;
      }
    }
  } catch (e) {
    out.textContent = '出错：' + e.message;
  } finally {
    btn.disabled = false; btn.textContent = '生成';
  }
};

// 结果应用：追加到正文 / 复制
$('#aiAppendBtn').onclick = () => {
  const text = $('#aiOut').textContent.trim();
  if (!text || current == null) return;
  const ed = $('#editor');
  ed.value = ed.value.replace(/\s+$/, '') + '\n\n' + text + '\n';
  dirty = true;
  $('#saveState').textContent = '未保存';
  updateWords();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSave, 1200);
  log('AI 结果已追加到本章末尾');
};
$('#aiCopyBtn').onclick = async () => {
  const t = $('#aiOut').textContent.trim();
  if (t) { await navigator.clipboard.writeText(t); log('AI 结果已复制'); }
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
      refresh();   // 正在编辑的章节由 openChapter/flushSave 机制保护
    } else if (e.kind === 'chapter-saved') {
      log(`第 ${e.payload.n} 章已保存`);
    }
  });

  conn.on('memory-changed', () => { log('AI 记忆已更新'); loadMemory(); });

  conn.on('mcpProgress', o => {
    const p = o?.params || {};
    log(p.message || JSON.stringify(o), true);
  });

  conn.start().catch(e => console.warn('实时连接失败', e));
}

// ---------- 杂项 ----------

function switchView(v) {
  $$('.tab').forEach(t => t.classList.toggle('active', t.dataset.view === v));
  ['write', 'outline', 'characters', 'literature', 'snapshots', 'settings'].forEach(n => {
    $('#view-' + n).classList.toggle('hidden', n !== v);
  });
  if (v === 'characters') loadCharacters();
  if (v === 'literature') loadLiterature();
  if (v === 'snapshots') loadSnapshots();
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
connect();
