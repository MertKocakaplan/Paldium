// Full screen map viewer (T104, screens/paldex.md "Harita görüntüleyici"). The Razor component renders the dialog, the list and the static
// controls; this script owns the canvas: zoom (x1..x16), pan, the points, the spawn radius, the tooltip, the off-screen Alpha chips and the
// overview map. Positions are fractions (0..1) of the picture; the picture and the point layer share one "world" element. A species on both
// maps (T140) has two entries in cfg.maps; showMap switches the picture, the grid and the points, and a place of the other map switches too.
window.pbMapView = (function () {
    const STEPS = [1, 1.5, 2, 3, 4, 6, 8, 12, 16];
    const MAX = 16;
    const RADIUS_ZOOM = 4;
    const LARGE_ZOOM = 2;

    function attach(dialog, dotNet, cfg) {
        const $ = function (sel) { return dialog.querySelector(sel); };
        const canvas = $('[data-role="canvas"]');
        const world = $('[data-role="world"]');
        const img = $('[data-role="image"]');
        const layer = $('[data-role="layer"]');
        const chips = $('[data-role="chips"]');
        const coord = $('[data-role="coord"]');
        const tip = $('[data-role="tip"]');
        const mmap = $('[data-role="mini"]');
        const mvp = $('[data-role="miniview"]');
        const miniImg = $('[data-role="miniimage"]');
        const zl = $('[data-role="zoomlabel"]');
        const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const nf = function (n) { return (n < 0 ? '−' : '') + Math.abs(Math.round(n)).toLocaleString(cfg.lang); };

        let zoom = 1, cx = 0.5, cy = 0.5, W = 1, H = 1, selected = -1, largeFor = null;
        let drag = null;
        let map = null;

        // ---- points ---------------------------------------------------------------------------------------------------------------
        let dotEls = [];
        let alphas = [];
        function build() {
        layer.textContent = '';
        world.querySelectorAll('.gl').forEach(function (l) { l.remove(); });
        mmap.querySelectorAll('i').forEach(function (m) { m.remove(); });
        dotEls = [];
        alphas = [];
        cfg.places.forEach(function (place, pi) {
            if (place.map !== map.id) return;
            place.dots.forEach(function (d, di) {
                const i = document.createElement('i');
                i.className = d[3] ? 'a' : (d[6] ? 'w n' : 'w');
                i.dataset.p = pi;
                i.dataset.d = di;
                i.style.left = (d[0] * 100) + '%';
                i.style.top = (d[1] * 100) + '%';
                layer.appendChild(i);
                dotEls.push({ el: i, place: pi });
                if (!d[3] && d[2] > 0) {
                    const ar = document.createElement('i');
                    ar.className = 'ar';
                    ar.dataset.p = pi;
                    ar.style.left = (d[0] * 100) + '%';
                    ar.style.top = (d[1] * 100) + '%';
                    ar.style.width = (d[2] * 200) + '%';
                    ar.style.height = (d[2] * 200) + '%';
                    layer.insertBefore(ar, layer.firstChild);
                    dotEls.push({ el: ar, place: pi });
                }
                if (d[3]) alphas.push({ place: pi, left: d[0], top: d[1] });
            });
        });
        alphas.forEach(function (a) {
            const m = document.createElement('i');
            m.style.left = (a.left * 100) + '%';
            m.style.top = (a.top * 100) + '%';
            mmap.insertBefore(m, mvp);
        });
        // The 500-unit in-game coordinate grid.
        map.gridX.forEach(function (f) {
            const l = document.createElement('b');
            l.className = 'gl gx';
            l.style.left = (f * 100) + '%';
            world.insertBefore(l, layer);
        });
        map.gridY.forEach(function (f) {
            const l = document.createElement('b');
            l.className = 'gl gy';
            l.style.top = (f * 100) + '%';
            world.insertBefore(l, layer);
        });
        }

        // Switches the picture to a map (no-op for the map already shown); the view starts fitted.
        function useMap(id) {
            const next = cfg.maps.find(function (m) { return m.id === id; }) || cfg.maps[0];
            if (!next || map === next) return false;
            map = next;
            largeFor = null;
            if (map.image) { img.src = map.image; miniImg.src = map.image; }
            img.hidden = miniImg.hidden = !map.image;
            build();
            tip.hidden = true;
            coord.textContent = restingCoordinate();
            return true;
        }
        function restingCoordinate() { return map.matrix ? cfg.strings.coordHint : cfg.strings.noCoords; }

        // ---- view -----------------------------------------------------------------------------------------------------------------
        function measure() {
            const r = canvas.getBoundingClientRect();
            W = Math.max(1, r.width);
            H = Math.max(1, r.height);
        }
        function fitSize() { return Math.max(100, Math.min(W, H) - 48); }
        function clampCenter() {
            cx = Math.min(1, Math.max(0, cx));
            cy = Math.min(1, Math.max(0, cy));
        }
        function apply() {
            clampCenter();
            const size = fitSize() * zoom;
            world.style.width = size + 'px';
            world.style.height = size + 'px';
            world.style.left = (W / 2 - cx * size) + 'px';
            world.style.top = (H / 2 - cy * size) + 'px';
            canvas.classList.toggle('zoomed', zoom > 1.001);
            canvas.classList.toggle('z4', zoom >= RADIUS_ZOOM - 0.001);
            zl.textContent = '×' + (Math.round(zoom * 10) / 10).toString().replace('.', cfg.lang === 'tr' ? ',' : '.');
            $('[data-act="fit"]').disabled = zoom <= 1.001;
            $('[data-act="in"]').disabled = zoom >= MAX - 0.001;
            $('[data-act="out"]').disabled = zoom <= 1.001;
            if (map.large && largeFor !== map.id && zoom > LARGE_ZOOM) {
                largeFor = map.id;
                const wanted = map;
                const pre = new Image();
                pre.onload = function () { if (map === wanted) img.src = wanted.large; };
                pre.src = wanted.large;
            }
            // The overview map: the visible part of the picture.
            const vw = Math.min(1, W / size), vh = Math.min(1, H / size);
            mvp.style.left = (Math.max(0, cx - vw / 2) * 100) + '%';
            mvp.style.top = (Math.max(0, cy - vh / 2) * 100) + '%';
            mvp.style.width = (Math.min(1, vw) * 100) + '%';
            mvp.style.height = (Math.min(1, vh) * 100) + '%';
            renderChips(size);
        }
        function setView(z, x, y, animate) {
            zoom = Math.min(MAX, Math.max(1, z));
            cx = x; cy = y;
            world.classList.toggle('anim', !!animate && !reduce);
            apply();
        }
        function stepZoom(dir, anchorX, anchorY) {
            let next;
            if (dir > 0) next = STEPS.find(function (s) { return s > zoom + 0.001; }) || MAX;
            else next = STEPS.slice().reverse().find(function (s) { return s < zoom - 0.001; }) || 1;
            zoomTo(next, anchorX, anchorY);
        }
        function zoomTo(next, anchorX, anchorY) {
            const size = fitSize() * zoom;
            const ax = anchorX == null ? W / 2 : anchorX, ay = anchorY == null ? H / 2 : anchorY;
            const fx = cx + (ax - W / 2) / size, fy = cy + (ay - H / 2) / size;
            zoom = Math.min(MAX, Math.max(1, next));
            const nsize = fitSize() * zoom;
            cx = fx - (ax - W / 2) / nsize;
            cy = fy - (ay - H / 2) / nsize;
            world.classList.toggle('anim', !reduce);
            if (zoom <= 1.001) { cx = 0.5; cy = 0.5; }
            apply();
        }

        // ---- places ---------------------------------------------------------------------------------------------------------------
        function focusPlace(index) {
            selected = index;
            const place = index < 0 ? null : cfg.places[index];
            if (place && place.map) useMap(place.map);
            canvas.classList.toggle('hasSel', index >= 0);
            dotEls.forEach(function (d) { d.el.classList.toggle('sel', d.place === index); });
            if (index < 0) { setView(1, 0.5, 0.5, true); return; }
            if (!place || !place.map || place.dots.length === 0) return;
            let x0 = 1, x1 = 0, y0 = 1, y1 = 0;
            place.dots.forEach(function (d) { x0 = Math.min(x0, d[0]); x1 = Math.max(x1, d[0]); y0 = Math.min(y0, d[1]); y1 = Math.max(y1, d[1]); });
            const bw = Math.max(0.002, (x1 - x0) * 1.3), bh = Math.max(0.002, (y1 - y0) * 1.3);
            const fit = fitSize();
            const z = Math.min(12, Math.max(1, Math.min(W / (bw * fit), H / (bh * fit))));
            setView(z, (x0 + x1) / 2, (y0 + y1) / 2, true);
        }

        // ---- Alpha chips ----------------------------------------------------------------------------------------------------------
        function renderChips(size) {
            chips.textContent = '';
            alphas.forEach(function (a) {
                const px = W / 2 + (a.left - cx) * size, py = H / 2 + (a.top - cy) * size;
                if (px >= 0 && px <= W && py >= 0 && py <= H) return;
                const dx = px - W / 2, dy = py - H / 2;
                const k = Math.min((W / 2 - 48) / Math.max(1e-6, Math.abs(dx)), (H / 2 - 24) / Math.max(1e-6, Math.abs(dy)));
                const bx = W / 2 + dx * k, by = H / 2 + dy * k;
                const units = map.mapUnits == null ? null : Math.round(Math.hypot((a.left - cx), (a.top - cy)) * map.mapUnits);
                const b = document.createElement('button');
                b.type = 'button';
                b.className = 'offs';
                b.style.left = bx + 'px';
                b.style.top = by + 'px';
                const arrow = document.createElement('span');
                arrow.textContent = '↑';
                arrow.style.display = 'inline-block';
                arrow.style.transform = 'rotate(' + (Math.atan2(dy, dx) * 180 / Math.PI + 90) + 'deg)';
                const label = document.createElement('span');
                label.textContent = units == null ? cfg.strings.alpha : cfg.strings.offscreen.replace('{0}', nf(units));
                b.appendChild(arrow);
                b.appendChild(label);
                b.addEventListener('click', function (e) { e.stopPropagation(); setView(4, a.left, a.top, true); });
                b.addEventListener('pointerdown', function (e) { e.stopPropagation(); });
                chips.appendChild(b);
                const cw = b.offsetWidth;
                b.style.left = Math.min(Math.max(bx, cw / 2 + 8), W - cw / 2 - 8) + 'px';
            });
        }

        // ---- input ----------------------------------------------------------------------------------------------------------------
        function onWheel(e) {
            e.preventDefault();
            const r = canvas.getBoundingClientRect();
            const factor = e.deltaY < 0 ? 1.25 : 1 / 1.25;
            zoomTo(zoom * factor, e.clientX - r.left, e.clientY - r.top);
        }
        function onDown(e) {
            if (e.button !== 0 || e.target.closest('.zc, .mmap, .offs')) return;
            drag = { x: e.clientX, y: e.clientY, cx: cx, cy: cy, moved: false };
            canvas.setPointerCapture(e.pointerId);
            canvas.classList.add('grabbing');
            world.classList.remove('anim');
        }
        function onMove(e) {
            const r = canvas.getBoundingClientRect();
            const size = fitSize() * zoom;
            const fx = cx + (e.clientX - r.left - W / 2) / size, fy = cy + (e.clientY - r.top - H / 2) / size;
            if (map.matrix && fx >= 0 && fx <= 1 && fy >= 0 && fy <= 1) {
                const wy = map.world.minY + fx * (map.world.maxY - map.world.minY);
                const wx = map.world.maxX - fy * (map.world.maxX - map.world.minX);
                const m = map.matrix;
                coord.textContent = cfg.strings.cursor + ' (' + nf(m[0] * wx + m[1] * wy + m[2]) + ', ' + nf(m[3] * wx + m[4] * wy + m[5]) + ')';
            } else coord.textContent = restingCoordinate();
            if (drag) {
                if (Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) > 3) drag.moved = true;
                cx = drag.cx - (e.clientX - drag.x) / size;
                cy = drag.cy - (e.clientY - drag.y) / size;
                apply();
                tip.hidden = true;
                return;
            }
            const t = e.target.closest('i[data-p]');
            if (t && !t.classList.contains('ar')) showTip(t, e.clientX - r.left, e.clientY - r.top);
            else tip.hidden = true;
        }
        function onUp(e) {
            if (!drag) return;
            const moved = drag.moved;
            drag = null;
            canvas.classList.remove('grabbing');
            try { canvas.releasePointerCapture(e.pointerId); } catch (x) { }
            const hit = !moved ? document.elementFromPoint(e.clientX, e.clientY) : null;
            const t = hit && hit.closest ? hit.closest('i[data-p]') : null;
            if (t && !t.classList.contains('ar')) dotNet.invokeMethodAsync('OnPlacePicked', Number(t.dataset.p));
        }
        function showTip(el, x, y) {
            const place = cfg.places[Number(el.dataset.p)];
            const dot = place.dots[Number(el.dataset.d)];
            tip.textContent = '';
            const b = document.createElement('b');
            b.textContent = dot[3] ? cfg.strings.alpha : (dot[6] ? cfg.strings.nightArea : cfg.strings.area);
            const meta = document.createElement('span');
            meta.textContent = place.meta;
            tip.appendChild(b);
            tip.appendChild(meta);
            if (dot[4] != null) {
                const c = document.createElement('span');
                c.className = 'num';
                c.textContent = '(' + nf(dot[4]) + ', ' + nf(dot[5]) + ')';
                tip.appendChild(c);
            }
            tip.style.left = x + 'px';
            tip.style.top = y + 'px';
            tip.hidden = false;
        }
        function onKey(e) {
            if (e.ctrlKey || e.altKey || e.metaKey) return;
            const size = fitSize() * zoom;
            const stepX = (e.shiftKey ? 0.5 : 0.1) * W / size, stepY = (e.shiftKey ? 0.5 : 0.1) * H / size;
            switch (e.key) {
                case '+': case '=': stepZoom(1); break;
                case '-': case '_': stepZoom(-1); break;
                case '0': setView(1, 0.5, 0.5, true); break;
                case 'ArrowLeft': cx -= stepX; world.classList.remove('anim'); apply(); break;
                case 'ArrowRight': cx += stepX; world.classList.remove('anim'); apply(); break;
                case 'ArrowUp': cy -= stepY; world.classList.remove('anim'); apply(); break;
                case 'ArrowDown': cy += stepY; world.classList.remove('anim'); apply(); break;
                default: return;
            }
            e.preventDefault();
            e.stopPropagation();
        }
        function onMini(e) {
            const r = mmap.getBoundingClientRect();
            const move = function (ev) {
                cx = (ev.clientX - r.left) / r.width;
                cy = (ev.clientY - r.top) / r.height;
                world.classList.remove('anim');
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
        function onLeave() { tip.hidden = true; coord.textContent = restingCoordinate(); }
        const ro = new ResizeObserver(function () { measure(); apply(); });
        ro.observe(canvas);

        canvas.addEventListener('wheel', onWheel, { passive: false });
        canvas.addEventListener('pointerdown', onDown);
        canvas.addEventListener('pointermove', onMove);
        canvas.addEventListener('pointerup', onUp);
        canvas.addEventListener('pointercancel', onUp);
        canvas.addEventListener('pointerleave', onLeave);
        canvas.addEventListener('keydown', onKey);
        // Esc and the arrows belong to the viewer: the page behind (back to the list, previous or next species) must not see them.
        const guard = function (e) { if (e.key === 'Escape' || e.key === 'ArrowLeft' || e.key === 'ArrowRight') e.stopPropagation(); };
        dialog.addEventListener('keydown', guard);
        mmap.addEventListener('pointerdown', onMini);
        $('[data-role="zoomctl"]').addEventListener('click', onButton);

        measure();
        useMap(cfg.map);
        apply();

        return {
            focusPlace: focusPlace,
            showMap: function (id) {
                if (!useMap(id)) return;
                selected = -1;
                canvas.classList.remove('hasSel');
                setView(1, 0.5, 0.5, false);
            },
            canvasFocus: function () { canvas.focus(); },
            dispose: function () {
                ro.disconnect();
                canvas.removeEventListener('wheel', onWheel);
                canvas.removeEventListener('pointerdown', onDown);
                canvas.removeEventListener('pointermove', onMove);
                canvas.removeEventListener('pointerup', onUp);
                canvas.removeEventListener('pointercancel', onUp);
                canvas.removeEventListener('pointerleave', onLeave);
                canvas.removeEventListener('keydown', onKey);
                dialog.removeEventListener('keydown', guard);
                mmap.removeEventListener('pointerdown', onMini);
            }
        };
    }

    return { attach: attach };
})();
