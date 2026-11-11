// StampGen: 2D-схема (canvas) + мышь (перетаскивание/клик) + скачивание файлов + localStorage
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
window.stampGen = (() => {
    const imgCache = new Map(); // src -> Promise<HTMLImageElement>
    const svgCache = new Map(); // src -> {url, img} for data-url svg
    let last = null;            // {canvasId, json}
    let view = null;            // {canvas, j, ox, oy, scale} — для попадания мыши в оттиск

    function loadImage(src) {
        if (!imgCache.has(src)) {
            imgCache.set(src, new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => resolve(img);
                img.onerror = () => reject(new Error('img ' + src));
                img.src = src;
            }));
        }
        return imgCache.get(src);
    }

    function download(name, mime, base64) {
        const bin = atob(base64);
        const arr = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
        triggerDownload(name, mime, arr);
    }

    // v1.6: бинарный поток с сервера (STL) — без base64-удвоения
    async function downloadStream(name, mime, streamRef) {
        const buf = await streamRef.arrayBuffer();
        triggerDownload(name, mime, buf);
    }

    function triggerDownload(name, mime, data) {
        const blob = new Blob([data], { type: mime });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = name;
        document.body.appendChild(a);
        a.click();
        setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 2000);
    }

    // v1.6: растеризация SVG-экспорта в PNG (прозрачный фон) и JPG (белый фон).
    // Тот же вектор, что качается в SVG/PDF — содержимое одинаковое.
    async function exportRaster(svgText, fmt, fileName, pxPerMm) {
        const m = /width="([\d.]+)mm"\s+height="([\d.]+)mm"/.exec(svgText);
        const wmm = m ? parseFloat(m[1]) : 130, hmm = m ? parseFloat(m[2]) : 80;
        const ppm = Math.max(4, Math.min(40, pxPerMm || 20));   // 20 px/мм ≈ 508 dpi
        const W = Math.max(1, Math.round(wmm * ppm)), H = Math.max(1, Math.round(hmm * ppm));
        const img = new Image();
        const svg64 = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(svgText)));
        await new Promise((res, rej) => {
            img.onload = () => res(null);
            img.onerror = () => rej(new Error('не удалось растеризовать SVG'));
            img.src = svg64;
        });
        const cv = document.createElement('canvas');
        cv.width = W; cv.height = H;
        const ctx = cv.getContext('2d');
        if (fmt === 'jpg') { ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, W, H); }   // JPG — белый фон
        ctx.drawImage(img, 0, 0, W, H);   // PNG — без фона (прозрачно)
        const mime = fmt === 'jpg' ? 'image/jpeg' : 'image/png';
        const blob = await new Promise(r => cv.toBlob(r, mime, fmt === 'jpg' ? 0.92 : undefined));
        if (!blob) throw new Error('canvas toBlob вернул пусто');
        triggerDownload(fileName, mime, blob);
        return { w: W, h: H, bytes: blob.size };
    }

    // ================= localStorage: настройки и автосохранение =================
    function lsGet(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }
    function lsSet(k, v) { try { localStorage.setItem(k, v); } catch (e) { /* переполнение */ } }
    function lsDel(k) { try { localStorage.removeItem(k); } catch (e) { } }

    const PREFS_KEY = 'stampgen_prefs_v2';
    const AUTOSAVE_KEY = 'stampgen_autosave_v2';

    function prefsGet() { return lsGet(PREFS_KEY); }
    function prefsSet(json) { lsSet(PREFS_KEY, json); }

    function autosaveSave(b64, projectName) {
        const meta = { t: new Date().toLocaleTimeString('ru-RU'), n: projectName || '' };
        lsSet(AUTOSAVE_KEY, JSON.stringify({ d: b64, t: meta.t, n: meta.n }));
    }
    function autosaveMeta() {
        const raw = lsGet(AUTOSAVE_KEY);
        if (!raw) return null;
        try {
            const o = JSON.parse(raw);
            return JSON.stringify({ t: o.t || '', n: o.n || '' });
        } catch (e) { return null; }
    }
    function autosaveLoad() {
        const raw = lsGet(AUTOSAVE_KEY);
        if (!raw) return null;
        try { return JSON.parse(raw).d || null; } catch (e) { return null; }
    }
    function autosaveClear() { lsDel(AUTOSAVE_KEY); }

    // ================= 2D-схема =================
    async function draw2d(canvasId, json) {
        last = { canvasId, json };
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;
        const j = typeof json === 'string' ? JSON.parse(json) : json;
        const parent = canvas.parentElement;
        const dpr = window.devicePixelRatio || 1;
        const cw = parent.clientWidth, ch = parent.clientHeight;
        if (cw < 10 || ch < 10) return;
        canvas.width = Math.round(cw * dpr);
        canvas.height = Math.round(ch * dpr);
        canvas.style.width = cw + 'px';
        canvas.style.height = ch + 'px';
        const ctx = canvas.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, cw, ch);
        const margin = 18;
        const scale = Math.min((cw - margin * 2) / j.w, (ch - margin * 2 - 14) / j.d);
        const ox = (cw - j.w * scale) / 2;
        const oy = (ch - j.d * scale) / 2 + 8;
        view = { canvas, j, ox, oy, scale };
        const X = mm => ox + mm * scale;
        const Y = mm => oy + mm * scale;
        const S = mm => mm * scale;

        // фон-сетка 10 мм
        ctx.fillStyle = '#fdfdfe';
        ctx.fillRect(X(0), Y(0), S(j.w), S(j.d));
        ctx.strokeStyle = '#eef1f6';
        ctx.lineWidth = 1;
        for (let gx = 10; gx < j.w; gx += 10) {
            ctx.beginPath(); ctx.moveTo(X(gx), Y(0)); ctx.lineTo(X(gx), Y(j.d)); ctx.stroke();
        }
        for (let gy = 10; gy < j.d; gy += 10) {
            ctx.beginPath(); ctx.moveTo(X(0), Y(gy)); ctx.lineTo(X(j.w), Y(gy)); ctx.stroke();
        }

        // рабочая область (пластина минус поля по периметру)
        const [wx0, wy0, wx1, wy1] = j.work;
        ctx.fillStyle = 'rgba(232,241,255,0.55)';
        ctx.fillRect(X(wx0), Y(wy0), S(wx1 - wx0), S(wy1 - wy0));

        // пластина
        ctx.strokeStyle = '#1f2937';
        ctx.lineWidth = 2;
        ctx.strokeRect(X(0), Y(0), S(j.w), S(j.d));

        // нижняя фаска/кайма (габарит нижней грани)
        if (j.bottomOffset > 0) {
            ctx.setLineDash([5, 4]);
            ctx.strokeStyle = '#d97706';
            ctx.lineWidth = 1.2;
            ctx.strokeRect(X(j.bottomOffset), Y(j.bottomOffset), S(j.w - 2 * j.bottomOffset), S(j.d - 2 * j.bottomOffset));
            ctx.setLineDash([]);
            ctx.fillStyle = '#d97706';
            ctx.font = '10px sans-serif';
            ctx.fillText('фаска снизу ' + j.bottomOffset.toFixed(1) + ' мм', X(0), Y(j.d) + 12);
        }

        // верхняя фаска
        if (j.topChamfer) {
            ctx.setLineDash([3, 3]);
            ctx.strokeStyle = '#2563eb';
            ctx.lineWidth = 1.2;
            ctx.strokeRect(X(1), Y(1), S(j.w - 2), S(j.d - 2));
            ctx.setLineDash([]);
            ctx.fillStyle = '#2563eb';
            ctx.font = '10px sans-serif';
            ctx.fillText('кромка 1x1', X(1) + 2, Y(1) + 10);
        }

        // поля по периметру (границы рабочей области)
        ctx.setLineDash([2, 3]);
        ctx.strokeStyle = '#64748b';
        ctx.lineWidth = 1;
        ctx.strokeRect(X(wx0), Y(wy0), S(wx1 - wx0), S(wy1 - wy0));
        ctx.setLineDash([]);
        ctx.fillStyle = '#64748b';
        ctx.font = '10px sans-serif';
        // v1.3: поля единые на всю подложку (j.margins для совместимости, все значения равны)
        const m = j.margins || [5, 5, 5, 5];
        ctx.fillText('поля: ' + m[0] + ' мм (все стороны)', X(wx0) + 2, Y(wy0) - 3);

        // подпись габарита
        ctx.fillStyle = '#334155';
        ctx.font = 'bold 11px sans-serif';
        ctx.fillText(j.w + ' x ' + j.d + ' x ' + j.h + ' мм', X(0), Y(0) - 5);

        // оттиски
        // v1.5: множество выбранных (галочки/Ctrl+клик) — зелёная подсветка габарита
        const batch = new Set(j.batch || []);
        for (const inst of j.instances || []) {
            let img = null;
            if (inst.src && inst.src.startsWith('data:')) img = await loadImage(inst.src);
            else if (inst.src) { try { img = await loadImage(inst.src); } catch (e) { img = null; } }
            const cx = X(inst.ax) + S(inst.w) / 2;
            const cy = Y(inst.ay) + S(inst.h) / 2;
            ctx.save();
            ctx.translate(cx, cy);
            ctx.rotate((inst.rot || 0) * Math.PI / 180);
            ctx.scale(inst.mirror ? -1 : 1, 1);
            if (img) {
                ctx.drawImage(img, -S(inst.w) / 2, -S(inst.h) / 2, S(inst.w), S(inst.h));
            } else {
                ctx.strokeStyle = '#94a3b8';
                ctx.lineWidth = 1;
                ctx.strokeRect(-S(inst.w) / 2, -S(inst.h) / 2, S(inst.w), S(inst.h));
                ctx.beginPath();
                ctx.moveTo(-S(inst.w) / 2, -S(inst.h) / 2);
                ctx.lineTo(S(inst.w) / 2, S(inst.h) / 2);
                ctx.stroke();
            }
            ctx.restore();

            // габаритный прямоугольник (вписанный оттиск) — оси пластины
            const isSel = j.sel && inst.id === j.sel;
            const isBatch = batch.has(inst.id);   // v1.5: выбран галочкой/Ctrl+кликом
            if (isSel) {
                ctx.fillStyle = 'rgba(224,123,0,0.10)';
                ctx.fillRect(X(inst.bx), Y(inst.by), S(inst.bw), S(inst.bh));
            } else if (isBatch) {
                ctx.fillStyle = 'rgba(22,163,74,0.10)';
                ctx.fillRect(X(inst.bx), Y(inst.by), S(inst.bw), S(inst.bh));
            }
            ctx.setLineDash(isSel || isBatch ? [] : [4, 3]);
            ctx.strokeStyle = isSel ? '#e07b00' : isBatch ? '#16a34a' : 'rgba(37,99,235,0.55)';
            ctx.lineWidth = isSel || isBatch ? 2 : 1;
            ctx.strokeRect(X(inst.bx), Y(inst.by), S(inst.bw), S(inst.bh));
            ctx.setLineDash([]);

            // подпись размеров
            ctx.save();
            ctx.translate(cx, cy);
            ctx.fillStyle = isSel ? 'rgba(194,96,0,0.95)' : isBatch ? 'rgba(21,128,61,0.95)' : 'rgba(37,99,235,0.9)';
            ctx.font = '9.5px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(inst.w.toFixed(1) + 'x' + inst.h.toFixed(1), 0, S(inst.bh) / 2 + 10);
            ctx.restore();
        }
    }

    window.addEventListener('resize', () => {
        if (last) draw2d(last.canvasId, last.json);
    });

    // ================= мышь на 2D-схеме: перетаскивание и клик =================
    // Оттиск и его копии — единое целое: тянем любой экземпляр, сдвигается вся группа.
    function attach2d(canvasId, dotnetRef) {
        const canvas = document.getElementById(canvasId);
        if (!canvas || !dotnetRef) return;
        if (canvas._stampGenMouse) { canvas._stampGenMouse.dotnet = dotnetRef; return; }
        const st = { dotnet: dotnetRef, drag: null };
        canvas._stampGenMouse = st;

        const toMm = ev => {
            if (!view || view.canvas !== canvas) return null;
            const r = canvas.getBoundingClientRect();
            return { px: ev.clientX - r.left, py: ev.clientY - r.top,
                     x: (ev.clientX - r.left - view.ox) / view.scale,
                     y: (ev.clientY - r.top - view.oy) / view.scale };
        };

        const instAt = mm => {
            if (!view || !mm) return null;
            const insts = (view.j && view.j.instances) || [];
            for (let i = insts.length - 1; i >= 0; i--) {
                const inst = insts[i];
                const cx = view.ox + (inst.ax + inst.w / 2) * view.scale;
                const cy = view.oy + (inst.ay + inst.h / 2) * view.scale;
                const dx = mm.px - cx, dy = mm.py - cy;
                const a = -(inst.rot || 0) * Math.PI / 180;
                const lx = dx * Math.cos(a) - dy * Math.sin(a);
                const ly = dx * Math.sin(a) + dy * Math.cos(a);
                const hw = inst.w * view.scale / 2 + 1, hh = inst.h * view.scale / 2 + 1;
                if (Math.abs(lx) <= hw && Math.abs(ly) <= hh) return inst;
            }
            return null;
        };

        const groupShift = inst => {
            // границы сдвига позиции оригинала: вся группа (с копиями) остаётся в рабочей области
            const j = view.j;
            const [wx0, wy0, wx1, wy1] = j.work;
            let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
            for (const it of (j.instances || [])) {
                if (it.id !== inst.id) continue;
                minX = Math.min(minX, it.bx); minY = Math.min(minY, it.by);
                maxX = Math.max(maxX, it.bx + it.bw); maxY = Math.max(maxY, it.by + it.bh);
            }
            if (!isFinite(minX)) return { minPx: 0, maxPx: 0, minPy: 0, maxPy: 0 };
            return {
                minPx: inst.px + (wx0 - minX), maxPx: inst.px + (wx1 - maxX),
                minPy: inst.py + (wy0 - minY), maxPy: inst.py + (wy1 - maxY)
            };
        };

        canvas.addEventListener('pointerdown', ev => {
            if (ev.button !== 0) return;
            const mm = toMm(ev);
            const inst = instAt(mm);
            if (!inst) {
                // клик по пустому месту — снять выделение; с Ctrl — снять весь выбор (v1.5)
                try { st.dotnet.invokeMethodAsync(ev.ctrlKey || ev.metaKey ? 'CanvasCtrlClick' : 'CanvasClick', null); } catch (e) { }
                return;
            }
            const lim = groupShift(inst);
            // v1.3: сигнал начала жеста — один снимок undo на всё перетаскивание
            try { st.dotnet.invokeMethodAsync('CanvasDragBegin', inst.id); } catch (e) { }
            st.drag = {
                id: inst.id, px0: inst.px, py0: inst.py,
                startX: mm.x, startY: mm.y, moved: false,
                minPx: lim.minPx, maxPx: lim.maxPx, minPy: lim.minPy, maxPy: lim.maxPy,
                curX: inst.px, curY: inst.py, lastSent: 0,
                orig: (view.j.instances || []).filter(it => it.id === inst.id)
                    .map(it => ({ copy: it.copy, ax: it.ax, ay: it.ay, bx: it.bx, by: it.by }))
            };
            canvas.setPointerCapture(ev.pointerId);
            canvas.style.cursor = 'grabbing';
            ev.preventDefault();
        });

        canvas.addEventListener('pointermove', ev => {
            const mm = toMm(ev);
            if (!st.drag) {
                canvas.style.cursor = instAt(mm) ? 'grab' : 'default';
                return;
            }
            const d = st.drag;
            let nx = d.px0 + (mm.x - d.startX);
            let ny = d.py0 + (mm.y - d.startY);
            nx = Math.min(d.maxPx, Math.max(d.minPx, nx));
            ny = Math.min(d.maxPy, Math.max(d.minPy, ny));
            if (!d.moved && (Math.abs(mm.x - d.startX) > 2 || Math.abs(mm.y - d.startY) > 2)) d.moved = true;
            d.curX = nx; d.curY = ny;
            if (view && view.j) {
                const ddx = nx - d.px0, ddy = ny - d.py0;
                for (const it of view.j.instances) {
                    if (it.id !== d.id) continue;
                    const o = d.orig.find(q => q.copy === it.copy);
                    if (!o) continue;
                    it.ax = o.ax + ddx; it.ay = o.ay + ddy;
                    it.bx = o.bx + ddx; it.by = o.by + ddy;
                }
                draw2d(view.canvas.id, view.j);
            }
            const now = Date.now();
            if (now - d.lastSent > 150) {
                d.lastSent = now;
                try { st.dotnet.invokeMethodAsync('CanvasDragTo', d.id, nx, ny); } catch (e) { }
            }
            ev.preventDefault();
        });

        const finish = ev => {
            if (!st.drag) return;
            const d = st.drag;
            st.drag = null;
            canvas.style.cursor = 'default';
            try {
                if (d.moved) st.dotnet.invokeMethodAsync('CanvasDragTo', d.id, d.curX, d.curY);
                // v1.5: Ctrl+клик — переключить выбор (аналог галочки), обычный клик — окно свойств
                else if (ev.ctrlKey || ev.metaKey) st.dotnet.invokeMethodAsync('CanvasCtrlClick', d.id);
                else st.dotnet.invokeMethodAsync('CanvasClick', d.id);
            } catch (e) { }
            ev.preventDefault();
        };
        canvas.addEventListener('pointerup', finish);
        canvas.addEventListener('pointercancel', ev => {
            if (st.drag) { st.drag = null; canvas.style.cursor = 'default'; }
        });
    }

    // плавная прокрутка к карточке оттиска (после клика на схеме)
    function scrollStampIntoView(id) {
        const el = document.getElementById('card-' + id);
        if (el && el.scrollIntoView) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    // ================= v1.3: колесо мыши на слайдерах (исправлено в v1.5) =================
    // v1.5: колесо меняет значение ТОЛЬКО когда слайдер в ФОКУСЕ (по нему кликнули).
    // Раньше колесо над любым слайдером менял его — при прокрутке правого списка курсор
    // задевал слайдеры и портил параметры. Теперь прокрутка списка работает как надо:
    // слайдер без фокуса просто прокручивает страницу, ничего не меняя.
    function attachWheel() {
        document.addEventListener('wheel', ev => {
            const t = ev.target;
            if (!t || !(t instanceof HTMLInputElement) || t.type !== 'range') return;
            if (document.activeElement !== t) return;   // v1.5: только сфокусированный слайдер
            ev.preventDefault();
            const min = parseFloat(t.min || '0'), max = parseFloat(t.max || '100');
            let step = parseFloat(t.step || '1');
            if (!isFinite(step) || step <= 0) step = 1;
            if (step < 0.05) step = 0.1;            // мелкие слайдеры размеров — 0.1 мм за щелчок
            const dir = ev.deltaY < 0 ? 1 : -1;
            let v = parseFloat(t.value || '0');
            v = Math.min(max, Math.max(min, v + dir * step));
            if (Number.isFinite(v)) {
                t.value = v;
                t.dispatchEvent(new Event('input', { bubbles: true }));
                t.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }, { passive: false });
    }

    // ================= v1.3: горячие клавиши Undo/Redo (+ Delete в v1.5) =================
    // Ctrl+Z — отменить, Ctrl+Y или Ctrl+Shift+Z — вернуть, Delete — удалить выбранные оттиски.
    // В полях ввода не перехватываем: там работает родная отмена текста.
    let hotkeyRef = null;
    function attachHotkeys(dotnetRef) {
        hotkeyRef = dotnetRef;
        if (attachHotkeys._installed) return;
        attachHotkeys._installed = true;
        document.addEventListener('keydown', ev => {
            if (!hotkeyRef) return;
            const tag = (ev.target && ev.target.tagName || '').toLowerCase();
            if (tag === 'input' && ev.target.type !== 'range' && ev.target.type !== 'checkbox') return;
            if (tag === 'textarea' || tag === 'select') return;
            const k = (ev.key || '').toLowerCase();
            try {
                if (ev.ctrlKey || ev.metaKey) {
                    if (k === 'z' && !ev.shiftKey) { ev.preventDefault(); hotkeyRef.invokeMethodAsync('UndoHotkey'); }
                    else if (k === 'y' || (k === 'z' && ev.shiftKey)) { ev.preventDefault(); hotkeyRef.invokeMethodAsync('RedoHotkey'); }
                }
                // v1.5: Delete — удалить выбранные оттиски (пачку или активный)
                else if (ev.key === 'Delete') {
                    ev.preventDefault();
                    hotkeyRef.invokeMethodAsync('DeleteHotkey');
                }
            } catch (e) { }
        });
    }

    // ================= v1.7: скрытые file-input (импорт библиотек zip) =================
    // Кликаем невидимый <input type=file> по кнопке программы; после чтения сбрасываем
    // значение — выбор ТОГО ЖЕ файла повторно тоже срабатывает.
    function clickFile(inputId) {
        const el = document.getElementById(inputId);
        if (el && el.click) el.click();
    }
    function resetFile(inputId) {
        const el = document.getElementById(inputId);
        if (el) el.value = '';
    }

    // для тестов: текущая сцена 2D (объект)
    function lastScene() {
        if (!last) return null;
        try { return typeof last.json === 'string' ? JSON.parse(last.json) : last.json; }
        catch (e) { return null; }
    }

    attachWheel();   // колесо на слайдерах — сразу при загрузке

    return {
        draw2d, download, downloadStream, exportRaster, attach2d, attachHotkeys, scrollStampIntoView, lastScene,
        clickFile, resetFile,
        prefsGet, prefsSet,
        autosaveSave, autosaveMeta, autosaveLoad, autosaveClear
    };
})();
