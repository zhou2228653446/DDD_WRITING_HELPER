// fx.js —— 界面特效层（只管「看起来怎么动」，不碰业务）
//
// 三块：
//   ① 生成态：顶栏流光条 → 输出区骨架屏 → 打字机光标 + 新字淡入 → 完成闪光。
//      这是全站最需要反馈的地方：AI 生成短则几秒、长则一两分钟，
//      屏幕上一点动静都没有，用户只能猜它是不是死了。
//   ② Toast：右下角滑入、可点掉，替代一部分 alert（alert 会打断写作节奏）。
//   ③ 通用润色：按钮水波、列表错落入场、状态数字闪一下。
//
// 时长 / 曲线对齐桌面端 Motion.cs（淡入 240 / 弹入 320，缓动接近其 Tween）。
// 所有动画在「系统减弱动效」下由 CSS 统一压到 0，这里只额外跳过 JS 侧的重启。

window.fx = (function () {
  const REDUCED = !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);
  const $ = s => document.querySelector(s);

  /* ══════════ ① 顶部流光进度条 ══════════ */
  // 不确定进度（不知道还要多久），所以是一条来回扫的光，不是一根涨的条。
  // mode: 'ai'（默认，暖色）/ 'mcp'（冷色，agent 在跑）
  // 两条同屏时也要分得清：是"我在生成"还是"它在写"。
  function top(on, mode) {
    const el = $('#fxTop');
    if (!el) return;
    el.classList.toggle('on', !!on);
    if (on) el.classList.toggle('mcp', mode === 'mcp');
  }

  /* ══════════ ② Toast ══════════ */
  const ICON = { ok: '✓', err: '✕', warn: '!', info: '·' };

  function toast(msg, kind, ms) {
    const box = $('#fxToasts');
    if (!box || !msg) return null;
    kind = kind || 'info';
    const life = ms || 2800;

    const t = document.createElement('div');
    t.className = 'toast ' + kind;
    const ico = document.createElement('i');
    ico.className = 'toast-ico';
    ico.textContent = ICON[kind] || ICON.info;
    const span = document.createElement('span');
    span.className = 'toast-msg';
    span.textContent = msg;                    // textContent：绝不拼 HTML
    const bar = document.createElement('b');
    bar.className = 'toast-bar';
    bar.style.animationDuration = life + 'ms';
    t.append(ico, span, bar);

    t.onclick = () => dismiss(t);
    box.appendChild(t);
    t.__timer = setTimeout(() => dismiss(t), life);

    // 最多同时 4 条：批量操作时（比如连点导出）不至于糊满一角。
    // ⚠ 只能数还没在退场的那几条，正在退场的还在 DOM 里 —— 否则 while 死循环。
    const live = [...box.children].filter(c => !c.__out);
    if (live.length > 4) live.slice(0, live.length - 4).forEach(dismiss);
    return t;
  }

  function dismiss(t) {
    if (!t || t.__out) return;
    t.__out = true;
    clearTimeout(t.__timer);
    t.classList.add('out');
    setTimeout(() => t.remove(), 260);
  }

  /* ══════════ ③ 流式输出 ══════════ */
  // 结构：<i class="ai-scan"> 扫光线 + <span class="ai-txt"> 已落定的字
  //       + <span class="ai-fresh"> 这一帧新到的字（淡入）+ <span class="ai-caret"> 光标
  // 为什么拆成两段：整段重设 textContent 的话，新到的字是"跳"出来的；
  // 只给增量套一层淡入动画，观感就是字一个个洇出来。
  // ⚠ 服务端每个 text 事件给的是**累计全文**（OnStreamText 传的是 state.Text），
  //    所以增量 = full.slice(shown.length)；若不是前缀关系（换了一轮）就整段重设。
  const streams = new WeakMap();

  function ensure(el) {
    if (!el) return null;
    let s = streams.get(el);
    if (s && s.txt.parentNode === el) return s;
    el.textContent = '';
    const scan = document.createElement('i');
    scan.className = 'ai-scan';
    const txt = document.createElement('span');
    txt.className = 'ai-txt';
    const fresh = document.createElement('span');
    fresh.className = 'ai-fresh';
    const caret = document.createElement('span');
    caret.className = 'ai-caret';
    el.append(scan, txt, fresh, caret);
    s = { txt, fresh, caret, shown: '', last: 0, skel: null, notes: [] };
    streams.set(el, s);
    return s;
  }

  function begin(el, label) {
    const s = ensure(el);
    if (!s) return;
    s.shown = '';
    s.txt.textContent = '';
    s.fresh.textContent = '';
    s.notes.forEach(n => n.remove());
    s.notes = [];
    el.classList.remove('err', 'done');
    el.classList.add('streaming');

    // 第一个字到达之前先摆骨架：空框 + 不动的光标看起来就是"卡住了"
    const sk = document.createElement('div');
    sk.className = 'ai-skel';
    const lb = document.createElement('div');
    lb.className = 'ai-skel-label';
    const dots = document.createElement('span');
    dots.className = 'dots';
    dots.innerHTML = '<i></i><i></i><i></i>';
    lb.append(dots, document.createTextNode(label || 'AI 正在构思'));
    sk.appendChild(lb);
    for (let i = 0; i < 3; i++) {
      const b = document.createElement('i');
      b.style.width = (88 - i * 21) + '%';
      sk.appendChild(b);
    }
    el.insertBefore(sk, s.txt);
    s.skel = sk;
    top(true);
  }

  function stream(el, full) {
    const s = ensure(el);
    if (!s) return null;
    full = full || '';
    if (s.skel) { s.skel.remove(); s.skel = null; }
    el.classList.add('streaming');

    // 只在用户本来就贴着底部时才跟着滚：他在回看前面就不该被拽走
    const wasBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 56;

    let delta = '';
    if (full.length > s.shown.length && full.startsWith(s.shown)) delta = full.slice(s.shown.length);
    s.shown = full;

    if (delta) {
      s.txt.textContent = full.slice(0, full.length - delta.length);
      s.fresh.textContent = delta;
      // 重启动画要强制回流，块与块间隔太短时（每秒几十块）不划算，节流到 ~80ms
      const now = performance.now();
      if (!REDUCED && now - s.last > 80) {
        s.fresh.style.animation = 'none';
        void s.fresh.offsetWidth;
        s.fresh.style.animation = '';
        s.last = now;
      }
    } else {
      s.txt.textContent = full;
      s.fresh.textContent = '';
    }
    if (wasBottom) el.scrollTop = el.scrollHeight;
    return s;
  }

  // 服务端的过程提示（比如"聊天记忆已压缩"）：单独一行挂在正文上面
  function notice(el, msg) {
    const s = ensure(el);
    if (!s) return;
    const n = document.createElement('div');
    n.className = 'ai-note';
    n.textContent = '[' + msg + ']';
    el.insertBefore(n, s.txt);
    s.notes.push(n);
  }

  function end(el, ok) {
    if (!el) return;
    const s = streams.get(el);
    if (s) {
      if (s.skel) { s.skel.remove(); s.skel = null; }
      // ★ 新字段里的尾巴必须先并回正文再清空——txt 只存到"上一帧之前"，
      //   直接清 fresh 会把最后一次到的字弄丢（自检页抓出来的）。
      if (s.fresh.textContent) {
        s.txt.textContent += s.fresh.textContent;
        s.fresh.textContent = '';
      }
      s.shown = s.txt.textContent;
    }
    el.classList.remove('streaming');
    if (ok === false) {
      el.classList.add('err');
    } else {
      el.classList.add('done');
      setTimeout(() => el.classList.remove('done'), 1000);
    }
    top(false);
  }

  /* ══════════ 流式计数器（字数 / 用时） ══════════ */
  let meterTimer = null;
  function meter(text, keepMs) {
    const m = $('#aiMeter');
    if (!m) return;
    if (!text) { m.classList.add('hidden'); return; }
    m.textContent = text;
    m.classList.remove('hidden');
    clearTimeout(meterTimer);
    if (keepMs) meterTimer = setTimeout(() => m.classList.add('hidden'), keepMs);
  }

  /* ══════════ ④ 通用润色 ══════════ */

  // 列表错落入场：容器重画后调一次，子元素按序浮上来（最多 14 个，再多就一起出）
  function stagger(container, max) {
    if (!container || REDUCED) return;
    const kids = container.children;
    const n = Math.min(kids.length, max || 14);
    for (let i = 0; i < n; i++) {
      kids[i].style.setProperty('--i', i);
      kids[i].classList.add('fx-in');
    }
  }

  // 状态文字闪一下（保存成功、字数变化这类"值变了"的时刻）
  function flash(el) {
    if (!el || REDUCED) return;
    el.classList.remove('fx-flash');
    void el.offsetWidth;
    el.classList.add('fx-flash');
    setTimeout(() => el.classList.remove('fx-flash'), 700);
  }

  // 按钮水波：按下处扩散一圈。用 ::after 而不是插元素，
  // 免得给每个按钮都挂一个 span；坐标由 --rx/--ry 传进去。
  // .tab 的 ::after 已经被选中指示条占了，排除掉。
  document.addEventListener('pointerdown', e => {
    if (REDUCED) return;
    const b = e.target.closest && e.target.closest('button');
    if (!b || b.classList.contains('tab')) return;
    const r = b.getBoundingClientRect();
    b.style.setProperty('--rx', (e.clientX - r.left) + 'px');
    b.style.setProperty('--ry', (e.clientY - r.top) + 'px');
    b.classList.remove('rippling');
    void b.offsetWidth;
    b.classList.add('rippling');
    b.addEventListener('animationend', () => b.classList.remove('rippling'), { once: true });
  }, true);

  /* ══════════ ⑤ v12：现代感动效 ══════════ */

  // Tab 滑行指示条：把 #tabGlider 平移到当前激活标签下面。
  // 位置用 offsetLeft/offsetWidth（相对 .tabs 定位容器），横滚时一起走。
  function glider() {
    const t = document.querySelector('.tabs .tab.active');
    const g = $('#tabGlider');
    if (!t || !g) return;
    // 祖先 display:none（登录门盖着）时 offset 全是 0，此时不定位——
    // 等门揭开时由下面的 observer 重算
    if (!t.offsetWidth) { g.classList.remove('on'); return; }
    g.style.left = t.offsetLeft + 'px';
    g.style.width = t.offsetWidth + 'px';
    g.classList.add('on');
  }
  window.addEventListener('resize', () => glider());
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => glider());
  // 登录门揭开（body.locked 移除）的那一帧 tab 才有尺寸，自动补一次定位
  new MutationObserver(() => {
    if (!document.body.classList.contains('locked')) glider();
  }).observe(document.body, { attributes: true, attributeFilter: ['class'] });

  // 值变化的弹跳（字数这类数字变了"跳"一下）。同一元素 2.5s 冷却：
  // 输入时字数每个键都在变，不冷却就是一直在弹，反而烦人。
  const popCool = new WeakMap();
  function pop(el) {
    if (!el || REDUCED) return;
    const now = performance.now();
    if (now - (popCool.get(el) || 0) < 2500) return;
    popCool.set(el, now);
    el.classList.remove('fx-pop');
    void el.offsetWidth;
    el.classList.add('fx-pop');
    setTimeout(() => el.classList.remove('fx-pop'), 520);
  }

  // 元素「弹出来」：配置片段这类"点了才冒出来的东西"，
  // 直接显示会让人没注意到它出现（尤其是页面没滚动的时候）。
  function reveal(el) {
    if (!el || REDUCED) return;
    el.classList.remove('fx-reveal');
    void el.offsetWidth;
    el.classList.add('fx-reveal');
  }

  // spotlight：卡片鼠标柔光。pointermove 委托 + rAF 节流，坐标写进
  // --mx/--my，径向渐变跟着鼠标走（显隐交给 CSS :hover，JS 不管离开）。
  let spX = 0, spY = 0, spT = null, spPending = false;
  document.addEventListener('pointermove', e => {
    if (REDUCED) return;
    const t = e.target.closest && e.target.closest('.card,.book-item,.agent-item');
    if (!t) return;
    spX = e.clientX; spY = e.clientY; spT = t;
    if (spPending) return;
    spPending = true;
    requestAnimationFrame(() => {
      spPending = false;
      if (!spT || !spT.isConnected) return;
      const r = spT.getBoundingClientRect();
      spT.style.setProperty('--mx', (spX - r.left) + 'px');
      spT.style.setProperty('--my', (spY - r.top) + 'px');
    });
  }, { passive: true });

  return { top, toast, begin, stream, notice, end, meter, stagger, flash, pop, glider, reveal, REDUCED };
})();
