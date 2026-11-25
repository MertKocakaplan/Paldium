// The map of the Map page (T138, screens/map.md "Harita tuvali"). The Razor page renders the side panel and the static controls; this script
// owns the canvas: zoom (x1..x16), pan, the markers drawn on one <canvas> (grid index hit test, clusters at x1..x2), the region names, the
// tooltip, the selection ring and pulse, a Pal's spawn areas and the overview map. Positions are fractions (0..1) of the map picture.
// The Paldex viewer (js/mapview.js) is a separate script and is not touched by this one.
window.pbWorldMap = (function () {
    const STEPS = [1, 1.5, 2, 3, 4, 6, 8, 12, 16];
    const MAX = 16;
    const LARGE_ZOOM = 2;
    const CLUSTER_ZOOM = 2;
    const CLUSTER_CELL = 28;
    const DENSE = 150;          // a category with this many markers on the map is clustered at x1..x2
    const FLY_ZOOM = 6;
    const HIT_CELL = 32;
    const ICON = 20, ICON_SELECTED = 26;

    function attach(root, dotNet, cfg) {
        const $ = function (sel) { return root.querySelector(sel); };
        const canvas = $('[data-role="canvas"]');
        const world = $('[data-role="world"]');
        const img = $('[data-role="image"]');
        const draw = $('[data-role="draw"]');
        const ctx = draw.getContext('2d');
        const coord = $('[data-role="coord"]');
        const tip = $('[data-role="tip"]');
        const mmap = $('[data-role="mini"]');
        const miniImg = $('[data-role="miniimage"]');
        const mvp = $('[data-role="miniview"]');
        const zl = $('[data-role="zoomlabel"]');
        const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const nf = function (n) { return (n < 0 ? '−' : '') + Math.abs(Math.round(n)).toLocaleString(cfg.lang); };
        const style = getComputedStyle(canvas);
        const css = function (name, fallback) { return (style.getPropertyValue(name) || '').trim() || fallback; };
        const colors = { ink: css('--ink', '#f0f0f0'), ground: css('--ground', '#151515'), accent: css('--accent', '#ff8a3d'), sunk: css('--sunk', '#101011') };
        const font = style.fontFamily || 'sans-serif';

        let zoom = 1, cx = 0.5, cy = 0.5, W = 1, H = 1;
        let map = null, enabled = new Set(), selected = null, palLayer = null, palFocus = null, hover = null;
        // Marks (T144, T148): the done markers, the mark kinds that are hidden (1 collect, 2 open, 3 defeat), whether the player's record is
        // known, and the quest givers' states (1 all done, 2 a quest not taken). Predator areas (T149): [left, top, radius, name].
        let done = new Set(), hidden = new Set(), known = false, givers = {}, areas = [], areaChosen = -1;
        let drag = null, frame = 0, largeFor = null, pulseUntil = 0, animUntil = 0, lastDraw = 0, firstDraw = null;
        let hits = new Map();
        const icons = {};

        function icon(url) {
            if (!url) return null;
            let image = icons[url];
            if (!image) {
                image = icons[url] = new Image();
                image.onload = requestDraw;
                image.src = url;
            }
            return image.complete && image.naturalWidth > 0 ? image : null;
        }

        // ---- view -----------------------------------------------------------------------------------------------------------------
        function measure() {
            const r = canvas.getBoundingClientRect();
            W = Math.max(1, r.width);
            H = Math.max(1, r.height);
            const ratio = window.devicePixelRatio || 1;
            draw.width = Math.round(W * ratio);
            draw.height = Math.round(H * ratio);
            draw.style.width = W + 'px';
            draw.style.height = H + 'px';
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
        }
        function fitSize() { return Math.max(100, Math.min(W, H) - 48); }
        function apply() {
            cx = Math.min(1, Math.max(0, cx));
            cy = Math.min(1, Math.max(0, cy));
            const size = fitSize() * zoom;
            world.style.width = size + 'px';
            world.style.height = size + 'px';
            world.style.left = (W / 2 - cx * size) + 'px';
            world.style.top = (H / 2 - cy * size) + 'px';
            canvas.classList.toggle('zoomed', zoom > 1.001);
            zl.textContent = '×' + (Math.round(zoom * 10) / 10).toString().replace('.', cfg.lang === 'tr' ? ',' : '.');
            $('[data-act="fit"]').disabled = zoom <= 1.001;
            $('[data-act="in"]').disabled = zoom >= MAX - 0.001;
            $('[data-act="out"]').disabled = zoom <= 1.001;
            if (map && map.large && largeFor !== map.large && zoom > LARGE_ZOOM) {
                largeFor = map.large;
                const wanted = map.large;
                const pre = new Image();
                pre.onload = function () { if (map && map.large === wanted) img.src = wanted; };
                pre.src = wanted;
            }
            const vw = Math.min(1, W / size), vh = Math.min(1, H / size);
            mvp.style.left = (Math.max(0, cx - vw / 2) * 100) + '%';
            mvp.style.top = (Math.max(0, cy - vh / 2) * 100) + '%';
            mvp.style.width = (vw * 100) + '%';
            mvp.style.height = (vh * 100) + '%';
            requestDraw();
        }
        // The markers follow the picture through its CSS transition: draw every frame while it runs.
        function animate(on) {
            const smooth = on && !reduce;
            world.classList.toggle('anim', smooth);
            animUntil = smooth ? performance.now() + 300 : 0;
        }
        function setView(z, x, y, smooth) {
            zoom = Math.min(MAX, Math.max(1, z));
            cx = x; cy = y;
            animate(smooth);
            apply();
        }
        function zoomTo(next, anchorX, anchorY) {
            const size = fitSize() * zoom;
            const ax = anchorX == null ? W / 2 : anchorX, ay = anchorY == null ? H / 2 : anchorY;
            const fx = cx + (ax - W / 2) / size, fy = cy + (ay - H / 2) / size;
            zoom = Math.min(MAX, Math.max(1, next));
            const nsize = fitSize() * zoom;
            cx = fx - (ax - W / 2) / nsize;
            cy = fy - (ay - H / 2) / nsize;
            if (zoom <= 1.001) { cx = 0.5; cy = 0.5; }
            animate(anchorX == null);
            apply();
        }
        function stepZoom(dir) {
            const next = dir > 0
                ? STEPS.find(function (s) { return s > zoom + 0.001; }) || MAX
                : STEPS.slice().reverse().find(function (s) { return s < zoom - 0.001; }) || 1;
            zoomTo(next);
        }

        // ---- drawing --------------------------------------------------------------------------------------------------------------
        function requestDraw() { if (!frame) frame = requestAnimationFrame(render); }

        // Where the picture is now (it may be in the middle of its transition).
        function pictureBox() {
            if (performance.now() < animUntil) {
                const w = world.getBoundingClientRect(), c = canvas.getBoundingClientRect();
                return { left: w.left - c.left, top: w.top - c.top, size: w.width };
            }
            const size = fitSize() * zoom;
            return { left: W / 2 - cx * size, top: H / 2 - cy * size, size: size };
        }
        function addHit(item) {
            const key = Math.floor(item.x / HIT_CELL) + ',' + Math.floor(item.y / HIT_CELL);
            let list = hits.get(key);
            if (!list) hits.set(key, list = []);
            list.push(item);
        }
        function glyph(category, x, y, size) {
            const image = icon(category.icon);
            ctx.beginPath();
            ctx.arc(x, y, size / 2 + 1, 0, 2 * Math.PI);
            ctx.fillStyle = image ? colors.sunk : category.color;
            ctx.fill();
            ctx.lineWidth = 1.5;
            ctx.strokeStyle = category.color;
            ctx.stroke();
            if (image) ctx.drawImage(image, x - size / 2 + 1, y - size / 2 + 1, size - 2, size - 2);
            else {
                ctx.fillStyle = '#fff';
                ctx.font = '700 ' + Math.round(size * 0.55) + 'px ' + font;
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(category.letter, x, y + 0.5);
            }
        }
        function drawPalLayer(box) {
            if (!palLayer) return;
            ctx.save();
            palLayer.forEach(function (s) {
                const x = box.left + s[0] * box.size, y = box.top + s[1] * box.size;
                // An Alpha is a point of a fixed size; a wild spawn area has its radius.
                const r = s[3] ? 8 : Math.max(3, s[2] * box.size);
                if (x < -r || y < -r || x > W + r || y > H + r) return;
                // The chosen place (Pals tab) is drawn stronger, the others fade.
                const chosen = palFocus !== null && s[4] === palFocus;
                ctx.globalAlpha = palFocus === null || chosen ? 1 : 0.4;
                ctx.beginPath();
                ctx.arc(x, y, r, 0, 2 * Math.PI);
                ctx.fillStyle = s[3] ? colors.accent : chosen ? 'rgba(255,138,61,0.32)' : 'rgba(255,138,61,0.14)';
                ctx.fill();
                ctx.lineWidth = s[3] ? 2 : chosen ? 2 : 1;
                ctx.strokeStyle = s[3] ? colors.sunk : chosen ? colors.ink : colors.accent;
                ctx.stroke();
            });
            ctx.restore();
        }
        // An opened place wears the game's "unlocked" look, a closed one the "locked" look. A collected or defeated marker is faded and
        // carries a small check; a quest giver with every quest done is faded, one with a quest to take carries a dot.
        function marker(category, point, x, y, size) {
            const isDone = done.has(point[0]);
            if (point[7] === 2) {
                const look = isDone ? category.openIcon : known ? category.lockedIcon : null;
                glyph(look ? { icon: look, color: category.color, letter: category.letter } : category, x, y, size);
                return;
            }
            const giver = givers[point[0]];
            if (!isDone) {
                ctx.globalAlpha = giver === 1 ? 0.4 : 1;
                glyph(category, x, y, size);
                ctx.globalAlpha = 1;
                if (giver === 2) {
                    ctx.beginPath();
                    ctx.arc(x + size / 2 - 1, y - size / 2 + 1, 4.5, 0, 2 * Math.PI);
                    ctx.fillStyle = '#ffd23f';
                    ctx.fill();
                    ctx.lineWidth = 1.5;
                    ctx.strokeStyle = colors.sunk;
                    ctx.stroke();
                }
                return;
            }
            ctx.globalAlpha = 0.35;
            glyph(category, x, y, size);
            ctx.globalAlpha = 1;
            const bx = x + size / 2 - 2, by = y + size / 2 - 2;
            ctx.beginPath();
            ctx.arc(bx, by, 6, 0, 2 * Math.PI);
            ctx.fillStyle = '#5fd38a';
            ctx.fill();
            ctx.lineWidth = 1.8;
            ctx.strokeStyle = colors.sunk;
            ctx.beginPath();
            ctx.moveTo(bx - 3, by);
            ctx.lineTo(bx - 0.8, by + 2.2);
            ctx.lineTo(bx + 3, by - 2);
            ctx.stroke();
        }
        // The areas where a predator may show up: red, half transparent; the chosen one stronger. All are filled as one shape, so overlapping
        // areas do not add up to a solid red.
        function drawAreas(box) {
            if (!areas.length) return;
            const circle = function (a) {
                const x = box.left + a[0] * box.size, y = box.top + a[1] * box.size, r = Math.max(4, a[2] * box.size);
                if (x < -r || y < -r || x > W + r || y > H + r) return false;
                ctx.moveTo(x + r, y);
                ctx.arc(x, y, r, 0, 2 * Math.PI);
                return true;
            };
            ctx.save();
            ctx.beginPath();
            areas.forEach(circle);
            ctx.fillStyle = 'rgba(232,66,66,0.28)';
            ctx.fill();
            const chosen = areas[areaChosen];
            ctx.beginPath();
            if (chosen && circle(chosen)) {
                ctx.fillStyle = 'rgba(232,66,66,0.3)';
                ctx.fill();
                ctx.lineWidth = 2;
                ctx.strokeStyle = colors.ink;
                ctx.stroke();
            }
            ctx.restore();
        }
        // The topmost area under a point of the canvas, or -1.
        function areaAt(x, y) {
            const box = pictureBox();
            for (let i = areas.length - 1; i >= 0; i--) {
                const a = areas[i];
                if (Math.hypot(box.left + a[0] * box.size - x, box.top + a[1] * box.size - y) <= Math.max(4, a[2] * box.size)) return i;
            }
            return -1;
        }
        // A base shows its radius from x3.
        function drawRanges(box) {
            if (zoom < 3 - 0.001) return;
            map.points.forEach(function (p) {
                if (!p[8] || !enabled.has(map.categories[p[1]].id)) return;
                ctx.beginPath();
                ctx.arc(box.left + p[2] * box.size, box.top + p[3] * box.size, p[8] * box.size, 0, 2 * Math.PI);
                ctx.lineWidth = 1.5;
                ctx.strokeStyle = map.categories[p[1]].color;
                ctx.stroke();
            });
        }
        function drawMarkers(box) {
            const cluster = zoom <= CLUSTER_ZOOM + 0.001;
            const buckets = new Map();
            let chosen = null;
            for (let i = 0; i < map.points.length; i++) {
                const p = map.points[i];
                const category = map.categories[p[1]];
                const isSelected = p[0] === selected;
                if (!isSelected && (!enabled.has(category.id) || (hidden.has(p[7]) && done.has(p[0])))) continue;
                const x = box.left + p[2] * box.size, y = box.top + p[3] * box.size;
                if (x < -ICON || y < -ICON || x > W + ICON || y > H + ICON) continue;
                if (isSelected) { chosen = { point: p, x: x, y: y, category: category }; continue; }
                if (cluster && category.count >= DENSE) {
                    const key = p[1] + ':' + Math.floor((p[2] * box.size) / CLUSTER_CELL) + ':' + Math.floor((p[3] * box.size) / CLUSTER_CELL);
                    let bucket = buckets.get(key);
                    if (!bucket) buckets.set(key, bucket = { category: category, point: p, x: 0, y: 0, left: 0, top: 0, n: 0 });
                    bucket.x += x; bucket.y += y; bucket.left += p[2]; bucket.top += p[3]; bucket.n++;
                    continue;
                }
                marker(category, p, x, y, ICON);
                addHit({ x: x, y: y, r: ICON / 2 + 2, point: p, category: category });
            }
            buckets.forEach(function (b) {
                const x = b.x / b.n, y = b.y / b.n;
                if (b.n === 1) {
                    marker(b.category, b.point, x, y, ICON);
                    addHit({ x: x, y: y, r: ICON / 2 + 2, point: b.point, category: b.category });
                    return;
                }
                ctx.beginPath();
                ctx.arc(x, y, 12, 0, 2 * Math.PI);
                ctx.fillStyle = colors.sunk;
                ctx.fill();
                ctx.lineWidth = 2;
                ctx.strokeStyle = b.category.color;
                ctx.stroke();
                ctx.fillStyle = colors.ink;
                ctx.font = '600 11px ' + font;
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(String(b.n), x, y + 0.5);
                addHit({ x: x, y: y, r: 13, cluster: b.n, left: b.left / b.n, top: b.top / b.n, category: b.category });
            });
            if (chosen) {
                marker(chosen.category, chosen.point, chosen.x, chosen.y, ICON_SELECTED);
                ctx.beginPath();
                ctx.arc(chosen.x, chosen.y, ICON_SELECTED / 2 + 5, 0, 2 * Math.PI);
                ctx.lineWidth = 2;
                ctx.strokeStyle = colors.accent;
                ctx.stroke();
                addHit({ x: chosen.x, y: chosen.y, r: ICON_SELECTED / 2 + 2, point: chosen.point, category: chosen.category });
            }
            return chosen;
        }
        // Wide regions always, sub regions from x3, places from x6; a label that would overlap an earlier (wider) one is left out.
        function drawLabels(box) {
            const placed = [];
            ctx.save();
            ctx.font = '600 13px ' + font;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.lineJoin = 'round';
            ctx.lineWidth = 6;
            ctx.strokeStyle = colors.ground;
            ctx.fillStyle = colors.ink;
            const max = zoom >= FLY_ZOOM - 0.001 ? 2 : zoom >= 3 - 0.001 ? 1 : 0;
            map.regions.forEach(function (r) {
                if (r[2] > max) return;
                const x = box.left + r[0] * box.size, y = box.top + r[1] * box.size;
                if (x < -80 || y < -20 || x > W + 80 || y > H + 20) return;
                const half = ctx.measureText(r[3]).width / 2 + 3;
                const rect = [x - half, y - 10, x + half, y + 10];
                if (placed.some(function (o) { return rect[0] < o[2] && rect[2] > o[0] && rect[1] < o[3] && rect[3] > o[1]; })) return;
                placed.push(rect);
                ctx.strokeText(r[3], x, y);
                ctx.fillText(r[3], x, y);
            });
            ctx.restore();
        }
        function render() {
            frame = 0;
            const started = performance.now();
            ctx.clearRect(0, 0, W, H);
            hits = new Map();
            if (!map) return;
            const box = pictureBox();
            drawAreas(box);
            drawRanges(box);
            drawPalLayer(box);
            const chosen = drawMarkers(box);
            drawLabels(box);
            if (chosen && pulseUntil > started) {
                const t = reduce ? 0.5 : ((2000 - (pulseUntil - started)) % 700) / 700;
                ctx.beginPath();
                ctx.arc(chosen.x, chosen.y, ICON_SELECTED / 2 + 6 + t * 18, 0, 2 * Math.PI);
                ctx.lineWidth = 3;
                ctx.strokeStyle = colors.accent;
                ctx.globalAlpha = 1 - t;
                ctx.stroke();
                ctx.globalAlpha = 1;
            }
            lastDraw = performance.now() - started;
            if (firstDraw === null && map.points.length > 0) firstDraw = lastDraw;
            if (started < animUntil || pulseUntil > started) requestDraw();
        }

        // ---- input ----------------------------------------------------------------------------------------------------------------
        function hitAt(x, y) {
            const gx = Math.floor(x / HIT_CELL), gy = Math.floor(y / HIT_CELL);
            let best = null, bestDistance = Infinity;
            for (let i = gx - 1; i <= gx + 1; i++) {
                for (let j = gy - 1; j <= gy + 1; j++) {
                    const list = hits.get(i + ',' + j);
                    if (!list) continue;
                    for (let k = 0; k < list.length; k++) {
                        const d = Math.hypot(list[k].x - x, list[k].y - y);
                        if (d <= list[k].r && d <= bestDistance) { best = list[k]; bestDistance = d; }
                    }
                }
            }
            return best;
        }
        function coordinates(left, top) {
            const wy = map.world.minY + left * (map.world.maxY - map.world.minY);
            const wx = map.world.maxX - top * (map.world.maxX - map.world.minX);
            const m = map.matrix;
            return '(' + nf(m[0] * wx + m[1] * wy + m[2]) + ', ' + nf(m[3] * wx + m[4] * wy + m[5]) + ')';
        }
        function restingCoordinate() { return map && !map.matrix ? cfg.strings.noCoords : cfg.strings.coordHint; }
        function showTip(item) {
            tip.textContent = '';
            const title = document.createElement('b');
            title.textContent = item.area ? item.area : item.cluster ? cfg.strings.cluster.replace('{0}', item.cluster) : (item.point[6] || item.category.name);
            tip.appendChild(title);
            const meta = document.createElement('span');
            meta.textContent = item.category.name;
            if (!item.area && (item.cluster || item.point[6])) tip.appendChild(meta);
            if (!item.cluster && !item.area && done.has(item.point[0])) {
                meta.textContent += ' · ' + [null, cfg.strings.collect, cfg.strings.open, cfg.strings.defeat][item.point[7]];
                if (!meta.parentNode) tip.appendChild(meta);
            }
            if (!item.cluster && !item.area && item.point[4] != null) {
                const c = document.createElement('span');
                c.className = 'num';
                c.textContent = '(' + nf(item.point[4]) + ', ' + nf(item.point[5]) + ')';
                tip.appendChild(c);
            }
            tip.style.left = item.x + 'px';
            tip.style.top = item.y + 'px';
            tip.hidden = false;
        }
        function onWheel(e) {
            e.preventDefault();
            const r = canvas.getBoundingClientRect();
            zoomTo(zoom * (e.deltaY < 0 ? 1.25 : 1 / 1.25), e.clientX - r.left, e.clientY - r.top);
        }
        function onDown(e) {
            if (e.button !== 0 || e.target.closest('.zc, .mmap, .wm-banner')) return;
            drag = { x: e.clientX, y: e.clientY, cx: cx, cy: cy, moved: false };
            canvas.setPointerCapture(e.pointerId);
            canvas.classList.add('grabbing');
            animate(false);
        }
        function onMove(e) {
            if (!map) return;
            const r = canvas.getBoundingClientRect();
            const x = e.clientX - r.left, y = e.clientY - r.top;
            const size = fitSize() * zoom;
            const fx = cx + (x - W / 2) / size, fy = cy + (y - H / 2) / size;
            coord.textContent = map.matrix && fx >= 0 && fx <= 1 && fy >= 0 && fy <= 1
                ? cfg.strings.cursor + ' ' + coordinates(fx, fy) : restingCoordinate();
            if (drag) {
                if (Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) > 3) drag.moved = true;
                cx = drag.cx - (e.clientX - drag.x) / size;
                cy = drag.cy - (e.clientY - drag.y) / size;
                apply();
                tip.hidden = true;
                return;
            }
            hover = null;
            if (!e.target.closest('.zc, .mmap')) {
                hover = hitAt(x, y);
                if (!hover) {
                    const under = areaAt(x, y);
                    if (under >= 0) hover = { x: x, y: y, area: areas[under][3] };
                }
            }
            canvas.classList.toggle('pointing', !!hover);
            if (hover) showTip(hover); else tip.hidden = true;
        }
        function onUp(e) {
            if (!drag) return;
            const moved = drag.moved;
            drag = null;
            canvas.classList.remove('grabbing');
            try { canvas.releasePointerCapture(e.pointerId); } catch (x) { }
            if (moved) return;
            const r = canvas.getBoundingClientRect();
            const item = hitAt(e.clientX - r.left, e.clientY - r.top);
            if (!item) {
                const under = areaAt(e.clientX - r.left, e.clientY - r.top);
                if (under >= 0) { tip.hidden = true; dotNet.invokeMethodAsync('OnAreaPicked', under); }
                return;
            }
            tip.hidden = true;
            if (item.cluster) setView(Math.min(MAX, Math.max(zoom * 2, 3)), item.left, item.top, true);
            else dotNet.invokeMethodAsync('OnPointPicked', item.point[0]);
        }
        function onKey(e) {
            if (e.ctrlKey || e.altKey || e.metaKey) return;
            const size = fitSize() * zoom;
            const stepX = (e.shiftKey ? 0.5 : 0.1) * W / size, stepY = (e.shiftKey ? 0.5 : 0.1) * H / size;
            switch (e.key) {
                case '+': case '=': stepZoom(1); break;
                case '-': case '_': stepZoom(-1); break;
                case '0': setView(1, 0.5, 0.5, true); break;
                case 'ArrowLeft': cx -= stepX; animate(false); apply(); break;
                case 'ArrowRight': cx += stepX; animate(false); apply(); break;
                case 'ArrowUp': cy -= stepY; animate(false); apply(); break;
                case 'ArrowDown': cy += stepY; animate(false); apply(); break;
                default: return;
            }
            e.preventDefault();
        }
        function onMini(e) {
            const r = mmap.getBoundingClientRect();
            const move = function (ev) {
                cx = (ev.clientX - r.left) / r.width;
                cy = (ev.clientY - r.top) / r.height;
                animate(false);
                apply();
            };
            move(e);
            mmap.setPointerCapture(e.pointerId);
            const up = function (ev) {
                mmap.removeEventListener('pointermove', move);
                mmap.removeEventListener('pointerup', up);
                try { mmap.releasePointerCapture(ev.pointerId); } catch (x) { }
            };
            mmap.addEventListener('pointermove', move);
            mmap.addEventListener('pointerup', up);
            e.stopPropagation();
        }
        function onButton(e) {
            const b = e.target.closest('[data-act]');
            if (!b || b.disabled) return;
            if (b.dataset.act === 'in') stepZoom(1);
            else if (b.dataset.act === 'out') stepZoom(-1);
            else setView(1, 0.5, 0.5, true);
            canvas.focus();
        }
        // A right click on a collectible marker marks it (or takes the mark back).
        function onContext(e) {
            e.preventDefault();
            if (e.target.closest('.zc, .mmap, .wm-banner')) return;
            const r = canvas.getBoundingClientRect();
            const item = hitAt(e.clientX - r.left, e.clientY - r.top);
            if (item && !item.cluster && item.point[7] > 0) { tip.hidden = true; dotNet.invokeMethodAsync('OnPointToggled', item.point[0]); }
        }
        function frameSpots(spots, maxZoom) {
            let x0 = 1, x1 = 0, y0 = 1, y1 = 0;
            spots.forEach(function (s) { x0 = Math.min(x0, s[0]); x1 = Math.max(x1, s[0]); y0 = Math.min(y0, s[1]); y1 = Math.max(y1, s[1]); });
            const fit = fitSize();
            const z = Math.min(maxZoom, Math.max(1, Math.min(W / (Math.max(0.002, (x1 - x0) * 1.3) * fit), H / (Math.max(0.002, (y1 - y0) * 1.3) * fit))));
            setView(z, z <= 1.001 ? 0.5 : (x0 + x1) / 2, z <= 1.001 ? 0.5 : (y0 + y1) / 2, true);
        }
        function onLeave() { tip.hidden = true; hover = null; coord.textContent = restingCoordinate(); }

        const observer = new ResizeObserver(function () { measure(); apply(); });
        observer.observe(canvas);
        canvas.addEventListener('wheel', onWheel, { passive: false });
        canvas.addEventListener('pointerdown', onDown);
        canvas.addEventListener('pointermove', onMove);
        canvas.addEventListener('pointerup', onUp);
        canvas.addEventListener('pointercancel', onUp);
        canvas.addEventListener('pointerleave', onLeave);
        canvas.addEventListener('keydown', onKey);
        canvas.addEventListener('contextmenu', onContext);
        mmap.addEventListener('pointerdown', onMini);
        $('[data-role="zoomctl"]').addEventListener('click', onButton);
        measure();

        // The handle is also kept on the page element, so measurements can be read from the developer tools.
        return root.pbWorldMapHandle = {
            // Shows another map: picture, markers [id, category index, left, top, mapX, mapY, name, mark kind, base radius],
            // regions [left, top, level, name].
            setMap: function (next, on) {
                const changed = !map || map.id !== next.id;
                map = next;
                map.categories.forEach(function (c) { c.count = 0; });
                map.points.forEach(function (p) { map.categories[p[1]].count++; });
                map.regions.sort(function (a, b) { return a[2] - b[2]; });
                enabled = new Set(on);
                if (changed) {
                    largeFor = null;
                    if (next.image) img.src = next.image;
                    if (next.image) miniImg.src = next.image;
                    img.hidden = miniImg.hidden = !next.image;
                    zoom = 1; cx = 0.5; cy = 0.5;
                    animate(false);
                }
                coord.textContent = restingCoordinate();
                tip.hidden = true;
                apply();
            },
            setEnabled: function (on) { enabled = new Set(on); tip.hidden = true; requestDraw(); },
            setMarks: function (ids, hiddenKinds, recordKnown, giverStates) {
                done = new Set(ids); hidden = new Set(hiddenKinds); known = !!recordKnown; givers = giverStates || {};
                tip.hidden = true; requestDraw();
            },
            setAreas: function (next, chosen) { areas = next || []; areaChosen = chosen; tip.hidden = true; requestDraw(); },
            fit: function () { setView(1, 0.5, 0.5, true); },
            select: function (id) { selected = id; pulseUntil = 0; requestDraw(); },
            // Flies to a marker (x6) with a two second pulse, or to any position of the picture.
            flyTo: function (id, left, top) {
                selected = id;
                setView(FLY_ZOOM, left, top, true);
                pulseUntil = id ? performance.now() + 2000 : 0;
                requestDraw();
            },
            // A species' spawn areas [left, top, radius, alpha, place index]; frames them when asked to.
            setPalLayer: function (spots, frameThem) {
                palLayer = spots && spots.length ? spots : null;
                palFocus = null;
                if (palLayer && frameThem) frameSpots(palLayer, 8);
                requestDraw();
            },
            // Flies to the areas of one place and draws them stronger.
            focusPal: function (index) {
                const spots = (palLayer || []).filter(function (s) { return s[4] === index; });
                if (!spots.length) return;
                palFocus = index;
                frameSpots(spots, FLY_ZOOM);
                requestDraw();
            },
            canvasFocus: function () { canvas.focus(); },
            // Measurements for the report: the first draw with markers and the latest one, in milliseconds.
            stats: function () { render(); return { first: firstDraw, last: lastDraw }; },
            dispose: function () {
                if (frame) cancelAnimationFrame(frame);
                frame = 0;
                map = null;
                observer.disconnect();
                canvas.removeEventListener('wheel', onWheel);
                canvas.removeEventListener('pointerdown', onDown);
                canvas.removeEventListener('pointermove', onMove);
                canvas.removeEventListener('pointerup', onUp);
                canvas.removeEventListener('pointercancel', onUp);
                canvas.removeEventListener('pointerleave', onLeave);
                canvas.removeEventListener('keydown', onKey);
                canvas.removeEventListener('contextmenu', onContext);
                mmap.removeEventListener('pointerdown', onMini);
            }
        };
    }

    return { attach: attach };
})();
