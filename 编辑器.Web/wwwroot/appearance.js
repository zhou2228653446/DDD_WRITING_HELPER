/* ══════════════════════════════════════════════════════════════════════════
   外观引擎：配色 × 材质，算法逐条对照桌面版 Services/ThemeTokens.cs。

   与桌面版的对应关系：
     · 配色 → CSS 变量（--bg / --panel / --editor-bg / --ink / --line / --accent…）
     · 材质 → 四层：① 颗粒平铺 tile（256px，只放随机噪声）
                     ② 全窗柔光（左上→右下，**固定尺寸不重复**）
                     ③ 液态玻璃的斜向高光带（同上一道，只画一条）
                     ④ 边缘光（inset box-shadow，玻璃感最强的识别特征）
     · 透光 → 表面色改 rgba + backdrop blur（浏览器有真背景模糊，比 WPF 那套
              "视觉模拟"更接近真玻璃）

   ⚠ 两条铁律（桌面版踩过坑，这里同样适用）：
     1. 规律性渐变**绝不能**进平铺 tile——每个 tile 各画一道渐变，铺开就是
        满屏割裂色块。所以颗粒进 tile，柔光/高光一律 fixed 全窗。
     2. 材质之间的数值是刻意拉开的（颗粒 0→0.44、柔光 0→0.62、透光 0→0.45、
        边缘光 0→0.60），不是随手填的；差别太小用户会觉得"换了跟没换一样"。
   ══════════════════════════════════════════════════════════════════════════ */

let appearance = null;        // 服务器上生效的外观
let appearanceDraft = null;   // 外观对话框里的草稿（实时预览用，点确定才落盘）
const grainTiles = {};        // "kind|factor" → dataURI，颗粒只生成一次

// 颗粒形态（与桌面版 GrainFor 的三张位图对应）：
// 细砂（纸）/ 纤维团 2×2（绒）/ 微尘（玻璃类）
const GRAIN_SPEC = {
  paper:  { max: 0x61, block: 1 },
  velvet: { max: 0x54, block: 2 },
  frosted:{ max: 0x40, block: 1 },
  glass:  { max: 0x40, block: 1 },
  liquid: { max: 0x40, block: 1 },
  solid:  null,
};

/**
 * 256px 噪声 tile：逐格随机取 0 或 255（黑白双色，浅底显暗点、深底显亮点，
 * 天然自适应），alpha 随机到 max × factor。
 *
 * 强度为什么烧进 tile 的 alpha，而不是用 opacity 盖一层？
 * 因为桌面版是把「底色 + 颗粒 + 柔光」合成进**同一个表面画刷**的（bitmap 的
 * alpha × 图层不透明度）。网页端要等价，就得让每个表面自己的 background-image
 * 带上颗粒——否则颗粒只能做成一整层盖在内容之上或之下：盖在上面会糊住文字，
 * 盖在下面则被不透明面板挡住，等于只有空隙里看得见（第一版就是这么错的）。
 *
 * role 系数按区域给：正文区略重（纸面感），菜单/状态栏更轻（噪点别干扰文字）。
 */
function grainTileFor(kind, factor) {
  const spec = GRAIN_SPEC[kind];
  if (!spec || factor <= 0) return 'none';
  const f = clamp01(factor);
  const key = `${kind}|${f.toFixed(3)}`;
  if (grainTiles[key]) return grainTiles[key];

  const max = Math.max(1, Math.round(spec.max * f));
  const S = 256;
  const cv = document.createElement('canvas');
  cv.width = cv.height = S;
  const g = cv.getContext('2d');
  const img = g.createImageData(S, S);
  const px = img.data;

  for (let y = 0; y < S; y += spec.block) {
    for (let x = 0; x < S; x += spec.block) {
      const v = Math.random() < 0.5 ? 255 : 0;
      const a = Math.floor(Math.random() * max);
      for (let dy = 0; dy < spec.block && y + dy < S; dy++) {
        for (let dx = 0; dx < spec.block && x + dx < S; dx++) {
          const o = ((y + dy) * S + (x + dx)) * 4;
          px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = a;
        }
      }
    }
  }
  g.putImageData(img, 0, 0);
  grainTiles[key] = `url(${cv.toDataURL('image/png')})`;
  return grainTiles[key];
}

/** 全窗/表面柔光：左上→右下，62% 处收干净（与桌面版 BuildSheenBrush 同参数）。 */
const sheenGradient = alpha => alpha > 0
  ? `linear-gradient(135deg, rgba(255,255,255,${alpha.toFixed(3)}), rgba(255,255,255,0) 62%)`
  : 'none';

/** 液态玻璃的斜向流动高光带：只画一条，峰值分深浅色（浅底上白高光本来就显眼）。 */
function glossGradient(mat, k, dark) {
  if (!(mat.highGloss && mat.sheen > 0)) return 'none';
  const peak = clamp01(mat.sheen * (dark ? 0.62 : 0.40) * k).toFixed(3);
  return `linear-gradient(135deg, rgba(255,255,255,0) 0%, rgba(255,255,255,${peak}) 20%, rgba(255,255,255,0) 40%)`;
}

// ---------------- 颜色工具 ----------------

const clamp01 = v => v < 0 ? 0 : v > 1 ? 1 : v;
const clamp = (v, lo, hi) => v < lo ? lo : v > hi ? hi : v;

function hexRgb(hex) {
  const h = (hex || '#000000').replace('#', '');
  const s = h.length === 3 ? h.split('').map(c => c + c).join('') : h;
  return {
    r: parseInt(s.slice(0, 2), 16),
    g: parseInt(s.slice(2, 4), 16),
    b: parseInt(s.slice(4, 6), 16),
  };
}
function mixRgb(a, b, t) {
  return {
    r: Math.round(a.r + (b.r - a.r) * t),
    g: Math.round(a.g + (b.g - a.g) * t),
    b: Math.round(a.b + (b.b - a.b) * t),
  };
}
const rgba = (c, a) => `rgba(${c.r},${c.g},${c.b},${a})`;

// ---------------- 合成 ----------------

/**
 * 一个表面（面板/编辑器/菜单…）的底色。
 * role 与 throughFactor 抄自桌面版 Surface() 的调用点，观感比例是量出来的：
 * 正文区略重（纸面感），菜单/状态栏更轻（噪点不干扰文字）。
 * 编辑器 throughFactor 0.3：正文长时间盯，透光会伤可读性。
 *
 * bgAlpha 非空 = 背景图模式，走桌面版另一条分支
 * （0.88 - 透光×强度×1.2，夹在 0.55~1.0）：背景图模式下玻璃要透得更多，
 * 透出来的就是真实画面——这才是"玻璃"该有的样子。
 */
/** 与 surfaceColor 同一套调色，但**不带 alpha** —— 给菜单这类必须不透明的表面用。 */
function solidColor(hex, mat, dark = false) {
  let c = hexRgb(hex);
  if (mat.stretched && !dark) c = mixRgb(c, { r: 0xC4, g: 0xD0, b: 0xDC }, 0.14);
  return `rgb(${c.r}, ${c.g}, ${c.b})`;
}

function surfaceColor(hex, mat, k, role, throughFactor = 1.0, dark = false, bgAlpha = null) {
  let c = hexRgb(hex);
  // 玻璃的"冷"调：浅色主题下白色柔光打在浅底上几乎看不见，
  // 不把底色往冷灰拉一点，玻璃和纸纹会糊成一片（桌面版同样处理）。
  if (mat.stretched && !dark) c = mixRgb(c, { r: 0xC4, g: 0xD0, b: 0xDC }, 0.14);
  const alpha = bgAlpha != null
    ? clamp(bgAlpha, 0.55, 1.0)
    : 1 - clamp01(mat.translucency * k * throughFactor);
  return rgba(c, alpha);
}

/**
 * 把一套「配色 + 材质 + 强度」翻译成一整批 CSS 变量，写进 <style>。
 * @param {object} state { scheme, material, intensity, bgUrl }
 */
function applyAppearance(state) {
  if (!appearance || !state) return;
  const scheme = appearance.schemes.find(s => s.name === state.scheme) || appearance.schemes[0];
  const mat = appearance.materials.find(m => m.name === state.material) || appearance.materials[0];
  const k = clamp(state.intensity ?? 1, 0, 2);
  const dark = !!scheme.isDark;

  // 背景图模式：面板改用桌面版那条"透得更多"的公式（编辑器除外——正文要看得清）
  const panelAlpha = state.bgUrl ? 0.88 - mat.translucency * k * 1.2 : null;

  // ① 表面色（角色系数与桌面版 Surface() 调用点一致）
  const vars = {
    '--bg':        surfaceColor(scheme.windowBg,    mat, k, 1.00, 1.0, dark),
    '--panel':     surfaceColor(scheme.panelBg,     mat, k, 0.95, 1.0, dark, panelAlpha),
    '--editor-bg': surfaceColor(scheme.editorBg,    mat, k, 1.08, 0.3, dark),
    '--menu-bg':   surfaceColor(scheme.menuBg,      mat, k, 0.85, 1.0, dark, panelAlpha),
    // ★ 菜单一定要实色：下拉弹出来是盖在正文上的，半透明会让背后的字透出来，
    //   菜单项根本读不清（材料再好看也不能牺牲可读性）。材质只影响它的色调，
    //   不参与透明度——桌面端菜单也是不透明的。
    '--menu-solid': solidColor(scheme.menuBg, mat, dark),
    '--ink':       scheme.textColor,
    '--muted':     scheme.textMuted,
    '--accent':    scheme.accent,
    '--danger':    scheme.danger,

    // ② 颗粒：烧进每个表面自己的 background-image（每个区域一个强度档）
    '--grain-window': grainTileFor(mat.kind, mat.texture * k * 1.00),
    '--grain-panel':  grainTileFor(mat.kind, mat.texture * k * 0.95),
    '--grain-editor': grainTileFor(mat.kind, mat.texture * k * 1.08),
    '--grain-menu':   grainTileFor(mat.kind, mat.texture * k * 0.85),
    '--grain-subtle': grainTileFor(mat.kind, mat.texture * k * 0.70),

    // ③ 柔光：拉伸材质才进表面（平铺材质一旦放进去就是满屏割裂斜纹——桌面版的坑）
    '--sheen-surf': sheenGradient(mat.stretched ? clamp01(mat.sheen * k) : 0),
    // 整窗再加一道极淡的（拉伸材质已有层内柔光，压到 0.22 免得双层光）
    '--sheen-global': sheenGradient(clamp01(mat.sheen * (mat.stretched ? 0.22 : 0.55) * k)),

    // ④ 液态玻璃的斜向高光带
    '--gloss': glossGradient(mat, k, dark),

    // ⑤ 边缘光：玻璃边缘折射出的亮线。毛玻璃刻意不给——磨砂面的边缘是漫射的
    '--edge': mat.edgeLight > 0 && mat.stretched
      ? `inset 0 0 0 ${mat.kind === 'liquid' ? 4 : 2.5}px rgba(255,255,255,${clamp01(mat.edgeLight * k).toFixed(3)})`
      : 'none',

    // ⑥ 透光 → 真背景模糊（浏览器比 WPF 那套模拟更接近真玻璃）
    '--glass-blur': (clamp01(mat.translucency * k) * 26).toFixed(1) + 'px',

    // ⑦ 边框线：玻璃类要更淡，实色边框会立刻把"玻璃"说破
    '--line': rgba(hexRgb(scheme.borderColor), borderAlpha(mat)),
    '--splitter': scheme.splitterBg,
  };

  // 背景图铺在 body 上（cover + fixed），面板靠材质的透光率透出它——
  // 与桌面版一致：纸纹不透（背景图只在缝隙里看得见），玻璃类才真正透出来
  vars['--bg-img'] = state.bgUrl ? `url("${state.bgUrl}")` : 'none';

  const css = `:root{${Object.entries(vars).filter(([, v]) => v !== '').map(([k2, v]) => `${k2}:${v}`).join(';')}}`;
  let tag = document.getElementById('appearanceStyle');
  if (!tag) {
    tag = document.createElement('style');
    tag.id = 'appearanceStyle';
    document.head.appendChild(tag);
  }
  tag.textContent = css;

  document.documentElement.dataset.material = mat.name;
  document.documentElement.dataset.dark = dark ? '1' : '0';
  document.title = `写作 · 网页版`;
}

function borderAlpha(mat) {
  if (mat.kind === 'glass' || mat.kind === 'liquid') return 0.69;   // 0xB0
  if (mat.kind === 'frosted') return 0.82;                          // 0xD0
  return 1;
}

/** 单个材质的预览（外观对话框里的缩略块）：拿当前配色的面板色走同一套合成。
 *  与真实界面同一份算法，所以预览所见即所得（对应桌面版 PreviewSurface）。 */
function materialPreviewVars(mat, scheme, k = 1) {
  const dark = !!scheme.isDark;
  return {
    '--pv-bg': surfaceColor(scheme.panelBg, mat, k, 1.0, 1.0, dark),
    '--pv-grain': grainTileFor(mat.kind, mat.texture * k),
    '--pv-sheen': sheenGradient(mat.stretched ? clamp01(mat.sheen * k) : 0),
    '--pv-gloss': glossGradient(mat, k, dark),
    '--pv-edge': mat.edgeLight > 0 && mat.stretched
      ? `inset 0 0 0 ${mat.kind === 'liquid' ? 4 : 2.5}px rgba(255,255,255,${clamp01(mat.edgeLight * k).toFixed(3)})`
      : 'none',
  };
}
const styleStr = obj => Object.entries(obj).map(([k, v]) => `${k}:${v}`).join(';');

// ---------------- 与服务端同步 ----------------

async function loadAppearance() {
  // 先用本地缓存画一帧，避免"打开还是默认色、闪一下才变"（服务器仍是权威）
  try {
    const cached = JSON.parse(localStorage.getItem('appearance') || 'null');
    if (cached) appearanceDraft = cached;
  } catch { /* 缓存坏了就当没有 */ }

  appearance = await api('/api/appearance');
  if (!appearanceDraft) appearanceDraft = pick(appearance);
  // 服务器是权威：以它为准（本地缓存只用于首帧）
  appearanceDraft = pick(appearance);
  applyAppearance(appearanceDraft);
  localStorage.setItem('appearance', JSON.stringify(appearanceDraft));
  fillSchemeSelect();
}

const pick = a => ({ scheme: a.scheme, material: a.material, intensity: a.intensity, bgUrl: a.bgUrl });

function fillSchemeSelect() {
  const sel = document.getElementById('themeSel');
  if (!sel || !appearance) return;
  sel.innerHTML = '';
  appearance.schemes.forEach(s => {
    const o = document.createElement('option');
    o.value = s.name; o.textContent = s.displayName;
    sel.appendChild(o);
  });
  sel.value = appearance.scheme;
}

// ---------------- 外观设置对话框（实时预览 / 取消还原，与桌面版同款交互） ----------------

function openAppearanceDlg() {
  if (!appearance) return;
  appearanceDraft = pick(appearance);   // 每次打开都从"生效值"起，草稿不改动已保存的
  renderAppearanceDlg();
  applyAppearance(appearanceDraft);     // 实时预览
  document.getElementById('appearanceDlg').showModal();
}

function renderAppearanceDlg() {
  const d = appearance, draft = appearanceDraft;
  const scheme = d.schemes.find(s => s.name === draft.scheme) || d.schemes[0];

  // 配色：每套给一条色带（窗口底 / 面板 / 编辑区 / 强调 / 正文），一眼看出气质
  const sBox = document.getElementById('apSchemes');
  sBox.innerHTML = '';
  d.schemes.forEach(s => {
    const el = document.createElement('label');
    el.className = 'ap-item' + (s.name === draft.scheme ? ' on' : '');
    el.innerHTML =
      `<input type="radio" name="apScheme" value="${s.name}" ${s.name === draft.scheme ? 'checked' : ''}>` +
      `<span class="ap-name">${s.displayName}${s.isDark ? ' · 深' : ''}</span>` +
      `<span class="ap-chips">` +
        `<i style="background:${s.windowBg}"></i><i style="background:${s.panelBg}"></i>` +
        `<i style="background:${s.editorBg}"></i><i style="background:${s.accent}"></i>` +
        `<i style="background:${s.textColor}"></i>` +
      `</span>` +
      `<span class="ap-desc">${s.description}</span>`;
    el.querySelector('input').onchange = () => { draft.scheme = s.name; renderAppearanceDlg(); applyAppearance(draft); };
    sBox.appendChild(el);
  });

  // 材质：缩略块走同一套合成算法 → 预览所见即所得
  const mBox = document.getElementById('apMaterials');
  mBox.innerHTML = '';
  d.materials.forEach(m => {
    const el = document.createElement('label');
    el.className = 'ap-item' + (m.name === draft.material ? ' on' : '');
    const pv = document.createElement('span');
    pv.className = 'mat-preview';
    pv.setAttribute('style', styleStr(materialPreviewVars(m, scheme)));
    el.innerHTML =
      `<input type="radio" name="apMaterial" value="${m.name}" ${m.name === draft.material ? 'checked' : ''}>` +
      `<span class="ap-name">${m.displayName}</span>`;
    el.appendChild(pv);
    const desc = document.createElement('span');
    desc.className = 'ap-desc';
    desc.textContent = m.description;
    el.appendChild(desc);
    el.querySelector('input').onchange = () => { draft.material = m.name; renderAppearanceDlg(); applyAppearance(draft); };
    mBox.appendChild(el);
  });

  const sl = document.getElementById('apIntensity');
  sl.value = draft.intensity ?? 1;
  document.getElementById('apIntensityText').textContent = Number(sl.value).toFixed(1);

  const bgOn = !!draft.bgUrl || !!d.bgUrl;
  document.getElementById('apBgState').textContent = d.bgUrl ? '已设置背景图' : '未设置背景图';
  document.getElementById('apBgPreview').style.backgroundImage = d.bgUrl ? `url("${d.bgUrl}")` : 'none';
  document.getElementById('apBgRemove').disabled = !d.bgUrl;
}

// 强度滑块：拖动即预览（不落盘）
document.getElementById('apIntensity').addEventListener('input', e => {
  appearanceDraft.intensity = Number(e.target.value);
  document.getElementById('apIntensityText').textContent = appearanceDraft.intensity.toFixed(1);
  applyAppearance(appearanceDraft);
});

// 上传背景图：立即写服务器（图片不参与"取消还原"，界面上有说明）
document.getElementById('apBgFile').addEventListener('change', async e => {
  const f = e.target.files?.[0];
  if (!f) return;
  const ext = (f.name.split('.').pop() || 'png').toLowerCase();
  const state = document.getElementById('apBgState');
  state.textContent = '上传中…';
  try {
    const res = await fetch('/api/appearance/bg?ext=' + encodeURIComponent(ext), {
      method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body: f,
    });
    const r = await res.json();
    if (r.error) { state.textContent = r.error; return; }
    appearance.bgUrl = r.bgUrl;
    appearanceDraft.bgUrl = r.bgUrl;
    applyAppearance(appearanceDraft);
    renderAppearanceDlg();
    log('背景图已上传（透光材质下效果最明显）');
  } catch (err) { state.textContent = '上传失败：' + err.message; }
  e.target.value = '';
});

document.getElementById('apBgRemove').onclick = async () => {
  await fetch('/api/appearance/bg', { method: 'DELETE' });
  appearance.bgUrl = null;
  appearanceDraft.bgUrl = null;
  applyAppearance(appearanceDraft);
  renderAppearanceDlg();
  log('背景图已移除');
};

document.getElementById('apOk').onclick = async () => {
  const r = await post('/api/appearance', {
    scheme: appearanceDraft.scheme, material: appearanceDraft.material, intensity: appearanceDraft.intensity,
  });
  appearance.scheme = r.scheme; appearance.material = r.material; appearance.intensity = r.intensity;
  appearanceDraft = pick(appearance);
  applyAppearance(appearanceDraft);
  localStorage.setItem('appearance', JSON.stringify(appearanceDraft));
  fillSchemeSelect();
  document.getElementById('appearanceDlg').close();
  log(`外观已保存：${schemeName(r.scheme)} + ${materialName(r.material)}（强度 ${r.intensity}）`);
};

document.getElementById('apCancel').onclick = () => {
  appearanceDraft = pick(appearance);   // 放弃草稿，回到生效值
  applyAppearance(appearanceDraft);
  document.getElementById('appearanceDlg').close();
};

const schemeName = id => appearance.schemes.find(s => s.name === id)?.displayName || id;
const materialName = id => appearance.materials.find(m => m.name === id)?.displayName || id;
