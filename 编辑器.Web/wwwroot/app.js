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
loadAppearance().catch(e => console.error(e));
connect();
